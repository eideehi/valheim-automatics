using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Automatics.AutomaticMapping;
using BepInEx.Configuration;
using HarmonyLib;
using ModUtils;
using UnityEngine;
using MappingConfig = Automatics.AutomaticMapping.Config;
using MappingModule = Automatics.AutomaticMapping.Module;

namespace Automatics.SmokeTest
{
    // Adds unsaved test pins at known bearings and distances from the player, turns the camera to
    // known headings and runs the compass update in the same frame, then reads the strip back:
    // the letters and markers at the x their bearing gives, the range and view cuts, the cap on
    // the pins in view, the icon-type toggle, the category and unexplored filters with a manual
    // pin as control, hiding on the large map, without the small map and with the HUD hidden,
    // and the strip's place on screen. Everything runs in one frame, so no scan can move or
    // remove a pin between a change and its check.
    //
    // The test pins are unsaved, so no map save keeps them, and they are the only pins the
    // section removes. The camera, the map mode, the HUD, the minimap's icon filter and the
    // settings it changes are put back when the section ends, and by the session hand-back when
    // the run is cut short.
    internal static class CompassFeatureSmoke
    {
        // The range the section sets for its pins, and how far a placed x may be off, in canvas
        // units.
        private const float TestRange = 80f;
        private const float MaxRange = 300f;
        private const float Tolerance = 1f;
        private const float HalfView = Compass.FieldOfView / 2f;

        private static readonly FieldInfo RefreshAtField = AccessTools.Field(typeof(Compass), "_refreshAt");
        private static readonly FieldInfo VisibleIconTypesField =
            AccessTools.Field(typeof(Minimap), "m_visibleIconTypes");
        private static readonly FieldInfo SharedFadeField = AccessTools.Field(typeof(Minimap), "m_sharedMapDataFade");
        private static readonly Type ImageType = AccessTools.TypeByName("UnityEngine.UI.Image");
        private static readonly PropertyInfo SpriteProperty =
            ImageType == null ? null : AccessTools.Property(ImageType, "sprite");
        private static readonly Type TextType =
            AccessTools.Property(typeof(Minimap.PinNameData), "PinNameText").PropertyType;
        private static readonly PropertyInfo TextProperty = AccessTools.Property(TextType, "text");

        // N, E, S and W, as the English and the Japanese translations write them.
        private static readonly string[][] LetterTexts =
        {
            new[] { "N", "北" }, new[] { "E", "東" }, new[] { "S", "南" }, new[] { "W", "西" }
        };

