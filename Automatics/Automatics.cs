using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using JetBrains.Annotations;

namespace Automatics
{
    [BepInPlugin(ModId, ModName, ModVersion)]
    public class Automatics : BaseUnityPlugin
    {
        private const string ModId = "net.eidee.valheim.automatics";
        private const string ModName = "Automatics";
        private const string ModVersion = "1.2.1";

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

            var assembly = Assembly.GetExecutingAssembly();

            foreach (var (initializer, _) in AccessTools.GetTypesFromAssembly(assembly)
                         .SelectMany(AccessTools.GetDeclaredMethods)
                         .Select(x => (x,
                             x.GetCustomAttributes(typeof(AutomaticsInitializerAttribute), false)
                                 .OfType<AutomaticsInitializerAttribute>().FirstOrDefault()))
                         .Where(x => x.Item2 != null)
                         .OrderBy(x => x.Item2.Order))
            {
                try
                {
                    initializer.Invoke(null, new object[] { });
                }
                catch (Exception e)
                {
                    ModLogger.LogError($"Error while initializing {initializer.Name}\n{e}");
                }
            }

            Harmony.CreateAndPatchAll(assembly, ModId);
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    [MeansImplicitUse]
    public class AutomaticsInitializerAttribute : Attribute
    {
        public int Order { get; }

        public AutomaticsInitializerAttribute(int order = 0)
        {
            Order = order;
        }
    }
}