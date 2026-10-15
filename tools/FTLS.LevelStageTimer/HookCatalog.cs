using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace FTLS.LevelStageTimer
{
    internal sealed class HookSpec
    {
        public string Type, Method, Stage, Kind;
        public int Parameters;
        public bool Root, Fika;
        public int MaxCalls;
        public bool Aggregate;
        public string OptionalGroupType;
        public string Parameter1Type;
        public HookSpec(string type, string method, int parameters, string stage, string kind = "work", bool root = false, bool fika = false,
            int maxCalls = 0, bool aggregate = false, string optionalGroupType = null, string parameter1Type = null)
        { Type = type; Method = method; Parameters = parameters; Stage = stage; Kind = kind; Root = root; Fika = fika;
          MaxCalls = maxCalls; Aggregate = aggregate; OptionalGroupType = optionalGroupType; Parameter1Type = parameter1Type; }
    }

    internal static class HookCatalog
    {
        // Fixed method list; no per-item/bundle/frame patches. Repeated detail calls have per-capture limits.
        public static readonly HookSpec[] Specs = {
            new HookSpec("EFT.TarkovApplication", "LocalGameMatching", 2, "raid.local", root: true),
            new HookSpec("EFT.TarkovApplication", "NetworkGameMatching", 2, "raid.network", root: true),
            new HookSpec("EFT.TarkovApplication", "GameReconnect", 5, "raid.reconnect", root: true),
            new HookSpec("EFT.TarkovApplication", "LoadMapAndData", 4, "map.total"),
            new HookSpec("EFT.GameWorld", "InitLevel", 6, "world.initialize"),
            new HookSpec("EFT.LoadScenesFromPresetOperation", "LoadPresetAsync", 1, "scenes.preset"),
            new HookSpec("EFT.AssetsManager.LoadSceneOperation", "ActivateScene", 0, "scene.activation-request", maxCalls: 64),
            new HookSpec("Koenigz.PerfectCulling.EFT.PerfectCullingCrossSceneSampler", "InitializeAutoCulling", 2, "culling.initialize"),
            new HookSpec("Audio.SpatialSystem.SpatialAudioSystem", "Initialize", 2, "audio.initialize"),
            new HookSpec("EFT.TarkovApplication", "GamePrepare", 1, "game.prepare"),
            new HookSpec("EFT.TarkovApplication", "LocalGameCreate", 5, "game.create.local"),
            new HookSpec("EFT.BaseLocalGame`1", "SpawnLoot", 1, "loot.prepare-and-spawn"),
            new HookSpec("EFT.ObjectsFactory", "LoadBundlesAndCreatePools", 6, "resources.bundles-and-pools"),
            new HookSpec("EFT.ObjectsFactory+Pools", "InitAndFillPools", 5, "resources.create-pools"),
            new HookSpec("EFT.EasyAssetsExtensions", "RetainSeparateTask", 4, "resources.retain-request"),
            new HookSpec("Diz.DependencyManager.DependencyGraph`1+TokenBase", "get_LoadingJob", 0, "resources.bundle-job"),
            new HookSpec("EFT.GameWorld", "SpawnLoot", 2, "loot.instantiate"),
            new HookSpec("EFT.BaseLocalGame`1", "PrepareSession", 0, "session.prepare"),
            new HookSpec("InGameMemoryManagement", "Collect", 5, "memory.collect"),
            new HookSpec("InGameMemoryManagement", "GetTotalAllocatedMemoryGB", 0, "memory.measure-with-collection"),
            new HookSpec("Fika.Core.Main.GameMode.CoopGame", "CreateLocalPlayer", 0, "fika.player.create", fika: true),
            new HookSpec("Fika.Core.Main.GameMode.HostGameController", "CreateSpawnSystem", 1, "spawn.host-system", fika: true, maxCalls: 8),
            new HookSpec("EFT.Game.Spawning.SpawnPointsCollection", "CreateFromScene", 2, "spawn.collection-from-scene", maxCalls: 8),
            new HookSpec("EFT.Game.Spawning.SpawnPointsCollection", "FixSpawns", 4, "spawn.reconcile-markers", maxCalls: 8),
            new HookSpec("EFT.Game.Spawning.SpawnPointMarker", "Create", 2, "spawn.marker-create", maxCalls: 8, aggregate: true, parameter1Type: "UnityEngine.GameObject"),
            new HookSpec("EFT.Game.Spawning.SpawnPointMarker", "FixParams", 2, "spawn.marker-fix-params", maxCalls: 8, aggregate: true),
            new HookSpec("Fika.Core.Main.GameMode.HostGameController", "WaitForHostToStart", 0, "fika.wait.host-start", "wait", fika: true),
            new HookSpec("Fika.Core.Main.GameMode.ClientGameController", "WaitForHostToStart", 0, "fika.wait.host-start", "wait", fika: true),
            new HookSpec("Fika.Core.Main.GameMode.HostGameController", "WaitForOtherPlayersToLoad", 0, "fika.wait.players", "wait", fika: true),
            new HookSpec("Fika.Core.Main.GameMode.ClientGameController", "WaitForOtherPlayersToLoad", 0, "fika.wait.players", "wait", fika: true),
            new HookSpec("Fika.Core.Main.GameMode.HostGameController", "InitializeLoot", 1, "fika.loot.initialize", fika: true),
            new HookSpec("Fika.Core.Main.GameMode.ClientGameController", "InitializeLoot", 1, "fika.loot.initialize", fika: true),
            new HookSpec("Fika.Core.Main.GameMode.HostGameController", "StartBotSystemsAndCountdown", 2, "fika.bots-and-readiness", fika: true),
            new HookSpec("Fika.Core.Main.GameMode.HostGameController", "InitializeBotsSystem", 3, "fika.bots.initialize", fika: true, maxCalls: 8),
            new HookSpec("Fika.Core.Main.GameMode.HostGameController", "StartBotsSystem", 1, "fika.bots.start", fika: true),
            new HookSpec("Fika.Core.Main.GameMode.BaseGameController", "FinishRaidSetup", 0, "fika.finish-setup", fika: true),
            new HookSpec("Fika.Core.Main.GameMode.BaseGameController", "CountdownScreen", 2, "fika.deployment", "wait", fika: true),

            new HookSpec("Fika.Core.Main.GameMode.CoopGame", "vmethod_3", 18, "player.fika-factory", fika: true, maxCalls: 8),
            new HookSpec("Fika.Core.Main.Players.FikaPlayer", "Create", 20, "player.fika-create", fika: true, maxCalls: 8),
            new HookSpec("EFT.ObjectsFactory", "CreatePlayerObject", 1, "player.prefab-create", maxCalls: 32),
            new HookSpec("Fika.Core.Main.BaseClasses.BaseInventoryController", ".ctor", 4, "player.inventory-construct", fika: true, maxCalls: 32),
            new HookSpec("Fika.Core.Main.ClientClasses.ClientQuestController", ".ctor", 5, "player.quests-construct", fika: true, maxCalls: 8),
            new HookSpec("Fika.Core.Main.ClientClasses.ClientSharedQuestController", ".ctor", 5, "player.shared-quests-construct", fika: true, maxCalls: 8),
            new HookSpec("Fika.Core.Main.ClientClasses.ClientSharedQuestController", "Init", 0, "player.shared-quests-initialize", fika: true, maxCalls: 8),
            new HookSpec("EFT.Quests.QuestControllerClientBackend", "Run", 0, "player.quests-run", maxCalls: 8),
            new HookSpec("EFT.Player+EmptyHandsController", "Spawn", 2, "player.empty-hands-spawn", maxCalls: 32),
            new HookSpec("Fika.Core.Main.Players.FikaPlayer", "SetupMainPlayer", 0, "player.main-setup", fika: true, maxCalls: 8),
            new HookSpec("Fika.Core.Networking.Http.FikaRequestHandler", "UpdatePlayerSpawn", 1, "player.backend-spawn", fika: true, maxCalls: 8),
            new HookSpec("Fika.Core.Networking.NetManagerUtils", "SetupGameVariables", 1, "player.network-variables", fika: true, maxCalls: 8),
            new HookSpec("EFT.Player", "Init", 15, "player.initialize", maxCalls: 32),
            new HookSpec("EFT.PlayerBody", "Init", 8, "player.body-initialize", maxCalls: 32),
            new HookSpec("EFT.Player", "InitAnimator", 0, "player.animator-initialize", maxCalls: 32),
            new HookSpec("EFT.Player", "CreatePlayerAnimator", 0, "player.animator-create", maxCalls: 32),
            new HookSpec("EFT.Player", "CreateSlotObservers", 0, "player.inventory-observers", maxCalls: 32),
            new HookSpec("EFT.Player", "InitMovementController", 0, "player.movement", maxCalls: 32),
            new HookSpec("EFT.Player", "InitVisuals", 1, "player.visuals", maxCalls: 32),
            new HookSpec("EFT.Player", "InitAudioController", 0, "player.audio", maxCalls: 32),
            new HookSpec("EFT.Player", "InitVoip", 1, "player.voip", maxCalls: 32),
            new HookSpec("EFT.Player", "InitVaultingComponent", 1, "player.vaulting", maxCalls: 32),
            new HookSpec("EFT.Player", "InitWeaponMountingComponent", 1, "player.weapon-mounting", maxCalls: 32),
            new HookSpec("EFT.Player", "InitializeLeftHandController", 0, "player.left-hand", maxCalls: 32),
            new HookSpec("EFT.GameWorld", "RegisterPlayer", 1, "callback.world-register-player", maxCalls: 32),

            new HookSpec("BotProfileClient", "TryLoadBotsProfilesOnStart", 1, "bots.profiles-on-start", maxCalls: 8),
            new HookSpec("EFT.BotsController", "Init", 13, "bots.controller-initialize", maxCalls: 8),
            new HookSpec("EFT.BotsController", "SpatialControllerActivate", 0, "bots.spatial-activate", maxCalls: 8),
            new HookSpec("AICoversData", "CreateOrFind", 1, "bots.covers-find", maxCalls: 8),
            new HookSpec("AICoversData", "RestoreData", 0, "bots.covers-restore", maxCalls: 8),
            new HookSpec("AICoversData", "CachePoints", 0, "bots.covers-cache", maxCalls: 8),
            new HookSpec("BotDoorsController", "RefreshData", 1, "bots.doors-refresh", maxCalls: 8),
            new HookSpec("BotSettingsController", "SetSettings", 4, "bots.settings", maxCalls: 8),
            new HookSpec("EFT.BotsController", "AIPlacesInit", 0, "bots.places", maxCalls: 8),
            new HookSpec("AIUtility", "Init", 0, "bots.utility", maxCalls: 8),
            new HookSpec("BotZone", "Init", 2, "bots.zone-initialize", maxCalls: 64, aggregate: true),
            new HookSpec("EFT.BotsController", "CachedPatrolsZones", 1, "bots.patrol-cache", maxCalls: 8),
            new HookSpec("AICoreController", "Activate", 0, "bots.core-activate", maxCalls: 8),
            new HookSpec("AILootPointsCluster", "CollectActualSpawnedLoot", 1, "bots.cluster-loot", maxCalls: 64, aggregate: true),
            new HookSpec("BotCoverBounds", "DisableAllCoilliders", 0, "bots.cover-colliders-disable", maxCalls: 8),
            new HookSpec("AIStationaryController", "Init", 1, "bots.stationary-weapons", maxCalls: 8),
            new HookSpec("BotZonesLeaveController", ".ctor", 6, "bots.zone-leave-construct", maxCalls: 8),
            new HookSpec("BotZonesLeaveController", "Activate", 0, "bots.zone-leave-activate", maxCalls: 8),
            new HookSpec("BotsEventsController", ".ctor", 7, "bots.events-construct", maxCalls: 16),
            new HookSpec("BotsEventsController", "Activate", 0, "bots.events-activate", maxCalls: 16),
            new HookSpec("OnlineBotSpawner", ".ctor", 9, "bots.spawner-construct", maxCalls: 8),
            new HookSpec("NavMeshCutController", "Init", 2, "bots.navmesh-cut-initialize", maxCalls: 16),

            new HookSpec("InGameMemoryManagement", "RunHeapPreAllocation", 0, "cleanup.heap-preallocation", maxCalls: 8),
            new HookSpec("InGameMemoryManagement", "EmptyWorkingSet", 0, "cleanup.empty-working-set", maxCalls: 8),
            new HookSpec("InGameMemoryManagement", "set_GCEnabled", 1, "cleanup.gc-mode", maxCalls: 16),
            new HookSpec("EFT.AudioUtils", "ResetAudioBuffer", 0, "cleanup.audio-reset", maxCalls: 8),
            new HookSpec("EFT.GameWorld", "FillLampControllers", 0, "setup.lamps", maxCalls: 8),
            new HookSpec("SeasonsController", "Run", 2, "setup.season", maxCalls: 8)
        };

        // Optional mods: presence is checked per group, then every listed signature is required.
        public static readonly HookSpec[] OptionalSpecs = {
            new HookSpec("Orbit.Patches.OrbitInitPatch", "Postfix", 1, "bots.mod.orbit-postfix", maxCalls: 8, optionalGroupType: "Orbit.Patches.OrbitInitPatch"),
            new HookSpec("Orbit.Helpers.ServerConfig", "Fetch", 0, "bots.orbit.fetch-config", maxCalls: 8, optionalGroupType: "Orbit.Patches.OrbitInitPatch"),
            new HookSpec("Orbit.Helpers.ServerConfig", "FetchZones", 0, "bots.orbit.fetch-zones", maxCalls: 8, optionalGroupType: "Orbit.Patches.OrbitInitPatch"),
            new HookSpec("Orbit.Navigation.DangerZones", "Refresh", 0, "bots.orbit.danger-zones", maxCalls: 8, optionalGroupType: "Orbit.Patches.OrbitInitPatch"),
            new HookSpec("Orbit.Navigation.DangerZoneCarver", "AddNavmeshCutter", 0, "bots.orbit.navmesh-cutter", maxCalls: 8, optionalGroupType: "Orbit.Patches.OrbitInitPatch"),
            new HookSpec("Orbit.Core.BotRoster", ".ctor", 0, "bots.orbit.roster-construct", maxCalls: 8, optionalGroupType: "Orbit.Patches.OrbitInitPatch"),
            new HookSpec("Orbit.Core.OrbitManager", ".ctor", 2, "bots.orbit.manager-construct", maxCalls: 8, optionalGroupType: "Orbit.Patches.OrbitInitPatch"),
            new HookSpec("SPT.Custom.Patches.BotsControllerInitPatch", "PatchPostfix", 2, "bots.mod.spt-postfix", maxCalls: 8, optionalGroupType: "SPT.Custom.Patches.BotsControllerInitPatch"),
            new HookSpec("SAIN.Patches.GameWorld.GetBotControllerPatch", "Patch", 1, "bots.mod.sain-postfix", maxCalls: 8, optionalGroupType: "SAIN.Patches.GameWorld.GetBotControllerPatch"),
            new HookSpec("BotPlacementSystemClient.Patches.BotsControllerInitPatch", "PatchPostfix", 1, "bots.mod.abps-postfix", maxCalls: 8, optionalGroupType: "BotPlacementSystemClient.Plugin")
        };

        public static bool Available(HookSpec spec, Func<string, Type> findType) =>
            spec.OptionalGroupType == null || findType(spec.OptionalGroupType) != null;

        public static MethodBase[] Resolve(HookSpec spec, Func<string, Type> findType)
        {
            Type type = findType(spec.Type) ?? throw new TypeLoadException(spec.Type);
            if (type.IsGenericTypeDefinition)
            {
                string argument = spec.Type.StartsWith("Diz.DependencyManager", StringComparison.Ordinal) ? "Diz.Resources.IEasyBundle" : "EFT.EftGamePlayerOwner";
                type = type.MakeGenericType(findType(argument) ?? throw new TypeLoadException(argument));
            }
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            MethodBase[] methods = (spec.Method == ".ctor" ? type.GetConstructors(flags).Cast<MethodBase>() : type.GetMethods(flags))
                .Where(m => m.Name == spec.Method && m.GetParameters().Length == spec.Parameters).ToArray();
            if (spec.Stage == "resources.bundles-and-pools")
                methods = methods.Where(m => m.GetParameters()[0].Name == "pools").ToArray();
            if (spec.Parameter1Type != null)
                methods = methods.Where(m => m.GetParameters()[1].ParameterType.FullName == spec.Parameter1Type).ToArray();
            if (methods.Length != 1)
                throw new MissingMethodException(spec.Type, spec.Method);
            foreach (MethodBase method in methods)
                if (method.ContainsGenericParameters || method.IsAbstract || ReturnType(method).IsByRef || ReturnType(method).IsPointer)
                    throw new NotSupportedException(method.ToString());
            if (spec.Aggregate && (spec.MaxCalls <= 0 || methods.Any(m => typeof(Task).IsAssignableFrom(ReturnType(m)) || ReturnType(m) == typeof(IEnumerator))))
                throw new NotSupportedException("Aggregate stages require synchronous methods and a positive detail cap");
            return methods;
        }

        public static Type ReturnType(MethodBase method) => method is MethodInfo info ? info.ReturnType : typeof(void);
    }
}