        internal static IEnumerator Run(FeatureRun run)
        {
            var player = Player.m_localPlayer;
            var map = Minimap.instance;
            var hud = Hud.instance;
            if (!run.IsCurrent || !player || !player.IsOwner() || !map || !hud || !EnemyHud.instance ||
                !GameCamera.instance || Localization.instance == null)
            {
                run.Skip("compass_section", "requires a loaded world with the local player, minimap, HUD and camera");
                yield break;
            }
            if (!MappingModule.Running)
            {
                run.Skip("compass_section", "Automatic Mapping is not running; enable the module first");
                yield break;
            }
            if (Game.m_noMap)
            {
                run.Skip("compass_section", "the world has no map, so the compass is never shown");
                yield break;
            }
            if (!Navigation.CanShowHud(player) || map.m_mode != Minimap.MapMode.Small)
            {
                run.Skip("compass_section", "the game must be unpaused, the HUD shown and the large map closed");
                yield break;
            }
            if (MapFilter.SearchActive)
            {
                run.Skip("compass_section",
                    "a pin search applies to the minimap; clear it or turn off Apply to minimap first");
                yield break;
            }
            if (RefreshAtField == null || VisibleIconTypesField == null || SharedFadeField == null ||
                SpriteProperty == null)
            {
                run.Fail("compass_section",
                    "the compass timer, the minimap's icon filter or shared-map fade, or the UI image sprite could not be found by reflection");
                yield break;
            }

            var camera = GameCamera.instance.transform;
            var fixture = new Fixture(run, player, map, camera);
            var rotation = camera.rotation;
            var userHidden = hud.m_userHidden;
            var mode = map.m_mode;
            // Vanilla turns an icon type back on when a pin of that type is added, so the whole
            // filter is put back, not only the type the toggle check turns off.
            var iconTypes = ((bool[])VisibleIconTypesField.GetValue(map)).ToArray();
            var pinsBefore = Map.GetAllPins().ToList();
            Func<string, bool> handBack = _ =>
            {
                if (camera) camera.rotation = rotation;
                if (hud) hud.m_userHidden = userHidden;
                if (!map || Minimap.instance != map) return true;
                foreach (var pin in fixture.Created) map.RemovePin(pin);
                var types = (bool[])VisibleIconTypesField.GetValue(map);
                for (var i = 0; i < types.Length && i < iconTypes.Length; i++) types[i] = iconTypes[i];
                if (map.m_mode != mode) map.SetMapMode(mode);
                Map.RefreshPins();
                return true;
            };
            SmokeSession.TrackHandBack(handBack);
            try
            {
                foreach (var entry in new ConfigEntryBase[]
                         {
                             MappingConfig.EnableCompassEntry, MappingConfig.CompassPinRangeEntry,
                             MappingConfig.EnableMapEnhancementsEntry, MappingConfig.MapFilterHiddenCategoriesEntry,
                             MappingConfig.HideUnexploredAutomaticMappingPinsEntry
                         })
                {
                    if (!SmokeSession.BeginSetting(run.RunId, entry, out var setting, out var refusal))
                    {
                        run.Fail("compass_settings", "setting ownership precondition failed: " + refusal);
                        yield break;
                    }
                    fixture.Settings.Add(entry, setting);
                }

                fixture.Write(MappingConfig.EnableCompassEntry, true);
                fixture.Write(MappingConfig.CompassPinRangeEntry, TestRange);
                fixture.Write(MappingConfig.EnableMapEnhancementsEntry, true);
                fixture.Write(MappingConfig.MapFilterHiddenCategoriesEntry, new StringList());
                fixture.Write(MappingConfig.HideUnexploredAutomaticMappingPinsEntry, false);

                if (!CheckShown(fixture, hud)) yield break;
                CheckLetters(fixture);
                CheckMarkers(fixture);
                CheckIconToggle(fixture);
                CheckCategory(fixture);
                CheckUnexplored(fixture);
                CheckCap(fixture);
                CheckHidden(fixture, hud, userHidden);
                CheckRect(fixture, hud);
                fixture.Report();
            }
            finally
            {
                handBack("compass smoke");
                SmokeSession.ReleaseHandBack(handBack);
                if (map && Minimap.instance == map)
                {
                    var now = new HashSet<Minimap.PinData>(Map.GetAllPins());
                    var left = fixture.Created.Count(now.Contains);
                    var lost = pinsBefore.Count(pin => !now.Contains(pin));
                    Check(run, "compass_pins_removed", left == 0 && lost == 0,
                        $"{left} of the section's {fixture.Created.Count} test pin(s) are on the map, and {lost} of the {pinsBefore.Count} pin(s) there before the section are gone");
                }
                var restored = SmokeSession.RestoreSettings(run.RunId);
                Check(run, "compass_settings_restored", restored.All(x =>
                        x.Key == SmokeSession.ToggleOutcome.Restored ||
                        x.Key == SmokeSession.ToggleOutcome.AlreadyOriginal ||
                        x.Key == SmokeSession.ToggleOutcome.NothingWritten),
                    string.Join("; ", restored.Select(x => x.Value)));
            }
        }

        private static bool CheckShown(Fixture fixture, Hud hud)
        {
            fixture.Face(fixture.Heading);
            fixture.Refresh();
            var root = Compass.Root;
            var shown = root && root.activeInHierarchy && root.name == Compass.ObjectName &&
                        root.transform.IsChildOf(hud.m_rootObject.transform);
            Check(fixture.Run, "compass_strip_shown", shown,
                $"with the option on, the HUD shown and the small map open the strip {(root ? root.activeInHierarchy ? "is shown" : "exists but is hidden" : "does not exist")}, {(root && root.transform.IsChildOf(hud.m_rootObject.transform) ? "under" : "not under")} the HUD's root");
            return shown;
        }

