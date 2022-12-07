using ModUtils;
using System;
using System.Linq;

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

            if (Config.AutomaticDoorEnableDisableToggle.IsDown())
            {
                var enabled = Config.EnableAutomaticDoor = !Config.EnableAutomaticDoor;
                var toggleMessage = Config.AutomaticDoorEnableDisableToggleMessage;
                if (toggleMessage != Message.None)
                {
                    var message = Automatics.L10N.Localize("@message_automatic_door_enable_disable_toggle", $"@{(enabled ? "enabled" : "disabled")}");
                    var type = toggleMessage == Message.Center
                        ? MessageHud.MessageType.Center
                        : MessageHud.MessageType.TopLeft;
                    Player.m_localPlayer.Message(type, message);
                }
            }
        }

        public static bool IsAllowAutomaticDoor(Door door)
        {
            var iName = Objects.GetName(door);
            if (ValheimDoor.GetFlag(iName, out var flag) && (Config.AllowAutomaticDoor & flag) != 0) return true;

            var list = Config.AllowAutomaticDoorCustom;
            if (!list.Any()) return false;

            var dName = Automatics.L10N.TranslateInternalName(iName);
            return list.Any(x =>
                L10N.IsInternalName(x)
                    ? iName.Equals(x, StringComparison.Ordinal)
                    : dName.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}