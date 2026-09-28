using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Automatics.Valheim;
using BepInEx.Configuration;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticStorage
{
    internal static class Config
    {
        private const string Section = "automatic_storage";

        private static ConfigEntry<AutomaticsModule> _module;
        private static ConfigEntry<int> _storageSearchRange;
        private static ConfigEntry<int> _containerReferenceLimit;
        private static ConfigEntry<StringList> _allowContainer;
        private static ConfigEntry<StringList> _allowedItemTypes;
        private static ConfigEntry<StringList> _excludedItems;
        private static ConfigEntry<bool> _storeHotbarItems;
        private static ConfigEntry<KeyboardShortcut> _storeItemsKey;
        private static ConfigEntry<Message> _storageMessage;

        /// <summary>
        ///     The toggle the module registry follows. It is exposed so the registry can read the
        ///     user's choice and subscribe to its changes; nothing writes it back (R1.3b), and no
        ///     hot path reads it either - those read the applied state on <see cref="Module" />
        ///     instead (R1.5).
        /// </summary>
        public static ConfigEntry<AutomaticsModule> ModuleEntry => _module;
        public static int StorageSearchRange => _storageSearchRange.Value;
        public static int ContainerReferenceLimit => _containerReferenceLimit.Value;
        public static StringList AllowContainer => _allowContainer.Value;
        public static StringList AllowedItemTypes => _allowedItemTypes.Value;
        public static StringList ExcludedItems => _excludedItems.Value;
        public static bool StoreHotbarItems => _storeHotbarItems.Value;
        public static KeyboardShortcut StoreItemsKey => _storeItemsKey.Value;
        public static Message StorageMessage => _storageMessage.Value;

        public static void Initialize()
        {
            var config = global::Automatics.Config.Instance;

            config.ChangeSection(Section);
            _module = config.Bind("module", AutomaticsModule.Disabled, initializer: x =>
            {
                x.DispName =
                    Automatics.L10N.Translate("@config_common_disable_module_runtime_name");
                x.Description =
                    Automatics.L10N.Translate("@config_common_disable_module_runtime_description");
            });

            // Bound unconditionally (R5.1). The module can be turned on while the game runs, so
            // the settings it needs have to exist whether it starts on or off; the early return
            // that used to sit here left them null and made a runtime enable impossible. Nothing
            // below has a side effect beyond the bind itself - this section has no
            // SettingChanged bridge - so binding a disabled module costs nothing but the lines
            // it writes to the config file. Those lines also pick up any value the user set
            // before turning the module off, and that value applies as soon as it is turned on.
            _storageSearchRange = config.Bind("storage_search_range", 8, (1, 64));
            _containerReferenceLimit = config.Bind("container_reference_limit", 0, (0, 999));
            _allowContainer = config.BindValheimObjectList("allow_container",
                ValheimObject.Container, excludes: new[] { "PieceChestPrivate" });
            _allowedItemTypes = config.Bind("allowed_item_types",
                new StringList(GetAllItemTypeKeys()), initializer: x =>
                {
                    x.CustomDrawer = ConfigurationCustomDrawer.MultiSelect(
                        GetAllItemTypeKeys,
                        key =>
                        {
                            var translationKey = "config_automatic_storage_item_type_" + key;
                            var label = Automatics.L10N.Translate("@" + translationKey);
                            return label == Automatics.L10NPrefix + "_" + translationKey
                                ? key
                                : label;
                        });
                });
            _excludedItems = config.Bind("excluded_items", new StringList());
            _storeHotbarItems = config.Bind("store_hotbar_items", false);
            _storeItemsKey = config.Bind("store_items_key", new KeyboardShortcut());
            _storageMessage = config.Bind("storage_message", Message.Center);
        }

        public static bool IsAllowedItemType(ItemDrop.ItemData.ItemType itemType)
        {
            var key = GetItemTypeKey(itemType);
            var numericKey = Convert.ToInt32(itemType).ToString(CultureInfo.InvariantCulture);
            return AllowedItemTypes.Contains(key) || AllowedItemTypes.Contains(numericKey);
        }

        public static string GetItemTypeKey(ItemDrop.ItemData.ItemType itemType)
        {
            return Enum.IsDefined(typeof(ItemDrop.ItemData.ItemType), itemType)
                ? itemType.ToString()
                : Convert.ToInt32(itemType).ToString(CultureInfo.InvariantCulture);
        }

        private static IEnumerable<string> GetAllItemTypeKeys()
        {
            return Enum.GetValues(typeof(ItemDrop.ItemData.ItemType))
                       .Cast<ItemDrop.ItemData.ItemType>()
                       .Where(x => x != ItemDrop.ItemData.ItemType.None)
                       .Select(GetItemTypeKey);
        }
    }
}