        // Facing north, N is at the centre and E and W at the strip's ends; turning moves them.
        private static void CheckLetters(Fixture fixture)
        {
            // A heading, then the angle from it to N, E, S and W, or NaN where the letter is
            // outside the half view.
            var cases = new[]
            {
                new[] { 0f, 0f, 90f, float.NaN, -90f },
                new[] { 30f, -30f, 60f, float.NaN, float.NaN },
                new[] { 225f, float.NaN, float.NaN, -45f, 45f }
            };
            var turning = new List<string>();
            for (var c = 0; c < cases.Length; c++)
            {
                fixture.Face(cases[c][0]);
                Compass.Update(fixture.Player);
                var failures = new List<string>();
                for (var i = 0; i < 4; i++)
                {
                    var letter = Compass.Letters[i];
                    var expected = cases[c][i + 1];
                    var shown = letter && letter.gameObject.activeSelf;
                    if (float.IsNaN(expected))
                    {
                        if (shown) failures.Add($"letter {i} is shown at x={letter.anchoredPosition.x:0.0}");
                    }
                    else if (!shown || Mathf.Abs(letter.anchoredPosition.x - X(expected)) > Tolerance)
                        failures.Add($"letter {i} is {(shown ? $"at x={letter.anchoredPosition.x:0.0}" : "hidden")}, expected x={X(expected):0.0}");
                }
                var detail = $"heading {cases[c][0]}: " +
                             (failures.Count == 0 ? "every letter in place" : string.Join("; ", failures));
                if (c == 0) Check(fixture.Run, "compass_letters_facing_north", failures.Count == 0, detail);
                else if (failures.Count > 0) turning.Add(detail);
            }
            Check(fixture.Run, "compass_letters_follow_turning", turning.Count == 0,
                turning.Count == 0 ? "at headings 30 and 225 every letter is in place" : string.Join("; ", turning));

            var texts = Compass.Letters.Select(letter =>
                letter ? (string)TextProperty.GetValue(letter.GetComponent(TextType)) : null).ToArray();
            Check(fixture.Run, "compass_letters_localized",
                texts.Select((text, i) => LetterTexts[i].Contains(text)).All(x => x),
                "the letters read " + string.Join(", ", texts.Select(x => x ?? "(missing)")));
            fixture.Face(fixture.Heading);
        }

