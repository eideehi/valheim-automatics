using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using JetBrains.Annotations;
using ModUtils;
using UnityEngine;
using Logger = ModUtils.Logger;
using Random = UnityEngine.Random;

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
            Automatics.Initialize(Info, Config, Logger);
            InvokeRepeating(nameof(InvokeTimer), Random.Range(1f, 2f), 0.05f);
        }

        private void InvokeTimer()
        {
            foreach (var queue in Automatics.TimerQueue.ToList())
            {
                var timer = queue.Value;
                if (!(Time.time - timer.timestamp >= timer.delay)) continue;
                Automatics.TimerQueue.Remove(queue.Key);
                timer.callback.Invoke();
            }
        }
    }

    internal static class Automatics
    {
        public const string L10NPrefix = "automatics";

        public static readonly Dictionary<string, Timer> TimerQueue;

        private static string _modLocation;

        static Automatics()
        {
            TimerQueue = new Dictionary<string, Timer>();
        }

        private static string GUID { get; set; }

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
            return $"{GUID}.{moduleName}";
        }

        public static void AddTimer(string id, Action callback, float delay)
        {
            TimerQueue[id] = new Timer
            {
                timestamp = Time.time,
                delay = delay,
                callback = callback
            };
        }

        public static void RemoveTimer(string id)
        {
            TimerQueue.Remove(id);
        }

        public static void Initialize(PluginInfo info, ConfigFile config, ManualLogSource logger)
        {
            Logger = new Logger(logger, Config.IsLogEnabled);

            Migration.MigrateConfig(config);

            _modLocation = Path.GetDirectoryName(info.Location) ?? "";
            L10N = new L10N(L10NPrefix);

            Logger.Debug($"Mod location: {_modLocation}");

            var translationsLoader = new TranslationsLoader(L10N);
            translationsLoader.LoadJson(GetFilePath("Languages"));

            foreach (var automaticsChildModDir in GetAutomaticsChildModDirs())
                translationsLoader.LoadJson(Path.Combine(automaticsChildModDir, "Languages"));

            Config.Initialize(config);

            translationsLoader.LoadJson(GetInjectedResourcePath("Languages"));

            Hooks.OnInitTerminal += Command.Register;

            GUID = info.Metadata.GUID;
            Harmony.CreateAndPatchAll(typeof(Patches), GUID);

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

        public struct Timer
        {
            public float timestamp;
            public float delay;
            public Action callback;
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