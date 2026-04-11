using System;
using HarmonyLib;

namespace Automatics
{
    internal static class ConfigurationManagerBridge
    {
        public static void Refresh()
        {
            ClearDrawerCache();
            RebuildSettingList();
        }

        private static void ClearDrawerCache()
        {
            try
            {
                AccessTools.Method("ConfigurationManager.SettingFieldDrawer:ClearCache")
                           ?.Invoke(null, Array.Empty<object>());
            }
            catch (Exception e)
            {
                Automatics.Logger?.Debug($"Failed to clear ConfigurationManager cache\n{e}");
            }
        }

        private static void RebuildSettingList()
        {
            try
            {
                var type = AccessTools.TypeByName("ConfigurationManager.ConfigurationManager");
                if (type == null) return;

                var instance = AccessTools.Property(type, "Instance")?.GetValue(null, null);
                AccessTools.Method(type, "BuildSettingList")?.Invoke(instance,
                    Array.Empty<object>());
            }
            catch (Exception e)
            {
                Automatics.Logger?.Debug(
                    $"Failed to rebuild ConfigurationManager setting list\n{e}");
            }
        }
    }
}