        // Pins at known bearings show their own icons at the x the bearing gives; pins beyond the
        // range, behind the player or beyond the half view do not, until the range is raised or
        // the camera turns to them.
        private static void CheckMarkers(Fixture fixture)
        {
            var relative = new[] { 30f, -60f, 0f, 75f };
            var distance = new[] { 20f, 35f, 50f, 70f };
            var types = new[]
            {
                Minimap.PinType.Icon0, Minimap.PinType.Icon1, Minimap.PinType.Icon2, Minimap.PinType.Icon4
            };
            var placed = relative.Select((angle, i) => fixture.Manual(fixture.At(fixture.Heading + angle, distance[i]), types[i]))
                .ToArray();
            var far = fixture.Manual(fixture.At(fixture.Heading + 10f, TestRange + 10f), Minimap.PinType.Icon3);
            var behind = fixture.Manual(fixture.At(fixture.Heading + 180f, 10f), Minimap.PinType.Icon3);
            var side = fixture.Manual(fixture.At(fixture.Heading - 100f, 15f), Minimap.PinType.Icon3);
            try
            {
                fixture.Face(fixture.Heading);
                fixture.Refresh();
                var crowded = fixture.Crowded(placed);
                if (crowded != null)
                {
                    fixture.Run.Skip("compass_marker_positions", crowded);
                    fixture.Run.Skip("compass_marker_icons", crowded);
                }
                else
                {
                    var positions = new List<string>();
                    var icons = new List<string>();
                    for (var i = 0; i < placed.Length; i++)
                    {
                        var marker = MarkerOf(placed[i]);
                        if (!marker || !marker.gameObject.activeSelf)
                        {
                            positions.Add($"the pin at {relative[i]} degrees is not shown");
                            icons.Add($"the pin of type {placed[i].m_type} has no marker to show its icon");
                            continue;
                        }
                        if (Mathf.Abs(marker.anchoredPosition.x - X(relative[i])) > Tolerance)
                            positions.Add($"the pin at {relative[i]} degrees is at x={marker.anchoredPosition.x:0.0}, expected {X(relative[i]):0.0}");
                        var sprite = SpriteProperty.GetValue(marker.GetComponent(ImageType)) as Sprite;
                        if (sprite != placed[i].m_icon)
                            icons.Add($"the pin of type {placed[i].m_type} shows {(sprite ? sprite.name : "no sprite")} instead of {placed[i].m_icon.name}");
                    }
                    Check(fixture.Run, "compass_marker_positions", positions.Count == 0,
                        positions.Count == 0 ? "four pins at -60, 0, 30 and 75 degrees sit within 1 unit of the expected x" : string.Join("; ", positions));
                    if (placed.Select(pin => pin.m_icon).Distinct().Count() != placed.Length)
                        fixture.Run.Skip("compass_marker_icons", "inconclusive: the four test icon types share a sprite");
                    else
                        Check(fixture.Run, "compass_marker_icons", icons.Count == 0,
                            icons.Count == 0 ? "each marker shows its own pin's icon" : string.Join("; ", icons));
                }

                var cutAtRange = !fixture.Shows(far);
                fixture.Write(MappingConfig.CompassPinRangeEntry, 100f);
                fixture.Refresh();
                var crowdedFar = fixture.Crowded(far);
                var shownInRange = fixture.Shows(far);
                fixture.Write(MappingConfig.CompassPinRangeEntry, TestRange);
                fixture.Refresh();
                if (crowdedFar != null) fixture.Run.Skip("compass_range_cut", crowdedFar);
                else
                    Check(fixture.Run, "compass_range_cut", cutAtRange && shownInRange,
                        $"a pin {TestRange + 10f} m away is {(cutAtRange ? "not shown" : "shown")} with the range at {TestRange} m and {(shownInRange ? "shown" : "not shown")} with it at 100 m");

                var cutBehind = !fixture.Shows(behind) && !fixture.Shows(side);
                fixture.Face(fixture.Heading + 180f);
                Compass.Update(fixture.Player);
                var crowdedBehind = fixture.Crowded(behind);
                var behindX = Offset(behind);
                fixture.Face(fixture.Heading - 100f);
                Compass.Update(fixture.Player);
                var crowdedSide = fixture.Crowded(side);
                var sideX = Offset(side);
                fixture.Face(fixture.Heading);
                Compass.Update(fixture.Player);
                if (crowdedBehind != null || crowdedSide != null)
                    fixture.Run.Skip("compass_view_cut", crowdedBehind ?? crowdedSide);
                else
                    Check(fixture.Run, "compass_view_cut",
                        cutBehind && Mathf.Abs(behindX) <= Tolerance && Mathf.Abs(sideX) <= Tolerance,
                        $"pins at 180 and -100 degrees are {(cutBehind ? "not shown" : "shown")}; turned to face each without a new collection, they sit at x={behindX:0.0} and x={sideX:0.0}");
            }
            finally
            {
                fixture.Remove(placed.Concat(new[] { far, behind, side }));
            }
        }

        // Turning an icon type off on the map hides its pins on the compass too.
        private static void CheckIconToggle(Fixture fixture)
        {
            var toggled = fixture.Manual(fixture.At(fixture.Heading + 20f, 25f), Minimap.PinType.Icon0);
            var control = fixture.Manual(fixture.At(fixture.Heading - 20f, 25f), Minimap.PinType.Icon1);
            var types = (bool[])VisibleIconTypesField.GetValue(fixture.MapInstance);
            var index = (int)toggled.m_type;
            var original = types[index];
            try
            {
                fixture.Refresh();
                var crowded = fixture.Crowded(toggled, control);
                var before = fixture.Shows(toggled) && fixture.Shows(control);
                types[index] = false;
                fixture.Refresh();
                var off = !fixture.Shows(toggled) && fixture.Shows(control);
                types[index] = true;
                fixture.Refresh();
                var on = fixture.Shows(toggled);
                if (crowded != null) fixture.Run.Skip("compass_icon_type_toggle", crowded);
                else
                    Check(fixture.Run, "compass_icon_type_toggle", before && off && on,
                        $"shown before={before}; with {toggled.m_type} off, hidden while another type stays={off}; shown again={on}");
            }
            finally
            {
                types[index] = original;
                fixture.Remove(new[] { toggled, control });
            }
        }

