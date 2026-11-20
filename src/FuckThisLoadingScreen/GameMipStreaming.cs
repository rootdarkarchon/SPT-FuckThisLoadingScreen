using System;
using System.Reflection;
using HarmonyLib;

namespace FuckThisLoadingScreen
{
    /// <summary>
    /// EFT's loaded mip streaming setting: <c>Singleton&lt;SettingsManager&gt;.Instance.Graphics.Settings.MipStreaming.Value</c>.
    /// Read from the live settings object, so SPT's settings folder redirect and EFT's fallbacks and defaults apply as in the game.
    /// </summary>
    internal sealed class GameMipStreaming
    {
        private readonly Func<bool> _loaded;
        private readonly Func<object> _manager;
        private readonly FieldInfo _graphics, _group, _setting;
        private readonly PropertyInfo _value;

        private GameMipStreaming(Func<bool> loaded, Func<object> manager, FieldInfo graphics, FieldInfo group, FieldInfo setting, PropertyInfo value)
        {
            _loaded = loaded;
            _manager = manager;
            _graphics = graphics;
            _group = group;
            _setting = setting;
            _value = value;
        }

        /// <summary>Resolves the EFT members; throws when any of them is missing or has another shape.</summary>
        internal static GameMipStreaming Bind(Func<string, Type> find)
        {
            Type manager = find("EFT.Settings.SettingsManager");
            Type singleton = find("Comfort.Common.Singleton`1").MakeGenericType(manager);
            MethodInfo instantiated = AccessTools.PropertyGetter(singleton, "Instantiated"), instance = AccessTools.PropertyGetter(singleton, "Instance");
            if (instantiated?.ReturnType != typeof(bool) || instance == null)
                throw new MissingMemberException("Singleton<EFT.Settings.SettingsManager>.Instantiated/Instance ABI");
            FieldInfo graphics = AccessTools.Field(manager, "Graphics") ?? throw new MissingFieldException("EFT.Settings.SettingsManager", "Graphics");
            FieldInfo group = AccessTools.Field(graphics.FieldType, "Settings");
            if (group?.FieldType.FullName != "EFT.Settings.Graphics.GraphicsSettingsGroup")
                throw new MissingFieldException(graphics.FieldType.FullName, "Settings (GraphicsSettingsGroup)");
            FieldInfo setting = AccessTools.Field(group.FieldType, "MipStreaming") ?? throw new MissingFieldException(group.FieldType.FullName, "MipStreaming");
            PropertyInfo value = AccessTools.Property(setting.FieldType, "Value");
            if (value?.PropertyType != typeof(bool) || value.GetGetMethod() == null)
                throw new MissingMemberException(setting.FieldType.FullName + ".Value (bool)");
            return new GameMipStreaming((Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), instantiated),
                (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), instance), graphics, group, setting, value);
        }

        /// <summary>True once EFT has loaded its settings; <c>InstantiateSingleton</c> registers the manager only after <c>Load</c>.</summary>
        internal bool Loaded() => _loaded();

        /// <summary>The loaded MipStreaming value. Call only after <see cref="Loaded"/> returned true.</summary>
        internal bool Enabled() => (bool)_value.GetValue(_setting.GetValue(_group.GetValue(_graphics.GetValue(_manager()))), null);
    }
}
