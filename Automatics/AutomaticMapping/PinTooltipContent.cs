using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Automatics.Valheim;
using HarmonyLib;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    internal static class PinTooltipContent
    {
        internal enum ObjectState { Loaded, Current, Cached, NoData, NoLink, Removed }
        internal enum DetailKind { None, Creature, Pickable, Sections, Health }

        internal sealed class Member
        {
            internal ObjectState State;
            internal DetailKind Kind;
            internal float Health;
            internal float MaxHealth;
            internal int Level;
            internal bool Ready;
            internal double? RespawnSeconds;
            internal int Remaining;
            internal int Total;
        }

        internal sealed class Snapshot
        {
            internal string Name;
            internal float Distance;
            internal bool HasState;
            internal bool MergedFlora;
            internal readonly List<Member> Members = new List<Member>();
        }

        internal sealed class Content
        {
            internal string Name;
            internal string Text;
        }

        internal sealed class SavedObjects
        {
            internal bool MergedFlora;
            internal readonly List<ZDOID> Ids = new List<ZDOID>();
        }

        private static readonly AccessTools.FieldRef<MineRock, Collider[]> RockAreas =
            AccessTools.FieldRefAccess<MineRock, Collider[]>("m_hitAreas");
        private static readonly FieldInfo Rock5Areas = AccessTools.Field(typeof(MineRock5), "m_hitAreas");
        private static readonly FieldInfo Rock5Health = AccessTools.Field(Rock5Areas.FieldType.GetGenericArguments()[0], "m_health");
        private static readonly List<Collider> FootprintColliders = new List<Collider>();
        private static readonly Vector3[] FootprintCorners = new Vector3[8];

        internal static Content Build(Minimap.PinData pin, SavedObjects savedObjects = null)
        {
            return Format(Read(pin, Player.m_localPlayer.transform.position, ZNet.instance.GetTime(), savedObjects),
                key => Automatics.L10N.Translate(key));
        }

        // Hover changes resolve saved pins from local sector data without adopting or changing them.
        internal static SavedObjects ResolveSavedObjects(Minimap.PinData pin)
        {
            var result = new SavedObjects();
            if (DynamicObjectMapping.OwnsPin(pin) || StaticObjectMapping.OwnsPin(pin) ||
                !TryGetSavedResourceCategory(pin, out var category)) return result;

            var label = Localization.instance.Localize(pin.m_name);
            if (category == PinCategory.Flora && PinCategories.TryGetFloraClusterLabel(pin.m_name, out var baseLabel))
            {
                label = baseLabel;
                result.MergedFlora = true;
            }
            var mergeRangeSquared = (float)Config.FloraPinMergeRange * Config.FloraPinMergeRange;
            var nearest = float.MaxValue;
            var candidates = new List<ZDO>();
            var matchingFlora = new List<ZDO>();
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(pin.m_pos),
                new SimulationDistance(1, 0, true), candidates);
            var registry = category == PinCategory.Flora ? ValheimObject.Flora : ValheimObject.Mineral;
            foreach (var zdo in candidates)
            {
                var prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
                if (!prefab) continue;
                var component = category == PinCategory.Flora ? (Component)prefab.GetComponent<Pickable>() :
                    (Component)prefab.GetComponent<MineRock>() ??
                    (Component)prefab.GetComponent<MineRock5>() ?? prefab.GetComponent<Destructible>();
                if (!component || !registry.GetIdentify(Objects.GetName(component), out var identifier) ||
                    !registry.GetName(identifier, out var objectLabel) ||
                    Localization.instance.Localize(Map.StoredPinName(objectLabel)) != label) continue;
                if (result.MergedFlora)
                {
                    matchingFlora.Add(zdo);
                    continue;
                }
                var distance = category == PinCategory.Mineral ? Utils.DistanceXZ(zdo.GetPosition(), pin.m_pos) :
                    Vector3.Distance(zdo.GetPosition(), pin.m_pos);
                var radius = category == PinCategory.Mineral ? MineralSearchRadius(prefab, zdo.GetRotation(), Vector3.zero) :
                    Config.FloraPinMergeRange;
                if (distance > radius || distance > nearest) continue;
                result.Ids.Clear();
                nearest = distance;
                result.Ids.Add(zdo.m_uid);
            }
            if (result.MergedFlora)
            {
                // Seed near the pin, then follow the same three-dimensional neighbour links as flora networks.
                var members = new List<ZDO>();
                for (var i = matchingFlora.Count - 1; i >= 0; i--)
                    if ((matchingFlora[i].GetPosition() - pin.m_pos).sqrMagnitude <= mergeRangeSquared)
                    {
                        members.Add(matchingFlora[i]);
                        matchingFlora.RemoveAt(i);
                    }
                for (var member = 0; member < members.Count; member++)
                    for (var i = matchingFlora.Count - 1; i >= 0; i--)
                        if ((matchingFlora[i].GetPosition() - members[member].GetPosition()).sqrMagnitude <= mergeRangeSquared)
                        {
                            members.Add(matchingFlora[i]);
                            matchingFlora.RemoveAt(i);
                        }
                foreach (var member in members) result.Ids.Add(member.m_uid);
            }
            return result;
        }

        private static bool TryGetSavedResourceCategory(Minimap.PinData pin, out PinCategory category)
        {
            category = default;
            return pin.m_save && pin.m_ownerID == 0 &&
                   (PinCategories.TryClassify(pin, out category) ||
                    (!Map.IsAutomaticPin(pin) && PinCategories.TryClassifyLocalizedName(pin.m_name, out category))) &&
                   (category == PinCategory.Flora || category == PinCategory.Mineral);
        }

        // A mineral deposit's footprint: how far across the ground, at least 4 m, its colliders reach
        // from a point given as its offset from the deposit's position. The source is the deposit's
        // prefab placed with the rotation given, or a live deposit with its own rotation. The tooltip
        // looks for a saved pin's deposit within it, and the static pass for a deposit's saved pin.
        internal static float MineralSearchRadius(GameObject source, Quaternion rotation, Vector3 anchorOffset)
        {
            var radius = 4f;
            var relativeRotation = rotation * Quaternion.Inverse(source.transform.rotation);
            // A live pin's collider centre stays within this footprint, even when only some sections remain.
            // Use shape bounds because inactive prefab colliders have no world-space physics bounds.
            source.GetComponentsInChildren(true, FootprintColliders);
            foreach (var collider in FootprintColliders)
            {
                if (!TryGetShapeCorners(collider, FootprintCorners)) continue;
                foreach (var corner in FootprintCorners)
                {
                    var offset = relativeRotation * (corner - source.transform.position);
                    radius = Mathf.Max(radius, Utils.DistanceXZ(anchorOffset, offset));
                }
            }
            FootprintColliders.Clear();
            return radius;
        }

        // The eight world-space corners of a collider's shape bounds. Unlike Collider.bounds they are
        // there while the collider is off, as on a prefab or a mined hit area; false for other shapes.
        internal static bool TryGetShapeCorners(Collider collider, Vector3[] corners)
        {
            Bounds bounds;
            switch (collider)
            {
                case MeshCollider mesh when mesh.sharedMesh:
                    bounds = mesh.sharedMesh.bounds;
                    break;
                case BoxCollider box:
                    bounds = new Bounds(box.center, box.size);
                    break;
                case SphereCollider sphere:
                    bounds = new Bounds(sphere.center, Vector3.one * sphere.radius * 2f);
                    break;
                case CapsuleCollider capsule:
                    var size = Vector3.one * capsule.radius * 2f;
                    size[capsule.direction] = Mathf.Max(capsule.height, size[capsule.direction]);
                    bounds = new Bounds(capsule.center, size);
                    break;
                default:
                    return false;
            }
            for (var corner = 0; corner < 8; corner++)
            {
                var point = bounds.center + Vector3.Scale(bounds.extents, new Vector3(
                    (corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f,
                    (corner & 4) == 0 ? -1f : 1f));
                corners[corner] = collider.transform.TransformPoint(point);
            }
            return true;
        }

        // Refresh details through existing associations or the ids captured when hover changed.
        internal static Snapshot Read(Minimap.PinData pin, Vector3 playerPosition, DateTime now,
            SavedObjects savedObjects = null)
        {
            var result = new Snapshot
            {
                Name = Localization.instance.Localize(pin.m_name ?? ""),
                Distance = Vector3.ProjectOnPlane(pin.m_pos - playerPosition, Vector3.up).magnitude
            };
            if (DynamicObjectMapping.TryGetPinId(pin, out var id))
            {
                result.HasState = true;
                result.Members.Add(ReadMember(id, now));
            }
            else if (StaticObjectMapping.TryGetPinKeys(pin, out var keys))
            {
                foreach (var key in keys)
                    if (key.IsUniqueId()) result.Members.Add(ReadMember(key.UniqueId, now));
                result.HasState = result.Members.Count > 0;
                result.MergedFlora = result.Members.Count > 1 &&
                    StaticObjectMapping.TryGetPinKind(pin, out var kind) && kind == PinKind.Flora;
            }
            else if (savedObjects != null && savedObjects.Ids.Count > 0)
            {
                result.HasState = true;
                result.MergedFlora = savedObjects.MergedFlora;
                foreach (var savedId in savedObjects.Ids) result.Members.Add(ReadMember(savedId, now));
            }
            else if (TryGetSavedResourceCategory(pin, out _) ||
                     (pin.m_ownerID == 0 && Map.IsAutomaticPin(pin) &&
                     (!PinCategories.TryClassify(pin, out var category) ||
                      category == PinCategory.Animal || category == PinCategory.Monster ||
                      category == PinCategory.Flora || category == PinCategory.Mineral)))
            {
                result.HasState = true;
                result.Members.Add(new Member { State = ObjectState.NoLink });
            }
            return result;
        }

        // Keep the authority decision callable when the smoke checks simulate a client.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool IsServer()
        {
            return ZNet.instance.IsServer();
        }

        internal static Member ReadMember(ZDOID id, DateTime now)
        {
            var live = ZNetScene.instance.FindInstance(id);
            var zdo = live ? live.GetComponent<ZNetView>().GetZDO() : ZDOMan.instance.GetZDO(id);
            var source = live ? live : zdo != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            if (!source || zdo == null) return new Member
            {
                State = IsServer() ? ObjectState.Removed : ObjectState.NoData
            };
            return ReadObject(source, zdo, live != null, now);
        }

        internal static Member ReadObject(GameObject source, ZDO zdo, bool loaded, DateTime now)
        {
            var result = new Member
            {
                State = loaded ? ObjectState.Loaded : IsServer() ? ObjectState.Current : ObjectState.Cached
            };
            var character = source.GetComponent<Character>();
            if (character)
            {
                result.Kind = DetailKind.Creature;
                result.MaxHealth = loaded ? character.GetMaxHealth() : zdo.GetFloat(ZDOVars.s_maxHealth, character.m_health);
                result.Health = loaded ? character.GetHealth() : zdo.GetFloat(ZDOVars.s_health, result.MaxHealth);
                result.Level = loaded ? character.GetLevel() : zdo.GetInt(ZDOVars.s_level, 1);
                return result;
            }
            var pickable = source.GetComponent<Pickable>();
            if (pickable)
            {
                result.Kind = DetailKind.Pickable;
                result.Ready = loaded ? !pickable.GetPicked() : !zdo.GetBool(ZDOVars.s_picked, pickable.m_defaultPicked);
                var pickedTime = zdo.GetLong(ZDOVars.s_pickedTime, 0L);
                if (!result.Ready && pickable.m_respawnTimeMinutes > 0f && pickedTime > 1)
                    result.RespawnSeconds = Math.Max(0d, pickable.m_respawnTimeMinutes * 60d -
                        (now.Ticks - pickedTime) / (double)TimeSpan.TicksPerSecond);
                return result;
            }
            var rock = source.GetComponent<MineRock>();
            if (rock)
            {
                result.Kind = DetailKind.Sections;
                result.Total = loaded ? RockAreas(rock).Length :
                    (rock.m_areaRoot ? rock.m_areaRoot : rock.gameObject).GetComponentsInChildren<Collider>().Length;
                for (var i = 0; i < result.Total; i++)
                    if (zdo.GetFloat("Health" + i, rock.m_health) > 0f) result.Remaining++;
                return result;
            }
            var rock5 = source.GetComponent<MineRock5>();
            if (rock5)
            {
                result.Kind = DetailKind.Sections;
                var package = zdo.GetString(ZDOVars.s_health);
                if (package.Length > 0)
                {
                    var health = new ZPackage(Convert.FromBase64String(package));
                    result.Total = health.ReadInt();
                    for (var i = 0; i < result.Total; i++)
                        if (health.ReadSingle() > 0f) result.Remaining++;
                }
                else if (loaded)
                {
                    var areas = (IList)Rock5Areas.GetValue(rock5);
                    result.Total = areas.Count;
                    foreach (var area in areas) if ((float)Rock5Health.GetValue(area) > 0f) result.Remaining++;
                }
                else
                    result.Remaining = result.Total = source.GetComponentsInChildren<Collider>().Length;
                return result;
            }
            if (source.GetComponent<Destructible>() is Destructible deposit &&
                ValheimObject.Mineral.IsDefined(Objects.GetName(deposit)))
            {
                result.Kind = DetailKind.Health;
                result.MaxHealth = deposit.m_health + Game.m_worldLevel * deposit.m_health * Game.instance.m_worldLevelMineHPMultiplier;
                result.Health = zdo.GetFloat(ZDOVars.s_health, result.MaxHealth);
            }
            return result;
        }

        // Formatting depends only on the captured values and the supplied translations.
        internal static Content Format(Snapshot snapshot, Func<string, string> translate)
        {
            var lines = new List<string>();
            Action<string, object[]> add = (key, args) => lines.Add(string.Format(CultureInfo.InvariantCulture,
                translate("@text_automatic_mapping_tooltip_" + key), args));
            add("distance", new object[] { snapshot.Distance.ToString("0", CultureInfo.InvariantCulture) });
            var outdated = false;
            var unavailable = false;
            var noLink = false;
            var ready = 0;
            var total = 0;
            double? earliest = null;
            foreach (var member in snapshot.Members)
            {
                if (member.State == ObjectState.Removed) continue;
                total++;
                unavailable |= member.State == ObjectState.NoData;
                noLink |= member.State == ObjectState.NoLink;
                if (member.Kind == DetailKind.None) continue;
                outdated |= member.State == ObjectState.Cached;
                if (snapshot.MergedFlora)
                {
                    if (member.Ready) ready++;
                    if (member.RespawnSeconds.HasValue && (!earliest.HasValue || member.RespawnSeconds < earliest))
                        earliest = member.RespawnSeconds;
                    continue;
                }
                switch (member.Kind)
                {
                    case DetailKind.Creature:
                        add("hp", new object[] { member.Health.ToString("0.#", CultureInfo.InvariantCulture), member.MaxHealth.ToString("0.#", CultureInfo.InvariantCulture) });
                        add("level", new object[] { member.Level });
                        break;
                    case DetailKind.Pickable:
                        if (member.Ready) add("ready", new object[0]);
                        else if (member.RespawnSeconds.HasValue) add("respawn", new object[] { Duration(member.RespawnSeconds.Value, translate) });
                        else add("picked", new object[0]);
                        break;
                    case DetailKind.Sections:
                        add("sections", new object[] { member.Remaining, member.Total });
                        break;
                    case DetailKind.Health:
                        add("hp", new object[] { member.Health.ToString("0.#", CultureInfo.InvariantCulture), member.MaxHealth.ToString("0.#", CultureInfo.InvariantCulture) });
                        break;
                }
            }
            if (snapshot.MergedFlora && total > 0)
            {
                add(unavailable ? "ready_known" : "ready_count", new object[] { ready, total });
                if (earliest.HasValue) add("earliest", new object[] { Duration(earliest.Value, translate) });
            }
            if (snapshot.HasState && unavailable) add("not_loaded", new object[0]);
            if (snapshot.HasState && noLink) add("no_link", new object[0]);
            if (outdated) add("outdated", new object[0]);
            return new Content { Name = snapshot.Name, Text = string.Join("\n", lines) };
        }

        private static string Duration(double seconds, Func<string, string> translate)
        {
            return string.Format(CultureInfo.InvariantCulture, translate("@text_automatic_mapping_tooltip_duration"),
                (long)Math.Ceiling(seconds / 60d));
        }
    }
}
