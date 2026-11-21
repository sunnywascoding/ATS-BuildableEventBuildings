using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Eremite;
using Eremite.Buildings;
using Eremite.Controller;
using Eremite.Model;
using Eremite.Model.Meta;
using Eremite.Services;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BuildableEventBuildings
{
    public enum BuildingMode
    {
        // Do nothing for this building.
        Disabled,
        // Building is unlocked at the start of every new game.
        Unlocked,
        // Building is added to the pool of buildings that can be picked (wildcards).
        Pickable
    }

    [HarmonyPatch]
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;

        private static ConfigEntry<bool> DebugLog;
        private static ConfigEntry<bool> RemoveRegularOnPick;

        // Everything the plugin knows about one manageable building.
        private sealed class BuildingSettings
        {
            public readonly string Name;         // in-game building name
            public readonly string RegularName;  // regular version (null if it has none)

            public ConfigEntry<BuildingMode> Mode;
            public ConfigEntry<float> CostMultiplier;
            public ConfigEntry<bool> RemoveRegular; // null when RegularName is null

            public BuildingSettings(string name, string regularName = null)
            {
                Name = name;
                RegularName = regularName;
            }

            public void Bind(ConfigFile config)
            {
                Mode = config.Bind(
                    "Buildings", Name, BuildingMode.Pickable,
                    "Disabled = do nothing, Unlocked = unlock at the start of every new game, " +
                    "Pickable = add to the pool of buildings that can be picked.");

                CostMultiplier = config.Bind(
                    "CostMultipliers", Name, 2f,
                    new ConfigDescription(
                        "Multiplier applied to this building's construction cost. 1 = vanilla cost.",
                        new AcceptableValueRange<float>(0.1f, 20f)));

                if (RegularName != null)
                {
                    RemoveRegular = config.Bind(
                        "RemoveRegularVersion", Name, false,
                        $"If true, the regular '{RegularName}' is removed from the draft pool at the start of a new game.");
                }
            }
        }

        // Regular names were confirmed to exist via the discovery log.
        private static readonly BuildingSettings[] Buildings =
        {
            new BuildingSettings("Feast Hall"),
            new BuildingSettings("Finesmith"),
            new BuildingSettings("Flawless Cellar", "Cellar"),
            new BuildingSettings("Flawless Cooperage", "Cooperage"),
            new BuildingSettings("Flawless Druid", "Druid"),
            new BuildingSettings("Flawless Leatherworks", "Leatherworks"),
            new BuildingSettings("Flawless Rain Mill", "Rain Mill"),
            new BuildingSettings("Flawless Smelter", "Smelter"),
            new BuildingSettings("Hallowed Herb Garden", "Herb Garden"),
            new BuildingSettings("Hallowed SmallFarm", "SmallFarm"),
            new BuildingSettings("Holy Guild House", "Guild House"),
            new BuildingSettings("Holy Market", "Market"),
            new BuildingSettings("Holy Temple", "Temple"),
            new BuildingSettings("Homestead"),
            new BuildingSettings("Rainpunk Foundry"),
        };

        private void Awake()
        {
            Log = Logger;

            DebugLog = Config.Bind(
                "DebugLog", "DebugLog", false,
                "If true, add the debug logs to bepinex log window");

            // Config file lives at BepInEx/config/<GUID>.cfg and is created on first run.
            RemoveRegularOnPick = Config.Bind(
                "RemoveRegularVersionOnPick", "RemoveRegularVersionOnPick", true,
                "If true, picking an advanced building (e.g. Flawless Cellar) removes its regular version " +
                "(e.g. Cellar) from the draft pool.");

            foreach (var settings in Buildings)
                settings.Bind(Config);

            Harmony.CreateAndPatchAll(typeof(Plugin));

            Log.LogInfo($"{PluginInfo.PLUGIN_NAME} log v{PluginInfo.PLUGIN_VERSION} Plugin loaded");
        }

        // ---- helpers ----------------------------------------------------------------

        private static void DebugInfo(string message)
        {
            if (DebugLog.Value) Log.LogInfo(message);
        }

        private static void DebugWarn(string message)
        {
            if (DebugLog.Value) Log.LogWarning(message);
        }

        // Runs `action` for every building that passes `filter` and exists in the game.
        private static void ForEachManaged(
            Func<BuildingSettings, bool> filter,
            Action<BuildingSettings, BuildingModel> action)
        {
            foreach (var settings in Buildings)
            {
                if (!filter(settings))
                    continue;

                var building = MB.Settings.GetBuilding(settings.Name);
                if (building == null)
                {
                    DebugWarn($"Building '{settings.Name}' was not found; skipping.");
                    continue;
                }

                action(settings, building);
            }
        }

        private static bool IsEnabled(BuildingSettings s) => s.Mode.Value != BuildingMode.Disabled;

        private static void RemoveFromPool(BuildingModel regular)
        {
            var blueprints = SO.BiomeService.Blueprints;
            blueprints.wildcards = blueprints.wildcards.Where(w => w.building != regular).ToArray();
            foreach (var range in blueprints.blueprints)
                foreach (var set in range.sets)
                    set.buildings = set.buildings.Where(b => b.building != regular).ToArray();

            DebugInfo($"Removed regular version {regular.Name} from the draft pool.");
        }

        private static void RemoveRegularFromPool(BuildingSettings settings)
        {
            var regular = MB.Settings.GetBuilding(settings.RegularName);
            if (regular == null)
            {
                DebugWarn($"{settings.Name}: regular version '{settings.RegularName}' was not found; nothing removed.");
                return;
            }

            RemoveFromPool(regular);
        }

        private static void ScaleConstructionCost(BuildingModel building, float multiplier)
        {
            if (building.requiredGoods == null || !building.requiredGoods.Any())
            {
                DebugInfo($"{building.Name} has no construction cost to scale.");
                return;
            }

            if (Math.Abs(multiplier - 1f) <= 0.001f)
                return;

            building.requiredGoods = building.requiredGoods
                .Select(g => g.ScaleToNew(multiplier))
                .ToArray();
            DebugInfo($"{building.Name} construction cost scaled by x{multiplier}.");
        }

        // ---- patches ----------------------------------------------------------------

        [HarmonyPatch(typeof(MainController), nameof(MainController.OnServicesReady))]
        [HarmonyPostfix]
        private static void HookMainControllerSetup()
        {
            DebugInfo($"Performing game initialization on behalf of {PluginInfo.PLUGIN_GUID}.");

            ForEachManaged(IsEnabled, (settings, building) =>
            {
                // Both modes need the building to be known to the meta content list.
                var knownBuildings = MB.MetaStateService.Content.buildings;
                if (!knownBuildings.Contains(building.Name))
                {
                    DebugInfo($"Adding {building.Name} to the list of available buildings.");
                    knownBuildings.Add(building.Name);
                }

                ScaleConstructionCost(building, settings.CostMultiplier.Value);
            });
        }

        // The reputation rewards service generates the first draft while it loads, which is before
        // GameController.StartGame fires. The draft pool must therefore be edited before it.
        [HarmonyPatch(typeof(ReputationRewardsService), "OnLoading")]
        [HarmonyPrefix]
        private static void PrepareDraftPool()
        {
            var isNewGame = MB.GameSaveService.IsNewGame();
            DebugInfo($"Preparing draft pool. Is this a new game: {isNewGame}.");
            if (!isNewGame)
                return;

            var blueprints = SO.BiomeService.Blueprints;
            var additions = new List<BuildingWeightedChance>();

            ForEachManaged(IsEnabled, (settings, building) =>
            {
                if (settings.RemoveRegular != null && settings.RemoveRegular.Value)
                    RemoveRegularFromPool(settings);

                if (settings.Mode.Value == BuildingMode.Pickable &&
                    !blueprints.wildcards.Any(w => w.building == building))
                {
                    additions.Add(new BuildingWeightedChance { building = building });
                    DebugInfo($"Added {building.Name} to the pickable buildings.");
                }
            });

            // Append once instead of rebuilding the array per building.
            if (additions.Count > 0)
                blueprints.wildcards = blueprints.wildcards.Concat(additions).ToArray();
        }

        [HarmonyPatch(typeof(GameContentService), nameof(GameContentService.Unlock))]
        [HarmonyPostfix]
        private static void RemoveRegularWhenAdvancedUnlocked(BuildingModel building)
        {
            if (!RemoveRegularOnPick.Value)
                return;

            var settings = Buildings.FirstOrDefault(b => b.Name == building.Name);
            if (settings?.RegularName == null)
                return;

            DebugInfo($"{settings.Name} was picked; removing regular version '{settings.RegularName}' from the draft pool.");
            RemoveRegularFromPool(settings);
        }

        // Runs after every game start; only new games get the unlocks.
        [HarmonyPatch(typeof(GameController), nameof(GameController.StartGame))]
        [HarmonyPostfix]
        private static void HookEveryGameStart()
        {
            var isNewGame = MB.GameSaveService.IsNewGame();
            DebugInfo($"Entered a game. Is this a new game: {isNewGame}.");
            if (!isNewGame)
                return;

            ForEachManaged(s => s.Mode.Value == BuildingMode.Unlocked, (settings, building) =>
            {
                SO.GameContentService.Unlock(building);
                DebugInfo($"Unlocked {building.Name}.");
            });
        }
    }
}
