using static Automatics.ModUtils.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using Automatics.ModUtils;
using UnityEngine;

namespace Automatics.AutomaticProcessing
{
    internal static class AutomaticProcessing
    {
        public static bool AutomaticStore(Beehive piece, ZNetView zNetView, int increaseCount)
        {
            Log.Debug(() => $"AutomaticStore: [Beehive, {piece.m_name} ({L10N.Translate(piece.m_name)})]");
            if (!Config.IsAllowAutomaticProcessing(piece.m_name, Type.Store)) return true;

            var honeyCount = zNetView.GetZDO().GetInt("level") + increaseCount;
            if (honeyCount <= 0) return true;

            var honeyItem = piece.m_honeyItem;
            var honeyName = honeyItem.m_itemData.m_shared.m_name;

            var totalStoredCount = 0;
            foreach (var (container, honeyCountBefore) in
                     from x in GetNearbyContainers(piece.m_name, piece.transform.position)
                     let count = x.Item1.GetInventory().CountItems(honeyName)
                     orderby count descending, x.Item2
                     select (x.Item1, count))
            {
                if (totalStoredCount >= honeyCount) break;

                var inventory = container.GetInventory();
                if (!inventory.AddItem(honeyItem.gameObject, honeyCount)) continue;

                var storedHoneyCount = inventory.CountItems(honeyName) - honeyCountBefore;
                Log.Debug(() =>
                    L10N.Localize(
                        $"Storing {honeyName} x{storedHoneyCount} from {piece.m_name} {piece.transform.position} into {container.m_name} {container.transform.position}"));
                totalStoredCount += storedHoneyCount;
            }

            zNetView.GetZDO().Set("level", Mathf.Clamp(honeyCount - totalStoredCount, 0, piece.m_maxHoney));
            return false;
        }

        public static void AutomaticCraft(CookingStation piece, ZNetView zNetView)
        {
            Log.Debug(() => $"AutomaticCraft: [CookingStation, {piece.m_name} ({L10N.Translate(piece.m_name)})]");
            if (!Config.IsAllowAutomaticProcessing(piece.m_name, Type.Craft)) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;
            if (piece.m_requireFire && !Reflection.InvokeMethod<bool>(piece, "IsFireLit")) return;

            var freeSlot = -1;
            for (var slot = 0; slot < piece.m_slots.Length; slot++)
            {
                if (zNetView.GetZDO().GetString("slot" + slot) != "") continue;
                freeSlot = slot;
                break;
            }

            if (freeSlot < 0) return;

            var containersWithInventory =
                (from x in GetNearbyContainers(piece.m_name, piece.transform.position)
                    orderby x.Item2
                    select (x.Item1, x.Item1.GetInventory()))
                .ToList();
            if (containersWithInventory.Count == 0) return;

            foreach (var conversion in piece.m_conversion)
            {
                foreach (var (container, inventory) in containersWithInventory)
                {
                    var item = inventory.GetItem(conversion.m_from.m_itemData.m_shared.m_name);
                    if (item == null) continue;

                    inventory.RemoveOneItem(item);
                    zNetView.InvokeRPC("AddItem", item.m_dropPrefab.name);
                    Log.Debug(() =>
                        L10N.Localize(
                            $"Crafting {item.m_shared.m_name} from {container.m_name} {container.transform.position} into {piece.m_name} {piece.transform.position}"));
                    goto CRAFT_DONE;
                }
            }

            CRAFT_DONE: ;
        }

        public static void AutomaticRefuel(CookingStation piece, ZNetView zNetView)
        {
            if (!piece.m_useFuel) return;
            Log.Debug(() => $"AutomaticRefuel: [CookingStation, {piece.m_name} ({L10N.Translate(piece.m_name)})]");
            if (!Config.IsAllowAutomaticProcessing(piece.m_name, Type.Refuel)) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;
            if (zNetView.GetZDO().GetFloat("fuel") > piece.m_maxFuel - 1f) return;

            var fuelName = piece.m_fuelItem.m_itemData.m_shared.m_name;
            var container = (from x in GetNearbyContainers(piece.m_name, piece.transform.position)
                    where x.Item1.GetInventory().HaveItem(fuelName)
                    orderby x.Item2
                    select x.Item1)
                .FirstOrDefault();
            if (!container) return;

            container.GetInventory().RemoveItem(fuelName, 1);
            zNetView.InvokeRPC("AddFuel");
            Log.Debug(() =>
                L10N.Localize(
                    $"Refueling {fuelName} from {container.m_name} {container.transform.position} into {piece.m_name} {piece.transform.position}"));
        }

