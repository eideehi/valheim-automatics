using System;
using System.Collections.Generic;
using HarmonyLib;
using ModUtils;

namespace Automatics.AutomaticProcessing
{
    internal static class TurretProcess
    {
        private static readonly Dictionary<int, float> ChargeTimers;

        // Turret.FindAmmoItem resolved once into a cached delegate so the per-
        // charge-tick ammo lookup does not allocate a Traverse + params array per
        // nearby container. Guarded bind falls back to reflection if it is renamed.
        private static readonly Func<Turret, Inventory, bool, ItemDrop.ItemData> FindAmmo;

        static TurretProcess()
        {
            ChargeTimers = new Dictionary<int, float>();
            FindAmmo = BindFindAmmoItem();
        }

        private static Func<Turret, Inventory, bool, ItemDrop.ItemData> BindFindAmmoItem()
        {
            try
            {
                var method = AccessTools.Method(typeof(Turret), "FindAmmoItem",
                    new[] { typeof(Inventory), typeof(bool) });
                if (method != null)
                    return AccessTools
                        .MethodDelegate<Func<Turret, Inventory, bool, ItemDrop.ItemData>>(method);
                Automatics.Logger.Warning(() =>
                    "Turret.FindAmmoItem not found; falling back to reflection.");
            }
            catch (Exception e)
            {
                Automatics.Logger.Warning(() =>
                    $"Failed to bind Turret.FindAmmoItem delegate; falling back to reflection: {e.Message}");
            }

            return null;
        }

        // Turret.OnDestroyed only fires when a turret is destroyed by damage, so
        // timers for turrets despawned by zone unload survive in ChargeTimers and
        // accumulate over a session. Instance IDs are also never reused across
        // logins, so without this reset the dictionary grows for the whole game
        // process lifetime.
        public static void Cleanup()
        {
            ChargeTimers.Clear();
        }

        private static ItemDrop.ItemData FindAmmoItem(Turret turret, Inventory inventory,
            bool onlyCurrentlyLoadableType)
        {
            return FindAmmo != null
                ? FindAmmo(turret, inventory, onlyCurrentlyLoadableType)
                : Reflections.InvokeMethod<ItemDrop.ItemData>(turret, "FindAmmoItem", inventory,
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
            if (!Module.Running) return;
            if (!Config.EnableAutomaticProcessing) return;
            if (!Objects.HasValidOwnership(zNetView)) return;

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
                    foreach (var ammo in turret.m_allowedAmmo)
                    {
                        item = inventory.GetAmmoItem(ammo.m_ammo.m_itemData.m_shared.m_name);
                        if (item != null) break;
                    }
                }

                if (item == null) continue;
                if (!Inventories.HaveItem(inventory, item.m_shared.m_name, 0, WorldLevelMatchMode.Ignore, minAmmoCount + 1))
                    continue;
                if (!Logics.TryClaimContainer(container)) continue;

                if (!Logics.TryRemoveItem(inventory, item, out var prefabName)) continue;
                zNetView.InvokeRPC("RPC_AddAmmo", prefabName);

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
