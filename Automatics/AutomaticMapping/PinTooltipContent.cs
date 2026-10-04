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

        private static readonly AccessTools.FieldRef<MineRock, Collider[]> RockAreas =
            AccessTools.FieldRefAccess<MineRock, Collider[]>("m_hitAreas");
        private static readonly FieldInfo Rock5Areas = AccessTools.Field(typeof(MineRock5), "m_hitAreas");
        private static readonly FieldInfo Rock5Health = AccessTools.Field(Rock5Areas.FieldType.GetGenericArguments()[0], "m_health");

        internal static Content Build(Minimap.PinData pin)
        {
            return Format(Read(pin, Player.m_localPlayer.transform.position, ZNet.instance.GetTime()),
                key => Automatics.L10N.Translate(key));
        }

        // Reading the association never discovers an object or changes its pin.
        internal static Snapshot Read(Minimap.PinData pin, Vector3 playerPosition, DateTime now)
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
            else if (Map.IsAutomaticPin(pin) &&
                     (!PinCategories.TryClassify(pin, out var category) ||
                      category == PinCategory.Animal || category == PinCategory.Monster ||
                      category == PinCategory.Flora || category == PinCategory.Mineral))
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