        public static void AutomaticStore(CookingStation piece, ZNetView zNetView)
        {
            Log.Debug(() => $"AutomaticStore: [CookingStation, {piece.m_name} ({L10N.Translate(piece.m_name)})]");
            if (!Config.IsAllowAutomaticProcessing(piece.m_name, Type.Store)) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;

            var doneItems = new List<(int, string)>(piece.m_slots.Length);
            for (var i = 0; i < piece.m_slots.Length; i++)
            {
                var slotItem = zNetView.GetZDO().GetString("slot" + i);
                if (string.IsNullOrEmpty(slotItem)) continue;
                if (!Reflection.InvokeMethod<bool>(piece, "IsItemDone", slotItem)) continue;

                doneItems.Add((i, slotItem));
            }

            if (doneItems.Count == 0) return;

            var containersWithDistance = GetNearbyContainers(piece.m_name, piece.transform.position).ToList();
            foreach (var (slot, itemName) in doneItems)
            {
                var conversion = piece.m_conversion.FirstOrDefault(x => x.m_to.gameObject.name == itemName);
                if (conversion == null) continue;

                var item = conversion.m_to;
                var container = (from x in containersWithDistance
                        orderby x.Item1.GetInventory().CountItems(item.m_itemData.m_shared.m_name) descending, x.Item2
                        select x.Item1)
                    .FirstOrDefault(x => x.GetInventory().AddItem(item.gameObject, 1));
                if (container == null) continue;

                zNetView.GetZDO().Set("slot" + slot, "");
                zNetView.GetZDO().Set("slot" + slot, 0f);
                zNetView.GetZDO().Set("slotstatus" + slot, 0);
                Log.Debug(() =>
                    L10N.Localize(
                        $"Storing {item.m_itemData.m_shared.m_name} from {piece.m_name} {piece.transform.position} into {container.m_name} {container.transform.position}"));
            }
        }

        public static void AutomaticCraft(CraftingStation piece, ZNetView zNetView)
        {
            if (!Config.IsAllowAutomaticProcessing(piece.m_name, Type.Craft)) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;
            Log.Debug(() => $"AutomaticCraft: [CraftingStation, {piece.m_name} ({L10N.Translate(piece.m_name)})]");
        }

        public static void AutomaticCraft(Fermenter piece, ZNetView zNetView)
        {
            Log.Debug(() => $"AutomaticCraft: [Fermenter, {piece.m_name} ({L10N.Translate(piece.m_name)})]");
            if (!Config.IsAllowAutomaticProcessing(piece.m_name, Type.Craft)) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;
            if (Reflection.InvokeMethod<int>(piece, "GetStatus") != 0) return;

            foreach (var container in from x in GetNearbyContainers(piece.m_name, piece.transform.position)
                     orderby x.Item2
                     select x.Item1)
            {
                var inventory = container.GetInventory();

                var baseItem = (from x in piece.m_conversion
                    let item = inventory.GetItem(x.m_from.m_itemData.m_shared.m_name)
                    where item != null
                    select item).FirstOrDefault();
                if (baseItem == null) continue;

                inventory.RemoveOneItem(baseItem);
                zNetView.InvokeRPC("AddItem", baseItem.m_dropPrefab.name);
                Log.Debug(() =>
                    L10N.Localize(
                        $"Crafting {baseItem.m_shared.m_name} from {container.m_name} {container.transform.position} into {piece.m_name} {piece.transform.position}"));
                break;
            }
        }

        public static void AutomaticStore(Fermenter piece, ZNetView zNetView)
        {
            Log.Debug(() => $"AutomaticStore: [Fermenter, {piece.m_name} ({L10N.Translate(piece.m_name)})]");
            if (!Config.IsAllowAutomaticProcessing(piece.m_name, Type.Store)) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;
            if (Reflection.InvokeMethod<int>(piece, "GetStatus") != 3) return;

            var item = zNetView.GetZDO().GetString("Content");

            zNetView.GetZDO().Set("Content", "");
            zNetView.GetZDO().Set("StartTime", 0);
            piece.m_spawnEffects.Create(piece.m_outputPoint.transform.position, Quaternion.identity);

            var conversion = piece.m_conversion.FirstOrDefault(x => x.m_from.gameObject.name == item);
            if (conversion == null) return;

            var totalStoredCount = 0;
            foreach (var (container, itemCountBefore) in
                     from x in GetNearbyContainers(piece.m_name, piece.transform.position)
                     let count = x.Item1.GetInventory().CountItems(conversion.m_to.m_itemData.m_shared.m_name)
                     orderby count descending, x.Item2
                     select (x.Item1, count))
            {
                if (totalStoredCount >= conversion.m_producedItems) break;

                var inventory = container.GetInventory();
                if (!inventory.AddItem(conversion.m_to.gameObject, conversion.m_producedItems)) continue;

                var storedItemCount =
                    inventory.CountItems(conversion.m_to.m_itemData.m_shared.m_name) - itemCountBefore;
                Log.Debug(() =>
                    L10N.Localize(
                        $"Storing {conversion.m_to.m_itemData.m_shared.m_name} x{storedItemCount} from {piece.m_name} {piece.transform.position} into {container.m_name} {container.transform.position}"));
                totalStoredCount += storedItemCount;
            }
        }

