using System;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;

namespace Automatics
{
    /// <summary>
    ///     Asks ConfigurationManager to re-read its setting list after a description changed.
    ///     ConfigurationManager already rebuilds that list whenever its window opens, so all this
    ///     adds is that a window which is open at the moment of the change shows the new text.
    ///     ConfigurationManager is optional. Its instance is taken from the plugin BepInEx loaded
    ///     under its GUID, and every member is looked up on that plugin's own types, so an absent
    ///     plugin - or a version without these members - is a silent no-op. Looking the types up
    ///     by name instead would scan every loaded assembly and log a Harmony warning each time
    ///     the plugin is missing. ConfigurationManager 18.4.1 has no static instance to ask for;
    ///     <c>BuildSettingList</c> is an instance method.
    /// </summary>
    internal static class ConfigurationManagerBridge
    {
        private const string ConfigurationManagerGuid = "com.bepis.bepinex.configurationmanager";
        private const string SettingFieldDrawerType = "ConfigurationManager.SettingFieldDrawer";

        private const BindingFlags InstanceMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private const BindingFlags StaticMembers =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static void Refresh()
        {
            var instance = FindConfigurationManager();
            if (instance == null) return;

            ClearDrawerCache(instance);
            RebuildSettingList(instance);
        }

        private static BaseUnityPlugin FindConfigurationManager()
        {
            try
            {
                if (!Chainloader.PluginInfos.TryGetValue(ConfigurationManagerGuid, out var info))
                    return null;

                // Null until the chainloader has instantiated the plugin, which can be after
                // Automatics initialises. The window builds its own list when it first opens.
                var instance = info?.Instance;
                return instance ? instance : null;
            }
            catch (Exception e)
            {
                Automatics.Logger?.Debug($"Failed to look up ConfigurationManager\n{e}");
                return null;
            }
        }

        private static void ClearDrawerCache(BaseUnityPlugin instance)
        {
            try
            {
                instance.GetType().Assembly.GetType(SettingFieldDrawerType, false)
                        ?.GetMethod("ClearCache", StaticMembers, null, Type.EmptyTypes, null)
                        ?.Invoke(null, Array.Empty<object>());
            }
            catch (Exception e)
            {
                Automatics.Logger?.Debug($"Failed to clear ConfigurationManager cache\n{e}");
            }
        }

        private static void RebuildSettingList(BaseUnityPlugin instance)
        {
            try
            {
                instance.GetType()
                        .GetMethod("BuildSettingList", InstanceMembers, null, Type.EmptyTypes, null)
                        ?.Invoke(instance, Array.Empty<object>());
            }
            catch (Exception e)
            {
                Automatics.Logger?.Debug(
                    $"Failed to rebuild ConfigurationManager setting list\n{e}");
            }
        }
    }
}
