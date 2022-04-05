using System.Linq;
using Automatics.ModUtils;

namespace Automatics.AutomaticDoor
{
    internal static class Core
    {
        public static void Initialize()
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
                var message = L10N.Localize("@message_toggle_automatic_door",
                    $"@config_checkbox_label_{(enabled ? "true" : "false")}");
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, message);
            }
        }

        public static bool IsAllowAutomaticDoor(Door door)
        {
            var name = Obj.GetName(door);
            if (ValheimDoor.GetFlag(name, out var flag) && (Config.AllowAutomaticDoor & flag) != 0) return true;

            var list = Config.AllowAutomaticDoorCustom;
            if (!list.Any()) return false;

            var localizedName = L10N.TranslateInternalNameOnly(name);
            return list.Any(x => L10N.TranslateInternalNameOnly(x) == localizedName);
        }
    }
}