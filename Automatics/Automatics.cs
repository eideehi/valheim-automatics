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
        private const string ModVersion = "1.0.5";

        internal static string ModLocation { get; private set; }
        internal static ManualLogSource ModLogger { get; private set; }
        internal static ConfigFile ModConfig { get; private set; }
        internal static Action OnInitTerminal { get; set; }
        internal static Action OnPlayerUpdate { get; set; }

        private void Awake()
        {
            var assembly = Assembly.GetExecutingAssembly();

            ModLocation = Path.GetDirectoryName(assembly.Location) ?? "";
            ModLogger = Logger;
            ModConfig = Config;

            ModLogger.LogInfo($"{ModName} {ModVersion} - Running");

            Core.Initialize();
            AutomaticDoor.Core.Initialize();
            AutomaticMapPinning.Core.Initialize();
            AutomaticProcessing.Core.Initialize();

            Harmony.CreateAndPatchAll(assembly, ModId);
        }
    }
}