        public static void AutomaticRefuel(Fireplace fire, Piece piece, ZNetView zNetView)
        {
            Log.Debug(() => $"AutomaticCraft: [Fireplace, {piece.m_name} ({L10N.Translate(piece.m_name)})]");
            if (!Config.IsAllowAutomaticProcessing(piece.m_name, Type.Refuel)) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;
            if (Mathf.CeilToInt(zNetView.GetZDO().GetFloat("fuel")) >= fire.m_maxFuel) return;

            var fuelName = fire.m_fuelItem.m_itemData.m_shared.m_name;
            var container = (from x in GetNearbyContainers(piece.m_name, fire.transform.position)
                    where x.Item1.GetInventory().HaveItem(fuelName)
                    orderby x.Item2
                    select x.Item1)
                .FirstOrDefault();
            if (!container) return;

            container.GetInventory().RemoveItem(fuelName, 1);
            zNetView.InvokeRPC("AddFuel");
            Log.Debug(() =>
                L10N.Localize(
                    $"Refueling {fuelName} from {container.m_name} {container.transform.position} into {piece.m_name} {fire.transform.position}"));
        }

        public static void AutomaticCraft(Smelter piece, ZNetView zNetView)
        {
            Log.Debug(() => $"AutomaticCraft: [Smelter, {piece.m_name} ({L10N.Translate(piece.m_name)})]");
            if (!Config.IsAllowAutomaticProcessing(piece.m_name, Type.Craft)) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;

            var materialCount = zNetView.GetZDO().GetInt("queued");
            if (materialCount >= piece.m_maxOre) return;

            var tailMaterial = materialCount <= 0 ? "" : zNetView.GetZDO().GetString("item" + (materialCount - 1));
            var material = !string.IsNullOrEmpty(tailMaterial)
                ? piece.m_conversion.FirstOrDefault(x => x.m_from.gameObject.name == tailMaterial)
                : null;
            foreach (var container in from x in GetNearbyContainers(piece.m_name, piece.transform.position)
                     orderby x.Item2
                     select x.Item1)
            {
                var inventory = container.GetInventory();

                var oreItem = material != null
                    ? inventory.GetItem(material.m_from.m_itemData.m_shared.m_name)
                    : (from x in piece.m_conversion
                        let item = inventory.GetItem(x.m_from.m_itemData.m_shared.m_name)
                        where item != null
                        select item).FirstOrDefault();
                if (oreItem == null) continue;

                inventory.RemoveOneItem(oreItem);
                zNetView.InvokeRPC("AddOre", oreItem.m_dropPrefab.name);
                Log.Debug(() =>
                    L10N.Localize(
                        $"Crafting {oreItem.m_shared.m_name} from {container.m_name} {container.transform.position} into {piece.m_name} {piece.transform.position}"));
                break;
            }
        }

        public static void AutomaticRefuel(Smelter piece, ZNetView zNetView)
        {
            Log.Debug(() => $"AutomaticRefuel: [Smelter, {piece.m_name} ({L10N.Translate(piece.m_name)})]");
            if (!Config.IsAllowAutomaticProcessing(piece.m_name, Type.Refuel)) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;
            if (zNetView.GetZDO().GetFloat("fuel") >= piece.m_maxFuel - 1) return;

            var fuelName = piece.m_fuelItem.m_itemData.m_shared.m_name;
            var container = (from x in GetNearbyContainers(piece.m_name, piece.transform.position)
                    where x.Item1.GetInventory().HaveItem(fuelName)
                    orderby x.Item2
                    select x.Item1)
                .FirstOrDefault();
            if (!container) return;

            container.GetInventory().RemoveItem(fuelName, 1);
            zNetView.InvokeRPC("AddFuel");
            Log.Debug(() =>
                L10N.Localize(
                    $"Refueling {fuelName} from {container.m_name} {container.transform.position} into {piece.m_name} {piece.transform.position}"));
        }