        // Hiding the Flora category hides an automatic Flora pin and keeps a manual pin of the same
        // type, both on explored ground.
        private static void CheckCategory(Fixture fixture)
        {
            var flora = fixture.Automatic(fixture.At(fixture.Heading + 20f, 25f), "Automatics smoke compass flora");
            Associate(flora, PinKind.Flora, 301);
            var manual = fixture.Manual(fixture.At(fixture.Heading - 20f, 25f), flora.m_type, "Automatics smoke compass manual");
            try
            {
                if (!Map.IsAutomaticPin(flora) || !PinCategories.TryClassify(flora, out var category) ||
                    category != PinCategory.Flora || Map.IsAutomaticPin(manual))
                {
                    fixture.Run.Fail("compass_category_filter",
                        "fixture precondition failed: the test pin is not an automatic Flora pin, or the control is automatic");
                    return;
                }
                fixture.Refresh();
                var crowded = fixture.Crowded(flora, manual);
                var before = fixture.Shows(flora) && fixture.Shows(manual);
                fixture.Write(MappingConfig.MapFilterHiddenCategoriesEntry, new StringList(new[] { "Flora" }));
                fixture.Refresh();
                var hidden = !fixture.Shows(flora);
                var kept = fixture.Shows(manual);
                fixture.Write(MappingConfig.MapFilterHiddenCategoriesEntry, new StringList());
                fixture.Refresh();
                var back = fixture.Shows(flora);
                if (crowded != null)
                {
                    fixture.Run.Skip("compass_category_filter", crowded);
                    return;
                }
                Check(fixture.Run, "compass_category_filter", before && hidden && back,
                    $"shown before={before}; hidden with Flora hidden={hidden}; shown again={back}");
                fixture.Note("compass_manual_pin_kept", kept, $"a manual pin of the same type is {(kept ? "shown" : "hidden")} with Flora hidden");
            }
            finally
            {
                fixture.Remove(new[] { flora, manual });
            }
        }

        // An automatic pin on unexplored ground in range is shown until Hide Pins Outside Explored
        // Area is turned on; an automatic pin on explored ground and a manual pin on the same
        // unexplored spot stay.
        private static void CheckUnexplored(Fixture fixture)
        {
            if (!MappingConfig.EnableAutomaticMapping)
            {
                fixture.Run.Skip("compass_unexplored_filter",
                    "enable_automatic_mapping is off, so Hide Pins Outside Explored Area does not apply");
                return;
            }

            // The game explores 100 m around you, so the search starts beyond that.
            float? bearing = null;
            var distance = 0f;
            for (var d = 110f; d <= MaxRange - 10f && bearing == null; d += 20f)
            for (var b = 0f; b < 360f && bearing == null; b += 15f)
                if (!Map.IsExplored(fixture.At(b, d)))
                {
                    bearing = b;
                    distance = d;
                }
            if (bearing == null)
            {
                fixture.Run.Skip("compass_unexplored_filter",
                    $"every point 110 to {MaxRange - 10f} m around you is explored; run the section nearer the edge of the explored map");
                return;
            }

            fixture.Write(MappingConfig.CompassPinRangeEntry, MaxRange);
            var position = fixture.At(bearing.Value, distance);
            var unexplored = fixture.Automatic(position, "Automatics smoke compass unexplored");
            var explored = fixture.Automatic(fixture.At(bearing.Value + 10f, 30f), "Automatics smoke compass explored");
            var manual = fixture.Manual(position, unexplored.m_type, "Automatics smoke compass unexplored manual");
            try
            {
                if (!Map.IsAutomaticPin(unexplored) || !Map.IsAutomaticPin(explored) || Map.IsAutomaticPin(manual) ||
                    Map.IsExplored(unexplored.m_pos) || !Map.IsExplored(explored.m_pos))
                {
                    fixture.Run.Fail("compass_unexplored_filter",
                        "fixture precondition failed: the test pins are not automatic, the control is automatic, or the explored and unexplored spots changed");
                    return;
                }
                fixture.Face(bearing.Value);
                fixture.Refresh();
                var crowded = fixture.Crowded(unexplored, explored, manual);
                var before = fixture.Shows(unexplored) && fixture.Shows(explored) && fixture.Shows(manual);
                fixture.Write(MappingConfig.HideUnexploredAutomaticMappingPinsEntry, true);
                fixture.Refresh();
                var hidden = !fixture.Shows(unexplored);
                var exploredKept = fixture.Shows(explored);
                var manualKept = fixture.Shows(manual);
                fixture.Write(MappingConfig.HideUnexploredAutomaticMappingPinsEntry, false);
                if (crowded != null)
                {
                    fixture.Run.Skip("compass_unexplored_filter", crowded);
                    return;
                }
                Check(fixture.Run, "compass_unexplored_filter", before && hidden && exploredKept,
                    $"pins {distance} m away on unexplored ground and 30 m away on explored ground: shown before={before}; with the filter on, the unexplored one hidden={hidden} and the explored one shown={exploredKept}");
                fixture.Note("compass_manual_pin_kept", manualKept,
                    $"a manual pin on unexplored ground is {(manualKept ? "shown" : "hidden")} with the filter on");
            }
            finally
            {
                fixture.Remove(new[] { unexplored, explored, manual });
                fixture.Write(MappingConfig.CompassPinRangeEntry, TestRange);
                fixture.Face(fixture.Heading);
                fixture.Refresh();
            }
        }

