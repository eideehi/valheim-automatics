using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace Automatics
{
    [BepInPlugin(ModId, ModName, ModVersion)]
    public class Automatics : BaseUnityPlugin
    {
        private const string ModId = "net.eidee.valheim.automatics";
        private const string ModName = "Automatics";
        private const string ModVersion = "1.2.0";

        internal static string ModLocation { get; private set; }
        internal static ManualLogSource ModLogger { get; private set; }
        internal static ConfigFile ModConfig { get; private set; }
        internal static Action OnInitTerminal { get; set; }
        internal static Action<Player, bool> OnPlayerUpdate { get; set; }

        private void Awake()
        {
            ModLocation = Path.GetDirectoryName(Info.Location) ?? "";
            ModLogger = Logger;
            ModConfig = Config;

            ModLogger.LogInfo($"{ModName} {ModVersion} - Running");

            Core.Initialize();
            AutomaticDoor.Core.Initialize();
            AutomaticMapPinning.Core.Initialize();
            AutomaticProcessing.Core.Initialize();
            AutomaticFeeding.Core.Initialize();
            AutomaticRepair.Core.Initialize();

            Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), ModId);
        }
    }
}