using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Automatics.Valheim;
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
        private const string ModVersion = "1.4.3";

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
        private static List<string> _allResourcesDirectory;

        public static BaseUnityPlugin Plugin { get; private set; }
        public static Logger Logger { get; private set; }
        public static L10N L10N { get; private set; }

        private static IEnumerable<string> GetAllResourcesDirectory()
        {
            if (!(_allResourcesDirectory is null)) return _allResourcesDirectory;
            _allResourcesDirectory = new List<string> { _modLocation };

            var root = Paths.PluginPath;
            if (!Directory.Exists(root)) return _allResourcesDirectory;

            foreach (var directory in Directory.GetDirectories(root))
            {
                var marker = Path.Combine(directory, "automatics-child-mod");
                if (File.Exists(marker))
                {
                    _allResourcesDirectory.Add(directory);
                    continue;
                }

                marker = Path.Combine(directory, "automatics-resources-marker");
                if (File.Exists(marker))
                    _allResourcesDirectory.Add(directory);
            }

            return _allResourcesDirectory;
        }

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

        [Obsolete]
        public static string GetInjectedResourcePath(string resourceName)
        {
            var directory = Config.ResourcesDirectory;
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                return Path.Combine(directory, resourceName);
            return "";
        }

        public static IEnumerable<string> GetAllResourcePath(string pathname)
        {
            return GetAllResourcesDirectory().Select(x => Path.Combine(x, pathname));
        }

        public static string GetHarmonyId(string moduleName)
        {
            return $"{_guid}.{moduleName}";
        }

        public static void Initialize(BaseUnityPlugin plugin, ManualLogSource logger)
        {
            Plugin = plugin;
            Logger = new Logger(logger, Config.IsLogEnabled);
            _modLocation = Path.GetDirectoryName(Plugin.Info.Location) ?? "";
            _guid = Plugin.Info.Metadata.GUID;

            Logger.Debug($"Mod location: {_modLocation}");

            ConfigMigration.Migration(Plugin.Config);

            ValheimObject.Initialize(GetAllResourcePath("Data"));

            L10N = new L10N(L10NPrefix);
            var translationsLoader = new TranslationsLoader(L10N);
            foreach (var directory in GetAllResourcePath("Languages"))
                translationsLoader.LoadJson(directory);

            Config.Initialize(Plugin.Config);

            translationsLoader.LoadJson(GetInjectedResourcePath("Languages"));

            Hooks.OnInitTerminal += Commands.Register;

            Harmony.CreateAndPatchAll(typeof(Patches), _guid);
            InitializeModules(Assembly.GetExecutingAssembly());

            ValheimObject.PostInitialize();
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