        // 24 pins behind the player are nearer than 30 pins ahead. Facing ahead, the nearest pins
        // in view show; turned around before the next collection, the pins behind do.
        private static void CheckCap(Fixture fixture)
        {
            var behind = Enumerable.Range(0, Compass.MarkerCount)
                .Select(i => fixture.Manual(fixture.At(fixture.Heading + 180f + (i - 11.5f) * 4f, 5f + i), Minimap.PinType.Icon3))
                .ToList();
            var ahead = Enumerable.Range(0, 30)
                .Select(i => fixture.Manual(fixture.At(fixture.Heading + (i - 14.5f) * 3f, 32f + i), Minimap.PinType.Icon3))
                .ToList();
            try
            {
                fixture.Face(fixture.Heading);
                fixture.Refresh();
                var shown = Compass.ShownPins.ToList();
                var expected = fixture.Expected();
                Check(fixture.Run, "compass_cap_nearest_in_view",
                    shown.Count == Compass.MarkerCount && new HashSet<Minimap.PinData>(shown).SetEquals(expected) &&
                    !shown.Any(behind.Contains),
                    $"facing away from the 24 nearer pins, {shown.Count} pins are shown ({shown.Count(ahead.Contains)} of the 30 ahead, {shown.Count(behind.Contains)} of the 24 behind); the map's rule picks {expected.Count}, {expected.Count(shown.Contains)} of them shown");

                var refreshAt = RefreshAtField.GetValue(null);
                fixture.Face(fixture.Heading + 180f);
                Compass.Update(fixture.Player);
                var collected = !Equals(RefreshAtField.GetValue(null), refreshAt);
                shown = Compass.ShownPins.ToList();
                expected = fixture.Expected();
                Check(fixture.Run, "compass_cap_after_turn",
                    !collected && shown.Count == Compass.MarkerCount &&
                    new HashSet<Minimap.PinData>(shown).SetEquals(expected) && !shown.Any(ahead.Contains) &&
                    (!behind.All(expected.Contains) || behind.All(shown.Contains)),
                    $"turned around {(collected ? "after a new collection" : "before the next collection")}, {shown.Count} pins are shown ({shown.Count(behind.Contains)} of the 24 behind, {shown.Count(ahead.Contains)} of the 30 ahead); the map's rule picks {expected.Count}, {expected.Count(shown.Contains)} of them shown");

                var order = new List<string>();
                for (var i = 0; i + 1 < shown.Count; i++)
                {
                    var near = Compass.GetMarker(i);
                    var next = Compass.GetMarker(i + 1);
                    if (fixture.Distance(shown[i]) > fixture.Distance(shown[i + 1]) + 0.01f)
                        order.Add($"pin {i} at {fixture.Distance(shown[i]):0.0} m comes before one at {fixture.Distance(shown[i + 1]):0.0} m");
                    if (!near || !next || near.GetSiblingIndex() <= next.GetSiblingIndex())
                        order.Add($"marker {i} is not drawn over marker {i + 1}");
                }
                Check(fixture.Run, "compass_nearer_on_top", shown.Count > 1 && order.Count == 0,
                    order.Count == 0 ? $"the {shown.Count} markers are nearest first and each is drawn over the next" : string.Join("; ", order.Take(5)));
            }
            finally
            {
                fixture.Remove(behind.Concat(ahead));
                fixture.Face(fixture.Heading);
                fixture.Refresh();
            }
        }

