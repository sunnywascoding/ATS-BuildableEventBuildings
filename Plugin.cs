using BepInEx;using BepInEx.Configuration;
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
        public static Plugin Instance;
        private Harmony harmony;
        public static ManualLogSource Log;

        // In-game building names that this plugin can manage.
        private static readonly string[] BuildingNames =
        {
            "Feast Hall",
            "Finesmith",
            "Flawless Cellar",
            "Flawless Cooperage",
            "Flawless Druid",
            "Flawless Leatherworks",
            "Flawless Rain Mill",
            "Flawless Smelter",
            "Hallowed Herb Garden",
            "Hallowed SmallFarm",
            "Holy Guild House",
            "Holy Market",
            "Holy Temple",
            "Homestead",
            "Rainpunk Foundry"
        };

        private static readonly Dictionary<string, ConfigEntry<BuildingMode>> BuildingConfigs =
            new Dictionary<string, ConfigEntry<BuildingMode>>();

        private static readonly Dictionary<string, ConfigEntry<float>> CostMultipliers =
            new Dictionary<string, ConfigEntry<float>>();

        // Enhanced building -> its regular version (names confirmed to exist via the discovery log).
        private static readonly Dictionary<string, string> RegularVersions = new Dictionary<string, string>
        {
            { "Flawless Cellar", "Cellar" },
            { "Flawless Cooperage", "Cooperage" },
            { "Flawless Druid", "Druid" },
            { "Flawless Leatherworks", "Leatherworks" },
            { "Flawless Rain Mill", "Rain Mill" },
            { "Flawless Smelter", "Smelter" },
            { "Hallowed Herb Garden", "Herb Garden" },
            { "Hallowed SmallFarm", "SmallFarm" },
            { "Holy Guild House", "Guild House" },
            { "Holy Market", "Market" },
            { "Holy Temple", "Temple" }
        };

        private static readonly Dictionary<string, ConfigEntry<bool>> RemoveRegular =
            new Dictionary<string, ConfigEntry<bool>>();
        private static ConfigEntry<bool> RemoveRegularOnPick;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            // Config file lives at BepInEx/config/<GUID>.cfg and is created on first run.
            RemoveRegularOnPick = Config.Bind(
                "RemoveRegularVersionOnPick",
                "RemoveRegularVersionOnPick",
                true,
                "If true, picking an advanced building (e.g. Flawless Cellar) removes its regular version " +
                "(e.g. Cellar) from the draft pool.");

            foreach (var name in BuildingNames)
            {
                BuildingConfigs[name] = Config.Bind(
                    "Buildings",
                    name,
                    BuildingMode.Pickable,
                    "Disabled = do nothing, Unlocked = unlock at the start of every new game, " +
                    "Pickable = add to the pool of buildings that can be picked.");

                CostMultipliers[name] = Config.Bind(
                    "CostMultipliers",
                    name,
                    2f,
                    new ConfigDescription(
                        "Multiplier applied to this building's construction cost. 1 = vanilla cost.",
                        new AcceptableValueRange<float>(0.1f, 20f)));

                if (RegularVersions.TryGetValue(name, out var regularName))
                {
                    RemoveRegular[name] = Config.Bind(
                        "RemoveRegularVersion",
                        name,
                        false,
                        $"If true, the regular '{regularName}' is removed from the draft pool at the start of a new game.");
                }
            }

            harmony = Harmony.CreateAndPatchAll(typeof(Plugin));

            Log.LogInfo($"{PluginInfo.PLUGIN_NAME} log v{PluginInfo.PLUGIN_VERSION} Plugin loaded");
        }

        private static BuildingMode GetMode(string name) =>
            BuildingConfigs.TryGetValue(name, out var entry) ? entry.Value : BuildingMode.Disabled;

        [HarmonyPatch(typeof(MainController), nameof(MainController.OnServicesReady))]
        [HarmonyPostfix]
        private static void HookMainControllerSetup()
        {
            Log.LogInfo($"Performing game initialization on behalf of {PluginInfo.PLUGIN_GUID}.");

            foreach (var name in BuildingNames)
            {
                if (GetMode(name) == BuildingMode.Disabled)
                    continue;

                BuildingModel building = MB.Settings.GetBuilding(name);
                if (building == null)
                {
                    Log.LogWarning($"Building '{name}' was not found; skipping.");
                    continue;
                }

                // Both modes need the building to be known to the meta content list.
                if (!MB.MetaStateService.Content.buildings.Contains(building.Name))
                {
                    Log.LogInfo($"Adding {building.Name} to the list of available buildings.");
                    MB.MetaStateService.Content.buildings.Add(building.Name);
                }

                // Scale the construction cost (requiredGoods) by the configured multiplier.
                float multiplier = CostMultipliers[name].Value;
                if (building.requiredGoods != null && !building.requiredGoods.Any())
                {
                    Log.LogInfo($"{building.Name} has no construction cost to scale.");
                }
                else if (building.requiredGoods != null && Math.Abs(multiplier - 1f) > 0.001f)
                {
                    building.requiredGoods = building.requiredGoods
                        .Select(g => g.ScaleToNew(multiplier))
                        .ToArray();
                    Log.LogInfo($"{building.Name} construction cost scaled by x{multiplier}.");
                }
            }
        }

        private static void RemoveFromPool(BuildingModel regular)
        {
            var bp = SO.BiomeService.Blueprints;
            bp.wildcards = bp.wildcards.Where(w => w.building != regular).ToArray();
            foreach (var range in bp.blueprints)
                foreach (var set in range.sets)
                    set.buildings = set.buildings.Where(b => b.building != regular).ToArray();
            Log.LogInfo($"Removed regular version {regular.Name} from the draft pool.");
        }

        // The reputation rewards service generates the first draft while it loads, which is before
        // GameController.StartGame fires. The draft pool must therefore be edited before it.
        [HarmonyPatch(typeof(ReputationRewardsService), "OnLoading")]
        [HarmonyPrefix]
        private static void PrepareDraftPool()
        {
            var isNewGame = MB.GameSaveService.IsNewGame();
            Log.LogInfo($"Preparing draft pool. Is this a new game: {isNewGame}.");
            if (!isNewGame)
                return;

            foreach (var name in BuildingNames)
            {
                var mode = GetMode(name);
                if (mode == BuildingMode.Disabled)
                    continue;

                BuildingModel building = MB.Settings.GetBuilding(name);
                if (building == null)
                    continue;

                if (RemoveRegular.TryGetValue(name, out var removeEntry) && removeEntry.Value &&
                    RegularVersions.TryGetValue(name, out var regularName))
                {
                    BuildingModel regular = MB.Settings.GetBuilding(regularName);
                    if (regular != null)
                        RemoveFromPool(regular);
                    else
                        Log.LogWarning($"{name}: regular version '{regularName}' was not found; nothing removed.");
                }

                if (mode == BuildingMode.Pickable &&
                    !SO.BiomeService.Blueprints.wildcards.Any(w => w.building == building))
                {
                    SO.BiomeService.Blueprints.wildcards =
                        SO.BiomeService.Blueprints.wildcards
                            .Append(new BuildingWeightedChance { building = building })
                            .ToArray();
                    Log.LogInfo($"Added {building.Name} to the pickable buildings.");
                }
            }
        }

        [HarmonyPatch(typeof(GameContentService), nameof(GameContentService.Unlock))]
        [HarmonyPostfix]
        private static void RemoveRegularWhenAdvancedUnlocked(BuildingModel building)
        {
            if (!RemoveRegularOnPick.Value)
                return;

            var name = building.Name;
            if (!RegularVersions.TryGetValue(name, out var regularName))
                return;

            BuildingModel regular = MB.Settings.GetBuilding(regularName);
            if (regular != null)
            {
                Log.LogInfo($"{name} was picked; removing regular version '{regularName}' from the draft pool.");
                RemoveFromPool(regular);
            }
            else
            {
                Log.LogWarning($"{name}: regular version '{regularName}' was not found; nothing removed.");
            }
        }

        [HarmonyPatch(typeof(GameController), nameof(GameController.StartGame))]
        [HarmonyPostfix]
        private static void HookEveryGameStart()
        {
            // Runs after every game start.
            var isNewGame = MB.GameSaveService.IsNewGame();
            Log.LogInfo($"Entered a game. Is this a new game: {isNewGame}.");
            if (!isNewGame)
                return;

            foreach (var name in BuildingNames)
            {
                if (GetMode(name) != BuildingMode.Unlocked)
                    continue;

                BuildingModel building = MB.Settings.GetBuilding(name);
                if (building == null)
                    continue;

                SO.GameContentService.Unlock(building);
                Log.LogInfo($"Unlocked {building.Name}.");
            }
        }
    }
}
