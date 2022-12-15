using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using JetBrains.Annotations;
using ModUtils;
using Logger = ModUtils.Logger;

namespace Automatics
{
    [BepInPlugin(ModId, ModName, ModVersion)]
    public class UnityPlugin : BaseUnityPlugin
    {
        private const string ModId = "net.eidee.valheim.automatics";
        private const string ModName = "Automatics";
        private const string ModVersion = "1.3.2";

        private void Awake()
        {
            Automatics.Initialize(this, Logger);
        }
    }

    internal static class Automatics
    {
        public const string L10NPrefix = "automatics";

        private static string _modLocation;
        private static string _guid;

        public static BaseUnityPlugin Plugin { get; private set; }
        public static Logger Logger { get; private set; }
        public static L10N L10N { get; private set; }

        private static void InitializeModules(Assembly assembly)
        {
            foreach (var (initializer, _) in AccessTools.GetTypesFromAssembly(assembly)
                         .SelectMany(AccessTools.GetDeclaredMethods)
                         .Select(x => (x,
                             x.GetCustomAttributes(typeof(AutomaticsInitializerAttribute), false)
                                 .OfType<AutomaticsInitializerAttribute>().FirstOrDefault()))
                         .Where(x => x.Item2 != null)
                         .OrderBy(x => x.Item2.Order))
                try
                {
                    initializer.Invoke(null, new object[] { });
                }
                catch (Exception e)
                {
                    Logger.Error($"Error while initializing {initializer.Name}\n{e}");
                }
        }

        internal static string GetInjectedResourcePath(string resourceName)
        {
            var directory = Config.ResourcesDirectory;
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                return Path.Combine(directory, resourceName);
            return "";
        }

        public static string GetFilePath(string pathname)
        {
            return Path.Combine(_modLocation, pathname);
        }

        public static string GetHarmonyId(string moduleName)
        {
            return $"{_guid}.{moduleName}";
        }

        [Obsolete]
        public static void AddTimer(string id, Action callback, float delay)
        {
        }

        public static void Initialize(BaseUnityPlugin plugin, ManualLogSource logger)
        {
            Plugin = plugin;
            Logger = new Logger(logger, Config.IsLogEnabled);
            _modLocation = Path.GetDirectoryName(Plugin.Info.Location) ?? "";
            _guid = Plugin.Info.Metadata.GUID;

            Logger.Debug($"Mod location: {_modLocation}");

            Migration.MigrateConfig(Plugin.Config);

            L10N = new L10N(L10NPrefix);
            var translationsLoader = new TranslationsLoader(L10N);
            translationsLoader.LoadJson(GetFilePath("Languages"));

            foreach (var automaticsChildModDir in GetAutomaticsChildModDirs())
                translationsLoader.LoadJson(Path.Combine(automaticsChildModDir, "Languages"));

            Config.Initialize(Plugin.Config);

            translationsLoader.LoadJson(GetInjectedResourcePath("Languages"));

            Hooks.OnInitTerminal += Command.Register;

            Harmony.CreateAndPatchAll(typeof(Patches), _guid);
            InitializeModules(Assembly.GetExecutingAssembly());
        }

        internal static IEnumerable<string> GetAutomaticsChildModDirs()
        {
            var root = Paths.PluginPath;
            if (Directory.Exists(root))
                return Directory.GetDirectories(root).Where(directory =>
                    File.Exists(Path.Combine(directory, "automatics-child-mod"))).ToList();

            return Array.Empty<string>();
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    [MeansImplicitUse]
    public class AutomaticsInitializerAttribute : Attribute
    {
        public AutomaticsInitializerAttribute(int order = 0)
        {
            Order = order;
        }

        public int Order { get; }
    }
}