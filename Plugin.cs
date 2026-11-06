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
        private const string GUID = "BuildableEventBuildings";
        private const string NAME = "BuildableEventBuildings";
        private const string VERSION = "1.0.0";

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

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            // Config file lives at BepInEx/config/<GUID>.cfg and is created on first run.
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
            }

            harmony = Harmony.CreateAndPatchAll(typeof(Plugin));

            Log.LogInfo($"{NAME} log v{VERSION} Plugin loaded");
        }

        private static BuildingMode GetMode(string name) =>
            BuildingConfigs.TryGetValue(name, out var entry) ? entry.Value : BuildingMode.Disabled;

        [HarmonyPatch(typeof(MainController), nameof(MainController.OnServicesReady))]
        [HarmonyPostfix]
        private static void HookMainControllerSetup()
        {
            Log.LogInfo($"Performing game initialization on behalf of {GUID}.");

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
                var mode = GetMode(name);
                if (mode == BuildingMode.Disabled)
                    continue;

                BuildingModel building = MB.Settings.GetBuilding(name);
                if (building == null)
                    continue;

                switch (mode)
                {
                    case BuildingMode.Unlocked:
                        SO.GameContentService.Unlock(building);
                        Log.LogInfo($"Unlocked {building.Name}.");
                        break;

                    case BuildingMode.Pickable:
                        if (!SO.BiomeService.Blueprints.wildcards.Any(w => w.building == building))
                        {
                            SO.BiomeService.Blueprints.wildcards =
                                SO.BiomeService.Blueprints.wildcards
                                    .Append(new BuildingWeightedChance { building = building })
                                    .ToArray();
                            Log.LogInfo($"Added {building.Name} to the pickable buildings.");
                        }
                        break;
                }
            }
        }
    }
}
