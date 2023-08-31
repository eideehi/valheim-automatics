using HarmonyLib;

namespace Automatics.AutomaticDoor
{
    internal static class Module
    {
        [AutomaticsInitializer(1)]
        private static void Initialize()
        {
            Config.Initialize();
            if (Config.ModuleDisabled) return;

            Hooks.OnPlayerUpdate += OnPlayerUpdate;
            Harmony.CreateAndPatchAll(typeof(Patches), Automatics.GetHarmonyId("automatic-door"));
        }

        private static void OnPlayerUpdate(Player player, bool takeInput)
        {
            if (Player.m_localPlayer != player || !player.IsOwner()) return;
            if (!takeInput) return;

            if (Config.AutomaticDoorEnableDisableToggle.IsDown())
            {
                Config.EnableAutomaticDoor = !Config.EnableAutomaticDoor;
                var messagePosition = Config.AutomaticDoorEnableDisableToggleMessage;
                if (messagePosition != Message.None)
                {
                    var message = Automatics.L10N.Localize(
                        "@message_automatic_door_enable_disable_toggle",
                        Config.EnableAutomaticDoor ? "@enabled" : "@disabled");
                    var type = messagePosition == Message.Center
                        ? MessageHud.MessageType.Center
                        : MessageHud.MessageType.TopLeft;
                    player.Message(type, message);
                }
            }
        }
    }
}