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
                var enabled = !Config.AutomaticDoorEnabled;
                Config.SetAutomaticDoorEnabled(enabled);

                var message = L10N.Localize("@message_toggle_automatic_door",
                    $"@config_checkbox_label_{(enabled ? "true" : "false")}");
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, message);
            }
        }
    }
}