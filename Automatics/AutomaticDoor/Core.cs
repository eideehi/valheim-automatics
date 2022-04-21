using System;
using System.Linq;
using Automatics.ModUtils;

namespace Automatics.AutomaticDoor
{
    internal static class Core
    {
        [AutomaticsInitializer(1)]
        private static void Initialize()
        {
            Config.Initialize();
            Automatics.OnPlayerUpdate += OnPlayerUpdate;
        }

        private static void OnPlayerUpdate(Player player, bool takeInput)
        {
            if (player == null || Player.m_localPlayer != player) return;
            if (!takeInput) return;

            if (Config.ToggleAutomaticDoorEnabledKey.IsDown())
            {
                var enabled = Config.AutomaticDoorEnabled = !Config.AutomaticDoorEnabled;
                var message = L10N.Localize("@message_toggle_automatic_door", $"@{(enabled ? "enabled" : "disabled")}");
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, message);
            }
        }

        public static bool IsAllowAutomaticDoor(Door door)
        {
            var iName = Obj.GetName(door);
            if (ValheimDoor.GetFlag(iName, out var flag) && (Config.AllowAutomaticDoor & flag) != 0) return true;

            var list = Config.AllowAutomaticDoorCustom;
            if (!list.Any()) return false;

            var dName = L10N.TranslateInternalNameOnly(iName);
            return list.Any(x =>
                L10N.IsInternalName(x)
                    ? iName.Equals(x, StringComparison.Ordinal)
                    : dName.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}