        // Hidden on the large map, without the small map (as while dead or in a world without a
        // map) and with the HUD hidden; shown again once each ends.
        private static void CheckHidden(Fixture fixture, Hud hud, bool userHidden)
        {
            var map = fixture.MapInstance;
            foreach (var mode in new[] { Minimap.MapMode.Large, Minimap.MapMode.None })
            {
                map.SetMapMode(mode);
                Compass.Update(fixture.Player);
                var hidden = Compass.Root && !Compass.Root.activeSelf;
                map.SetMapMode(Minimap.MapMode.Small);
                Compass.Update(fixture.Player);
                var back = Compass.Root && Compass.Root.activeSelf;
                Check(fixture.Run, mode == Minimap.MapMode.Large ? "compass_hidden_on_large_map" : "compass_hidden_without_minimap",
                    hidden && back, $"hidden in map mode {mode}={hidden}; shown again with the small map={back}");
            }

            try
            {
                hud.m_userHidden = true;
                Compass.Update(fixture.Player);
                var hidden = Compass.Root && !Compass.Root.activeSelf;
                hud.m_userHidden = userHidden;
                Compass.Update(fixture.Player);
                var back = Compass.Root && Compass.Root.activeSelf;
                Check(fixture.Run, "compass_hidden_with_hud_hidden", hidden && back,
                    $"hidden with the HUD hidden={hidden}; shown again with it shown={back}");
            }
            finally
            {
                hud.m_userHidden = userHidden;
            }
        }

        // Under the HUD's root, horizontally centred and within the top fifth of the screen.
        private static void CheckRect(Fixture fixture, Hud hud)
        {
            AccessTools.Method(AccessTools.TypeByName("UnityEngine.Canvas"), "ForceUpdateCanvases")
                .Invoke(null, null);
            var root = Compass.Root;
            if (!root)
            {
                fixture.Run.Fail("compass_strip_in_top_band", "the strip is gone");
                return;
            }
            var rect = MapFilter.ScreenRect((RectTransform)root.transform);
            var inBand = rect.width > 0f && rect.height > 0f && rect.xMin >= -1f && rect.xMax <= Screen.width + 1f &&
                         rect.yMax <= Screen.height + 1f && rect.yMin >= Screen.height * 0.8f &&
                         Mathf.Abs(rect.center.x - Screen.width / 2f) <= 2f;
            Check(fixture.Run, "compass_strip_in_top_band",
                inBand && root.transform.IsChildOf(hud.m_rootObject.transform),
                $"screen rect x {rect.xMin:0}-{rect.xMax:0}, y {rect.yMin:0}-{rect.yMax:0} on a {Screen.width}x{Screen.height} screen");
        }

        private static float X(float relative)
        {
            return relative / HalfView * (Compass.Width / 2f);
        }

        private static RectTransform MarkerOf(Minimap.PinData pin)
        {
            var shown = Compass.ShownPins;
            for (var i = 0; i < shown.Count; i++)
                if (ReferenceEquals(shown[i], pin)) return Compass.GetMarker(i);
            return null;
        }

        // The marker's x, or NaN when the pin is not shown.
        private static float Offset(Minimap.PinData pin)
        {
            var marker = MarkerOf(pin);
            return marker && marker.gameObject.activeSelf ? marker.anchoredPosition.x : float.NaN;
        }

        // Registers the pin with the static scan's cache the way the scan does, so it has a live
        // category.
        private static void Associate(Minimap.PinData pin, PinKind kind, uint id)
        {
            AccessTools.Method(typeof(StaticObjectMapping), "CachePin", new[]
            {
                typeof(StaticObjectMapping.MapPinIdentify), typeof(Minimap.PinData), typeof(PinKind),
                typeof(string), typeof(string), typeof(PinSourceDomain)
            }).Invoke(null, new object[]
            {
                new StaticObjectMapping.MapPinIdentify(new ZDOID(long.MinValue, id)), pin, kind,
                "smoke", "smoke", PinSourceDomain.Component
            });
        }

        private static void Check(FeatureRun run, string name, bool passed, string detail)
        {
            if (passed) run.Pass(name, detail);
            else run.Fail(name, detail);
        }

        private sealed class Fixture
        {
            private readonly Dictionary<string, List<KeyValuePair<bool, string>>> _notes =
                new Dictionary<string, List<KeyValuePair<bool, string>>>();

            internal Fixture(FeatureRun run, Player player, Minimap map, Transform camera)
            {
                Run = run;
                Player = player;
                MapInstance = map;
                Camera = camera;
                Heading = Mathf.Round(camera.eulerAngles.y);
            }

