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

        private static void OnPlayerUpdate()
        {
            if (Player.m_localPlayer == null) return;

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