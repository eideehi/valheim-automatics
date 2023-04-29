using System.Collections.Generic;
using System.Linq;
using ModUtils;

namespace Automatics.AutomaticProcessing
{
    internal static class TurretProcess
    {
        private static readonly Dictionary<int, float> ChargeTimers;

        static TurretProcess()
        {
            ChargeTimers = new Dictionary<int, float>();
        }

        private static ItemDrop.ItemData FindAmmoItem(Turret turret, Inventory inventory,
            bool onlyCurrentlyLoadableType)
        {
            return Reflections.InvokeMethod<ItemDrop.ItemData>(turret, "FindAmmoItem", inventory,
                onlyCurrentlyLoadableType);
        }

        private static bool CanCharge(Turret turret, float delta)
        {
            var instanceID = turret.GetInstanceID();
            if (!ChargeTimers.TryGetValue(instanceID, out var timer))
            {
                ChargeTimers.Add(instanceID, 0f);
                timer = 0f;
            }

            timer += delta;
            if (timer < 1f)
            {
                ChargeTimers[instanceID] = timer;
                return false;
            }

            ChargeTimers[instanceID] = 0f;
            return true;
        }

        public static void Charge(Turret turret, ZNetView zNetView, float delta)
        {
            if (!Config.EnableAutomaticProcessing) return;

            var turretName = turret.m_name;
            if (!Logics.IsAllowProcessing(turretName, Process.Charge)) return;

            if (!CanCharge(turret, delta)) return;
            if (turret.GetAmmo() >= turret.m_maxAmmo) return;

            var minAmmoCount = Config.NumberOfItemsToStopCharge(turretName);
            var origin = turret.transform.position;
            foreach (var (container, _) in Logics.GetNearbyContainers(turretName, origin))
            {
                var inventory = container.GetInventory();
                var item = FindAmmoItem(turret, inventory, true);
                if (item == null && turret.GetAmmo() == 0)
                {
                    foreach (var ammoName in turret.m_allowedAmmo.Select(type =>
                                 type.m_ammo.m_itemData.m_shared.m_name))
                    {
                        item = inventory.GetAmmoItem(ammoName);
                        if (item != null) break;
                    }
                }

                if (item == null) continue;
                if (!Inventories.HaveItem(inventory, item.m_shared.m_name, minAmmoCount + 1))
                    continue;

                inventory.RemoveItem(item, 1);
                zNetView.InvokeRPC("RPC_AddAmmo", item.m_dropPrefab.name);

                Logics.ChargeLog(item.m_shared.m_name, 1, turretName, origin, container.m_name,
                    container.transform.position);
                break;
            }
        }

        public static void ClearTimer(Turret turret)
        {
            ChargeTimers.Remove(turret.GetInstanceID());
        }
    }
}