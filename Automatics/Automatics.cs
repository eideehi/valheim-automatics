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
using UnityEngine;

namespace Automatics
{
    [BepInPlugin(ModId, ModName, ModVersion)]
    public class Automatics : BaseUnityPlugin
    {
        private const string ModId = "net.eidee.valheim.automatics";
        private const string ModName = "Automatics";
        private const string ModVersion = "1.2.1";

        private static readonly Dictionary<string, (Action action, float timestamp, float delay)> InvokeQueue;

        static Automatics()
        {
            InvokeQueue = new Dictionary<string, (Action action, float timestamp, float delay)>();
        }

        internal static string ModLocation { get; private set; }
        internal static ManualLogSource ModLogger { get; private set; }
        internal static ConfigFile ModConfig { get; private set; }
        internal static Action OnGameAwake { get; set; }
        internal static Action OnInitTerminal { get; set; }
        internal static Action<Player, bool> OnPlayerUpdate { get; set; }

        internal static string GetDefaultResourcePath(string resourceName)
        {
            return Path.Combine(ModLocation, resourceName);
        }

        internal static string GetInjectedResourcePath(string resourceName)
        {
            var directory = global::Automatics.Config.ResourcesDirectory;
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                return Path.Combine(directory, resourceName);
            return "";
        }

        internal static void AddInvoke(string id, Action action, float delay)
        {
            InvokeQueue[id] = (action, Time.time, delay);
        }

        internal static void RemoveInvoke(string id)
        {
            InvokeQueue.Remove(id);
        }

        private void Awake()
        {
            ModLocation = Path.GetDirectoryName(Info.Location) ?? "";
            ModLogger = Logger;
            ModConfig = Config;

            Migration.MigrateConfig(ModConfig);

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

        private void Update()
        {
            foreach (var queue in InvokeQueue.ToList())
            {
                var (action, timestamp, delay) = queue.Value;
                if (!(Time.time - timestamp >= delay)) continue;

                action.Invoke();
                InvokeQueue.Remove(queue.Key);
            }
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