        public static bool AutomaticStore(Smelter piece, string ore, int stack)
        {
            Log.Debug(() => $"AutomaticStore: [Smelter, {piece.m_name} ({L10N.Translate(piece.m_name)})]");
            if (!Config.IsAllowAutomaticProcessing(piece.m_name, Type.Store)) return true;

            var conversion = piece.m_conversion.FirstOrDefault(x => x.m_from.gameObject.name == ore);
            if (conversion == null) return true;

            var transform = piece.transform;
            var item = conversion.m_to;
            var containers =
                from x in GetNearbyContainers(piece.m_name, transform.position)
                let count = x.Item1.GetInventory().CountItems(item.m_itemData.m_shared.m_name)
                orderby count descending, x.Item2
                select (x.Item1, count);

            var storedCount = 0;
            foreach (var (container, itemCountBefore) in containers)
            {
                if (storedCount >= stack) break;

                var inventory = container.GetInventory();
                if (!inventory.AddItem(item.gameObject, stack)) continue;

                var storedItemCount = inventory.CountItems(item.m_itemData.m_shared.m_name) - itemCountBefore;
                Log.Debug(() =>
                    L10N.Localize(
                        $"Storing {item.m_itemData.m_shared.m_name} x{storedItemCount} from {piece.m_name} {transform.position} into {container.m_name} {container.transform.position}"));
                storedCount += storedItemCount;
            }

            if (storedCount == 0) return true;

            piece.m_produceEffects.Create(transform.position, transform.rotation);
            return false;
        }

        private static IEnumerable<(Container, float)> GetNearbyContainers(string target, Vector3 origin)
        {
            var range = Config.GetContainerSearchRange(target);
            return range <= 0
                ? Enumerable.Empty<(Container, float)>()
                : from x in ContainerCache.GetAllInstance()
                let distance = Vector3.Distance(origin, x.transform.position)
                where distance <= range
                select (x, distance);
        }
    }

    internal static class Target
    {
        //public const string ArtisanTable = "$piece_artisanstation";
        public const string Beehive = "$piece_beehive";
        public const string Bonfire = "$piece_bonfire";
        public const string BlastFurnace = "$piece_blastfurnace";
        public const string Campfire = "$piece_firepit";
        //public const string Cauldron = "$piece_cauldron";
        public const string CharcoalKiln = "$piece_charcoalkiln";
        public const string CookingStation = "$piece_cookingstation";
        public const string Fermenter = "$piece_fermenter";
        //public const string Forge = "$piece_forge";
        public const string HangingBrazier = "$piece_brazierceiling01";
        public const string Hearth = "$piece_hearth";
        public const string IronCookingStation = "$piece_cookingstation_iron";
        public const string JackOTurnip = "$piece_jackoturnip";
        public const string Sconce = "$piece_sconce";
        public const string Smelter = "$piece_smelter";
        public const string SpinningWheel = "$piece_spinningwheel";
        public const string StandingBlueBurningIronTorch = "$piece_groundtorchblue";
        public const string StandingGreenBurningIronTorch = "$piece_groundtorchgreen";
        public const string StandingIronTorch = "$piece_groundtorch";
        public const string StandingWoodTorch = "$piece_groundtorchwood";
        public const string StoneOven = "$piece_oven";
        //public const string Stonecutter = "$piece_stonecutter";
        public const string Windmill = "$piece_windmill";
        //public const string Workbench = "$piece_workbench";

        public static readonly IList<string> All;

        static Target()
        {
            All = Array.AsReadOnly(new[]
            {
                //ArtisanTable,
                Beehive,
                Bonfire,
                BlastFurnace,
                Campfire,
                //Cauldron,
                CharcoalKiln,
                CookingStation,
                Fermenter,
                //Forge,
                HangingBrazier,
                Hearth,
                IronCookingStation,
                JackOTurnip,
                Sconce,
                Smelter,
                SpinningWheel,
                StandingBlueBurningIronTorch,
                StandingGreenBurningIronTorch,
                StandingIronTorch,
                StandingWoodTorch,
                StoneOven,
                //Stonecutter,
                Windmill,
                //Workbench,
            });
        }
    }

    [Flags]
    internal enum Type : long
    {
        None = 0,

        [LocalizedDescription("@config_automatic_processing_type_craft")]
        Craft = 1L << 0,

        [LocalizedDescription("@config_automatic_processing_type_refuel")]
        Refuel = 1L << 1,

        [LocalizedDescription("@config_automatic_processing_type_store")]
        Store = 1L << 2,

        [LocalizedDescription("@select_all")]
        All = -1L,
    }
}