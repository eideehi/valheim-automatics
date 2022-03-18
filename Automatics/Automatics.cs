using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Automatics
{
    [BepInPlugin(ModId, ModName, ModVersion)]
    public partial class Automatics : BaseUnityPlugin
    {
        private const string ModId = "net.eidee.valheim.automatics";
        private const string ModName = "Automatics";
        private const string ModVersion = "1.0.4";

        internal static string ModLocation { get; private set; }
        internal static ManualLogSource ModLogger { get; private set; }
        internal static ConfigFile ModConfig { get; private set; }

        private void Awake()
        {
            var assembly = Assembly.GetExecutingAssembly();

            ModLocation = Path.GetDirectoryName(assembly.Location) ?? "";
            ModLogger = Logger;
            ModConfig = Config;

            ModLogger.LogInfo($"{ModName} {ModVersion} - Running");
            LanguageLoader.LoadFromCsv(Path.Combine(ModLocation, "Languages"));
            InitializeConfigurations();

            AutomaticMapPinning.AutomaticMapPinning.Initialize();

            Harmony.CreateAndPatchAll(assembly, ModId);
        }
    }

    [HarmonyPatch]
    internal static partial class Patches
    {
        [HarmonyPostfix, HarmonyPatch(typeof(Container), "Awake")]
        private static void ContainerAwakePostfix(Container __instance, ZNetView ___m_nview)
        {
            if (___m_nview.GetZDO() != null)
                __instance.gameObject.AddComponent<ContainerCache>();
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Pickable), "Awake")]
        private static void PickableAwakePostfix(Pickable __instance, ZNetView ___m_nview)
        {
            if (___m_nview.GetZDO() != null)
                __instance.gameObject.AddComponent<PickableCache>();
        }
    }

    [DisallowMultipleComponent]
    public sealed class ContainerCache : InstanceCache<Container>
    {
    }

    [DisallowMultipleComponent]
    public sealed class PickableCache : InstanceCache<Pickable>
    {
    }
}