using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FuckThisLoadingScreen
{
    /// <summary>
    /// EFT and Unity readings for the streaming budget measurement. Every member is bound once in <see cref="Bind"/>;
    /// the per-sample path (<see cref="InRaid"/>, <see cref="TryRead"/>, <see cref="MapBudget"/>) does not allocate in steady state.
    /// </summary>
    internal sealed class StreamingBudgetGame
    {
        private delegate void VramQuery(out ulong totalVram, out ulong localBudget, out ulong localCurrentUsage);

        private readonly Func<bool> _inRaidStatus, _worldInstantiated;
        private readonly Func<object> _world, _camera;
        private readonly Func<float> _nativeBudget;
        private readonly PropertyInfo _locationId;
        private readonly MethodInfo _vramMethod;
        private object _boundCamera;
        private VramQuery _vram;

        private StreamingBudgetGame(Func<bool> inRaidStatus, Func<bool> worldInstantiated, Func<object> world, Func<object> camera,
            Func<float> nativeBudget, PropertyInfo locationId, MethodInfo vramMethod)
        {
            _inRaidStatus = inRaidStatus;
            _worldInstantiated = worldInstantiated;
            _world = world;
            _camera = camera;
            _nativeBudget = nativeBudget;
            _locationId = locationId;
            _vramMethod = vramMethod;
        }

        /// <summary>Resolves the EFT members; throws when any of them is missing or has another shape.</summary>
        internal static StreamingBudgetGame Bind(Func<string, Type> find, Guid module)
        {
            Type status = find("EFT.InGameStatus"), camera = find("EFT.CameraControl.CameraManager"),
                world = find("EFT.GameWorld"), graphics = find("EFT.Settings.Graphics.GraphicsSettingsController");
            if (status.Module.ModuleVersionId != module)
                throw new NotSupportedException("Streaming budget measurement requires the supported EFT module");
            FieldInfo inRaid = Field(status, "InRaid", typeof(bool)), instance = Field(camera, "instance", camera),
                budget = Field(graphics, "MipStreamingMemoryBudget", typeof(float));
            MethodInfo vram = AccessTools.DeclaredMethod(camera, "GetVRamUsage");
            ParameterInfo[] parameters = vram?.GetParameters();
            if (vram == null || vram.IsStatic || vram.ReturnType != typeof(void) || parameters.Length != 3 ||
                parameters.Any(p => !p.IsOut || p.ParameterType != typeof(ulong).MakeByRefType()))
                throw new MissingMethodException("CameraManager.GetVRamUsage(out ulong, out ulong, out ulong) ABI");
            Type singleton = find("Comfort.Common.Singleton`1").MakeGenericType(world);
            MethodInfo instantiated = AccessTools.PropertyGetter(singleton, "Instantiated"), worldInstance = AccessTools.PropertyGetter(singleton, "Instance");
            PropertyInfo location = AccessTools.Property(world, "LocationId");
            if (instantiated?.ReturnType != typeof(bool) || worldInstance == null || location?.PropertyType != typeof(string))
                throw new MissingMemberException("Singleton<GameWorld>.Instantiated/Instance and GameWorld.LocationId ABI");
            return new StreamingBudgetGame(Static<bool>(inRaid), (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), instantiated),
                (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), worldInstance), Static<object>(instance), Static<float>(budget), location, vram);
        }

        /// <summary>True during a raid with a live GameWorld; false in the hideout, the menu and while a raid is still being prepared.</summary>
        internal bool InRaid() => _inRaidStatus() && _worldInstantiated();

        /// <summary>
        /// EFT's selected map budget: the accounting's view when it is active, otherwise EFT's static field.
        /// Unaffected by a Streaming Budget Override.
        /// </summary>
        internal float MapBudget()
        {
            MipBudgetAccounting accounting = MipBudgetAccounting.Active;
            return accounting != null && accounting.State == "active" ? accounting.BaseBudget : _nativeBudget();
        }

        /// <summary>EFT's location id for the raid, falling back to the active scene name.</summary>
        internal string MapName()
        {
            object world = _worldInstantiated() ? _world() : null;
            string id = world == null ? null : (string)_locationId.GetValue(world, null);
            return string.IsNullOrEmpty(id) ? SceneManager.GetActiveScene().name : id;
        }

        /// <summary>Reads the Unity texture counters and the DXGI query; false when the query reports no budget.</summary>
        internal bool TryRead(out StreamingBudgetSample sample)
        {
            sample = default;
            object camera = _camera();
            if (camera == null)
                return false;
            if (!ReferenceEquals(camera, _boundCamera))
            {
                // CameraManager is recreated rarely; the closed delegate is rebuilt only then.
                _vram = (VramQuery)Delegate.CreateDelegate(typeof(VramQuery), camera, _vramMethod);
                _boundCamera = camera;
            }

            _vram(out ulong total, out ulong budget, out ulong usage);
            if (budget == 0)
                return false;
            sample = new StreamingBudgetSample(Texture.desiredTextureMemory, Texture.nonStreamingTextureMemory, Texture.currentTextureMemory, total, budget, usage);
            return true;
        }

        private static FieldInfo Field(Type type, string name, Type expected)
        {
            FieldInfo field = AccessTools.Field(type, name) ?? throw new MissingFieldException(type.FullName, name);
            if (!field.IsStatic || field.FieldType != expected)
                throw new MissingFieldException(type.FullName + "." + name + " ABI");
            return field;
        }

        /// <summary>Compiled static-field getter: no boxing of value types per read.</summary>
        private static Func<T> Static<T>(FieldInfo field)
        {
            Expression value = Expression.Field(null, field);
            if (field.FieldType != typeof(T))
                value = Expression.Convert(value, typeof(T));
            return Expression.Lambda<Func<T>>(value).Compile();
        }
    }
}