            internal FeatureRun Run { get; }
            internal Player Player { get; }
            internal Minimap MapInstance { get; }
            internal Transform Camera { get; }
            internal float Heading { get; }
            internal List<Minimap.PinData> Created { get; } = new List<Minimap.PinData>();
            internal Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> Settings { get; } =
                new Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration>();

            internal Vector3 At(float bearing, float distance)
            {
                var radians = bearing * Mathf.Deg2Rad;
                return Player.transform.position + new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians)) * distance;
            }

            internal float Distance(Minimap.PinData pin)
            {
                return global::Utils.DistanceXZ(Player.transform.position, pin.m_pos);
            }

            internal void Face(float yaw)
            {
                Camera.rotation = Quaternion.Euler(0f, yaw, 0f);
            }

            // The update the player's frame makes, with the pins collected again first.
            internal void Refresh()
            {
                RefreshAtField.SetValue(null, 0f);
                Compass.Update(Player);
            }

            internal bool Shows(Minimap.PinData pin)
            {
                return Compass.ShownPins.Contains(pin);
            }

            internal Minimap.PinData Manual(Vector3 position, Minimap.PinType type, string name = "")
            {
                var pin = MapInstance.AddPin(position, type, name, false, false);
                Created.Add(pin);
                return pin;
            }

            internal Minimap.PinData Automatic(Vector3 position, string name)
            {
                var pin = Map.AddPin(position, name, false, new Target { name = name });
                Created.Add(pin);
                return pin;
            }

            internal void Remove(IEnumerable<Minimap.PinData> pins)
            {
                foreach (var pin in pins) MapInstance.RemovePin(pin);
            }

            internal void Write(ConfigEntryBase entry, object value)
            {
                if (!SmokeSession.WriteSetting(Run.RunId, Settings[entry], value, out var refusal))
                    throw new InvalidOperationException(entry.Definition.Key + " setting write precondition failed: " + refusal);
            }

            // The pins the map's own rule shows at the current heading: those the minimap would
            // draw, within range and the half view, nearest first, at most the marker count.
            internal List<Minimap.PinData> Expected()
            {
                var origin = Player.transform.position;
                var heading = Camera.eulerAngles.y;
                var types = (bool[])VisibleIconTypesField.GetValue(MapInstance);
                var shared = (float)SharedFadeField.GetValue(MapInstance) > 0f;
                return Map.GetAllPins()
                    .Where(pin => pin != null && pin.m_icon && pin.m_worldSize <= 0f &&
                                  pin.m_type != Minimap.PinType.EventArea &&
                                  (int)pin.m_type < types.Length && types[(int)pin.m_type] &&
                                  (pin.m_ownerID == 0L || shared) &&
                                  global::Utils.DistanceXZ(origin, pin.m_pos) <= MappingConfig.CompassPinRange &&
                                  !Map.ShouldHideAutomaticPin(pin) &&
                                  Mathf.Abs(Mathf.DeltaAngle(heading, Mathf.Atan2(pin.m_pos.x - origin.x,
                                      pin.m_pos.z - origin.z) * Mathf.Rad2Deg)) <= HalfView)
                    .OrderBy(pin => global::Utils.DistanceXZ(origin, pin.m_pos))
                    .Take(Compass.MarkerCount)
                    .ToList();
            }

            // Why a test pin that should show cannot be judged at the current heading: more of
            // your own pins than there are markers lie nearer in view. Null when it can.
            internal string Crowded(params Minimap.PinData[] pins)
            {
                var expected = Expected();
                var missing = pins.Count(pin => !expected.Contains(pin));
                return missing == 0
                    ? null
                    : $"inconclusive: {missing} test pin(s) are pushed out of the {Compass.MarkerCount} markers by nearer pins of yours in view; run the section away from them";
            }

            // A check made in several places, reported once: a PASS only when every part passed.
            internal void Note(string name, bool passed, string detail)
            {
                if (!_notes.TryGetValue(name, out var parts)) _notes[name] = parts = new List<KeyValuePair<bool, string>>();
                parts.Add(new KeyValuePair<bool, string>(passed, detail));
            }

            internal void Report()
            {
                foreach (var note in _notes)
                    Check(Run, note.Key, note.Value.All(x => x.Key), string.Join("; ", note.Value.Select(x => x.Value)));
            }
        }
    }
}
