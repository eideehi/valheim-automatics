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
    // the strip's place on screen at the top and the bottom, the size and opacity by distance,
    // the navigation target's own marker, the name labels, and moving out of the way of the
    // event bar and a boss's bar. Everything runs in one frame, so no scan can move or remove a
    // pin between a change and its check.
    //
    // The test pins are unsaved, so no map save keeps them, and they are the only pins the
    // section removes. Your navigation target is cleared for the section and set again at its
    // end. The camera, the map mode, the HUD, the minimap's icon filter, the event bar, the test
    // creature with its boss flag and bar, and the settings it changes are put back when the
    // section ends, and by the session hand-back when the run is cut short.
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
        private static readonly PropertyInfo ColorProperty =
            ImageType == null ? null : AccessTools.Property(ImageType, "color");
        private static readonly MethodInfo ShowHudMethod = AccessTools.Method(typeof(EnemyHud), "ShowHud");
        private static readonly FieldInfo HudsField = AccessTools.Field(typeof(EnemyHud), "m_huds");
        private static readonly MethodInfo ForceUpdateCanvasesMethod =
            AccessTools.Method(AccessTools.TypeByName("UnityEngine.Canvas"), "ForceUpdateCanvases");
        private static readonly Type TextType =
            AccessTools.Property(typeof(Minimap.PinNameData), "PinNameText").PropertyType;
        private static readonly PropertyInfo TextProperty = AccessTools.Property(TextType, "text");

        // N, E, S and W, as the English and the Japanese translations write them.
        private static readonly string[][] LetterTexts =
        {
            new[] { "N" }, new[] { "E" }, new[] { "S" }, new[] { "W" }
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
                SpriteProperty == null || ColorProperty == null || ShowHudMethod == null || HudsField == null ||
                ForceUpdateCanvasesMethod == null)
            {
                run.Fail("compass_section",
                    "the compass timer, the minimap's icon filter or shared-map fade, the UI image sprite or colour, the enemy HUD's table or ShowHud, or Canvas.ForceUpdateCanvases could not be found by reflection");
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
            // Set again by its state: a temporary marker comes back as a new pin.
            var originalTarget = Navigation.CaptureTarget();
            var navigationRestored = false;
            List<Minimap.PinData> pinsBefore = null;
            Func<string, bool> handBack = _ =>
            {
                if (camera) camera.rotation = rotation;
                if (hud) hud.m_userHidden = userHidden;
                fixture.RestoreBars();
                if (!map || Minimap.instance != map) return true;
                foreach (var pin in fixture.Created) map.RemovePin(pin);
                if (map.m_mode != mode) map.SetMapMode(mode);
                if (!navigationRestored)
                {
                    Navigation.RestoreTarget(originalTarget);
                    navigationRestored = true;
                }
                // Last, as a temporary marker added back with your navigation turns its icon type
                // on again.
                var types = (bool[])VisibleIconTypesField.GetValue(map);
                for (var i = 0; i < types.Length && i < iconTypes.Length; i++) types[i] = iconTypes[i];
                Map.RefreshPins();
                return true;
            };
            SmokeSession.TrackHandBack(handBack);
            try
            {
                // Cleared before the pins are counted, so that a temporary marker of yours, which
                // comes back as a new pin, is not counted as one the section lost.
                Navigation.ClearTarget();
                pinsBefore = Map.GetAllPins().ToList();
                foreach (var entry in new ConfigEntryBase[]
                         {
                             MappingConfig.EnableCompassEntry, MappingConfig.CompassPinRangeEntry,
                             MappingConfig.CompassPositionEntry, MappingConfig.EnableCompassLabelsEntry,
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
                fixture.Write(MappingConfig.CompassPositionEntry, CompassPosition.Top);
                fixture.Write(MappingConfig.EnableCompassLabelsEntry, true);
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
                CheckPosition(fixture, hud);
                CheckDistanceStyle(fixture);
                CheckTarget(fixture);
                CheckTargetLivePosition(fixture);
                CheckTargetCleared(fixture);
                CheckLabels(fixture);
                CheckLabelTargetInWindow(fixture);
                CheckAvoidance(fixture, hud);
                fixture.Report();
            }
            finally
            {
                handBack("compass smoke");
                SmokeSession.ReleaseHandBack(handBack);
                if (map && Minimap.instance == map && pinsBefore != null)
                {
                    var now = new HashSet<Minimap.PinData>(Map.GetAllPins());
                    var left = fixture.Created.Count(now.Contains);
                    var lost = pinsBefore.Count(pin => !now.Contains(pin));
                    Check(run, "compass_pins_removed", left == 0 && lost == 0,
                        $"{left} of the section's {fixture.Created.Count} test pin(s) are on the map, and {lost} of the {pinsBefore.Count} pin(s) there before the section are gone");
                    CheckNavigationRestored(run, originalTarget);
                    var types = (bool[])VisibleIconTypesField.GetValue(map);
                    var changed = Enumerable.Range(0, Math.Max(types.Length, iconTypes.Length))
                        .Where(i => i >= types.Length || i >= iconTypes.Length || types[i] != iconTypes[i])
                        .Select(i => i.ToString()).ToList();
                    Check(run, "compass_icon_filter_restored", changed.Count == 0,
                        changed.Count == 0
                            ? $"all {iconTypes.Length} icon types are shown or hidden as before the section, after your navigation was set again"
                            : $"icon type(s) {string.Join(", ", changed)} differ from before the section");
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
            Check(fixture.Run, "compass_strip_in_top_band", InBand(hud, false, out var detail), detail);
        }

        // At the bottom the strip sits in the bottom fifth instead; the setting moves it there
        // and back while the game runs, recreating the strip each time.
        private static void CheckPosition(Fixture fixture, Hud hud)
        {
            var topRoot = Compass.Root;
            var top = InBand(hud, false, out var topDetail);
            fixture.Write(MappingConfig.CompassPositionEntry, CompassPosition.Bottom);
            Compass.Update(fixture.Player);
            var bottomRoot = Compass.Root;
            var bottom = InBand(hud, true, out var bottomDetail);
            fixture.Write(MappingConfig.CompassPositionEntry, CompassPosition.Top);
            Compass.Update(fixture.Player);
            var back = InBand(hud, false, out var backDetail);
            var recreated = bottomRoot && bottomRoot != topRoot && Compass.Root && Compass.Root != bottomRoot;
            Check(fixture.Run, "compass_strip_in_bottom_band", bottom, bottomDetail);
            Check(fixture.Run, "compass_position_switch", top && bottom && back && recreated,
                $"top={top} ({topDetail}); bottom={bottom} ({bottomDetail}); top again={back} ({backDetail}); a new strip for each change={recreated}");
        }

        // A pin 10 m, one half the range and one at the range (just inside it, since a pin
        // beyond the range is not collected) away take the size and opacity the eased curve
        // gives at their share of the range: with t the share and f = 1 - (1 - t)^2, the size
        // is NearIconSize x lerp(1, FarIconSize / NearIconSize, f) and the opacity
        // lerp(1, FarIconAlpha, f). At half the range that is well away from a linear fall.
        private static void CheckDistanceStyle(Fixture fixture)
        {
            var near = fixture.Manual(fixture.At(fixture.Heading + 15f, 10f), Minimap.PinType.Icon0);
            var mid = fixture.Manual(fixture.At(fixture.Heading + 45f, TestRange / 2f), Minimap.PinType.Icon2);
            var far = fixture.Manual(fixture.At(fixture.Heading - 15f, TestRange - 0.5f), Minimap.PinType.Icon1);
            try
            {
                fixture.Face(fixture.Heading);
                fixture.Refresh();
                var crowded = fixture.Crowded(near, mid, far);
                if (crowded != null)
                {
                    fixture.Run.Skip("compass_distance_style", crowded);
                    return;
                }
                var parts = new List<string>();
                var passed = true;
                foreach (var pin in new[] { near, mid, far })
                {
                    var marker = MarkerOf(pin);
                    var t = Mathf.Clamp01(fixture.Distance(pin) / TestRange);
                    var f = 1f - (1f - t) * (1f - t);
                    var size = Compass.NearIconSize * Mathf.Lerp(1f, Compass.FarIconSize / Compass.NearIconSize, f);
                    var alpha = Mathf.Lerp(1f, Compass.FarIconAlpha, f);
                    var shown = marker && marker.gameObject.activeSelf;
                    var actualSize = shown ? Size(marker) : float.NaN;
                    var actualAlpha = shown ? ColorOf(marker).a : float.NaN;
                    passed &= shown && Mathf.Abs(actualSize - size) <= 0.05f && Mathf.Abs(actualAlpha - alpha) <= 0.01f;
                    parts.Add($"{fixture.Distance(pin):0.0} m away: size {actualSize:0.00} (expected {size:0.00}), alpha {actualAlpha:0.00} (expected {alpha:0.00})");
                }
                Check(fixture.Run, "compass_distance_style", passed, string.Join("; ", parts));
            }
            finally
            {
                fixture.Remove(new[] { near, mid, far });
            }
        }

        // The navigation target has its own marker: beyond the range at its bearing in the
        // guide's colour at full size and opacity, never also an ordinary marker while 24 other
        // pins show, and hidden behind you.
        private static void CheckTarget(Fixture fixture)
        {
            fixture.Face(fixture.Heading);
            var beyond = fixture.Manual(fixture.At(fixture.Heading + 20f, TestRange + 40f), Minimap.PinType.Icon2);
            var inRange = fixture.Manual(fixture.At(fixture.Heading - 25f, 10f), Minimap.PinType.Icon2);
            var behind = fixture.Manual(fixture.At(fixture.Heading + 180f, 30f), Minimap.PinType.Icon2);
            var others = Enumerable.Range(0, Compass.MarkerCount)
                .Select(i => fixture.Manual(fixture.At(fixture.Heading + (i - 11.5f) * 3f, 20f + i), Minimap.PinType.Icon3))
                .ToList();
            try
            {
                fixture.Navigate(beyond);
                Compass.Update(fixture.Player);
                var marker = Compass.TargetMarker;
                var shown = marker && marker.gameObject.activeSelf;
                var x = shown ? marker.anchoredPosition.x : float.NaN;
                var sprite = shown ? SpriteProperty.GetValue(marker.GetComponent(ImageType)) as Sprite : null;
                var color = shown ? ColorOf(marker) : default(Color);
                var guide = MappingConfig.NavigationGuideColor;
                var tinted = shown && Mathf.Abs(color.r - guide.r) <= 0.01f && Mathf.Abs(color.g - guide.g) <= 0.01f &&
                             Mathf.Abs(color.b - guide.b) <= 0.01f && Mathf.Abs(color.a - 1f) <= 0.01f;
                var size = shown ? Size(marker) : float.NaN;
                Check(fixture.Run, "compass_target_beyond_range",
                    shown && Mathf.Abs(x - X(20f)) <= Tolerance && sprite == beyond.m_icon && tinted &&
                    Mathf.Abs(size - Compass.NearIconSize) <= 0.05f && !fixture.Shows(beyond),
                    $"a target {TestRange + 40f} m away at 20 degrees is {(shown ? $"shown at x={x:0.0} (expected {X(20f):0.0})" : "not shown")}, with {(sprite == beyond.m_icon ? "its own icon" : "another icon")}, colour {color} against the guide's {guide}, size {size:0.00} of {Compass.NearIconSize}");

                fixture.Navigate(inRange);
                Compass.Update(fixture.Player);
                marker = Compass.TargetMarker;
                shown = marker && marker.gameObject.activeSelf;
                x = shown ? marker.anchoredPosition.x : float.NaN;
                var shownPins = Compass.ShownPins.ToList();
                var expected = fixture.Expected();
                Check(fixture.Run, "compass_target_not_duplicated",
                    shown && Mathf.Abs(x - X(-25f)) <= Tolerance && !shownPins.Contains(inRange) &&
                    shownPins.Count == Compass.MarkerCount && new HashSet<Minimap.PinData>(shownPins).SetEquals(expected),
                    $"a target 10 m away at -25 degrees is {(shown ? $"shown at x={x:0.0} (expected {X(-25f):0.0})" : "not shown")} on its own marker and {(shownPins.Contains(inRange) ? "also" : "not")} an ordinary marker; {shownPins.Count} ordinary markers show, {shownPins.Count(others.Contains)} of the 24 other test pins, {expected.Count(shownPins.Contains)} of the {expected.Count} the map's rule picks");

                // The same target, with the navigation left as it is: hidden behind, shown when
                // the camera turns to it, hidden again when it turns back.
                fixture.Navigate(behind);
                Compass.Update(fixture.Player);
                marker = Compass.TargetMarker;
                var hidden = marker && !marker.gameObject.activeSelf && !fixture.Shows(behind);
                fixture.Face(fixture.Heading + 180f);
                Compass.Update(fixture.Player);
                marker = Compass.TargetMarker;
                var facing = marker && marker.gameObject.activeSelf && Mathf.Abs(marker.anchoredPosition.x) <= Tolerance;
                var facingX = marker && marker.gameObject.activeSelf ? marker.anchoredPosition.x : float.NaN;
                fixture.Face(fixture.Heading);
                Compass.Update(fixture.Player);
                marker = Compass.TargetMarker;
                var hiddenAgain = marker && !marker.gameObject.activeSelf;
                var same = ReferenceEquals(Navigation.TargetPin, behind);
                Check(fixture.Run, "compass_target_behind_hidden", hidden && facing && hiddenAgain && same,
                    $"a target 30 m behind you is {(hidden ? "not shown" : "shown")}; turned to face it, it is {(facing ? "shown at the centre" : $"at x={facingX:0.0}")}; turned back, it is {(hiddenAgain ? "hidden again" : "still shown")}; the navigation stayed on it={same}");
            }
            finally
            {
                Navigation.ClearTarget();
                fixture.Face(fixture.Heading);
                fixture.Remove(others.Concat(new[] { beyond, inRange, behind }));
            }
        }

        // A creature target is drawn and named where navigation last saw the creature, not at
        // its pin: the marker's bearing and the label's distance follow that position when it
        // moves while the pin stays the same.
        private static void CheckTargetLivePosition(Fixture fixture)
        {
            fixture.Face(fixture.Heading);
            var pin = fixture.Manual(fixture.At(fixture.Heading + 30f, 40f), Minimap.PinType.Icon2,
                "Automatics smoke compass creature");
            var creature = new ZDOID(long.MinValue, 302);
            try
            {
                var parts = new List<string>();
                var passed = true;
                foreach (var live in new[] { new[] { -20f, 25f }, new[] { 10f, 50f } })
                {
                    var position = fixture.At(fixture.Heading + live[0], live[1]);
                    Navigation.RestoreTarget(new Navigation.TargetState
                    {
                        Pin = pin, CreatureId = creature, LastPosition = position
                    });
                    Compass.Update(fixture.Player);
                    var marker = Compass.TargetMarker;
                    var shown = marker && marker.gameObject.activeSelf;
                    var x = shown ? marker.anchoredPosition.x : float.NaN;
                    var label = LabelText(Compass.TargetLabelRect);
                    var expected = fixture.LabelOf(pin, position);
                    var same = ReferenceEquals(Navigation.TargetPin, pin);
                    passed &= same && shown && Mathf.Abs(x - X(live[0])) <= Tolerance && label == expected &&
                              !fixture.Shows(pin);
                    parts.Add($"seen {live[1]} m away at {live[0]} degrees (the pin at 30 degrees, 40 m): the same pin={same}, marker {(shown ? $"at x={x:0.0}" : "not shown")} (expected {X(live[0]):0.0}), label '{label ?? "(hidden)"}' (expected '{expected}'), the pin also an ordinary marker={fixture.Shows(pin)}");
                }
                Check(fixture.Run, "compass_target_follows_live_position", passed, string.Join("; ", parts));
            }
            finally
            {
                Navigation.ClearTarget();
                fixture.Remove(new[] { pin });
            }
        }

        // Clearing navigation hides the target's marker, and its temporary marker, removed with
        // it, is not left as an ordinary marker before the next collection.
        private static void CheckTargetCleared(Fixture fixture)
        {
            fixture.Face(fixture.Heading);
            Navigation.RestoreTarget(new Navigation.TargetState
            {
                Pin = new Minimap.PinData { m_type = Minimap.PinType.Icon3, m_name = "Automatics smoke compass marker" },
                Temporary = true,
                LastPosition = fixture.At(fixture.Heading + 15f, 25f)
            });
            var temporary = Navigation.TargetPin;
            try
            {
                if (temporary == null || !ReferenceEquals(Navigation.TemporaryMarker, temporary))
                {
                    fixture.Run.Fail("compass_target_cleared",
                        "fixture precondition failed: navigation did not take a temporary marker as its target");
                    return;
                }
                fixture.Refresh();
                var marker = Compass.TargetMarker;
                var shownBefore = marker && marker.gameObject.activeSelf && !fixture.Shows(temporary);
                var wouldShow = fixture.Expected(false).Contains(temporary);
                Navigation.ClearTarget();
                var removed = !Map.ContainsPin(temporary);
                Compass.Update(fixture.Player);
                var hidden = marker && !marker.gameObject.activeSelf;
                var notOrdinary = !fixture.Shows(temporary);
                if (!wouldShow)
                {
                    fixture.Run.Skip("compass_target_cleared",
                        $"inconclusive: the temporary marker is pushed out of the {Compass.MarkerCount} markers by nearer pins of yours in view; run the section away from them");
                    return;
                }
                Check(fixture.Run, "compass_target_cleared", shownBefore && removed && hidden && notOrdinary,
                    $"a temporary target 25 m away at 15 degrees: on its own marker before={shownBefore}; removed from the map with the navigation={removed}; its marker hidden={hidden}; not an ordinary marker before the next collection={notOrdinary}");
            }
            finally
            {
                Navigation.ClearTarget();
            }
        }

        // The pin faced within the focus angle and the navigation target are named at their
        // markers, as "name [Nm]"; a pin outside the window is not, a faced target is named once,
        // and the option turns both off. A label is rewritten for a rename but not otherwise, and
        // comes back after the position changes and after the strip is recreated.
        private static void CheckLabels(Fixture fixture)
        {
            var faced = fixture.Manual(fixture.At(fixture.Heading + 4f, 30f), Minimap.PinType.Icon0,
                "Automatics smoke compass faced");
            var target = fixture.Manual(fixture.At(fixture.Heading - 45f, 40f), Minimap.PinType.Icon1,
                "Automatics smoke compass target");
            try
            {
                fixture.Face(fixture.Heading);
                fixture.Refresh();
                var blocked = fixture.FacedBlocked(faced);
                if (blocked != null)
                {
                    foreach (var name in new[]
                             {
                                 "compass_label_faced", "compass_label_not_rewritten", "compass_label_rename",
                                 "compass_label_outside_window", "compass_label_target",
                                 "compass_labels_after_position_change", "compass_labels_after_recreation",
                                 "compass_label_faced_target_once", "compass_labels_option_off"
                             })
                        fixture.Run.Skip(name, blocked);
                    return;
                }

                var text = LabelText(Compass.FacedLabelRect);
                var x = Compass.FacedLabelRect.anchoredPosition.x;
                var noTarget = LabelText(Compass.TargetLabelRect) == null;
                Check(fixture.Run, "compass_label_faced",
                    text == fixture.LabelOf(faced) && Mathf.Abs(x - X(4f)) <= Tolerance && noTarget,
                    $"the pin at 4 degrees is labelled '{text ?? "(hidden)"}' at x={x:0.0}, expected '{fixture.LabelOf(faced)}' at {X(4f):0.0}; the target label is {(noTarget ? "hidden" : "shown")} without navigation");
                if (text != fixture.LabelOf(faced))
                {
                    fixture.Run.Skip("compass_label_not_rewritten", "the faced label must show first");
                    fixture.Run.Skip("compass_label_rename", "the faced label must show first");
                }
                else
                {
                    const string sentinel = "Automatics smoke compass sentinel";
                    TextProperty.SetValue(Compass.FacedLabelRect.GetComponent(TextType), sentinel);
                    Compass.Update(fixture.Player);
                    text = LabelText(Compass.FacedLabelRect);
                    Check(fixture.Run, "compass_label_not_rewritten", text == sentinel,
                        $"with the pin, its name and the metre unchanged, the label reads '{text ?? "(hidden)"}' after the next update instead of being written again");

                    faced.m_name = "Automatics smoke compass renamed";
                    Compass.Update(fixture.Player);
                    text = LabelText(Compass.FacedLabelRect);
                    Check(fixture.Run, "compass_label_rename", text == fixture.LabelOf(faced),
                        $"renamed at the same distance, the label reads '{text ?? "(hidden)"}', expected '{fixture.LabelOf(faced)}'");
                }

                fixture.Face(fixture.Heading - 20f);
                Compass.Update(fixture.Player);
                var inWindow = fixture.InFocus();
                var hidden = LabelText(Compass.FacedLabelRect) == null;
                if (inWindow > 0)
                    fixture.Run.Skip("compass_label_outside_window",
                        $"inconclusive: {inWindow} pin(s) of yours lie within {Compass.FocusAngle} degrees of the centre 20 degrees to the left; run the section away from them");
                else
                    Check(fixture.Run, "compass_label_outside_window", hidden,
                        $"turned 20 degrees to the left, with the test pins 24 and -25 degrees off the centre, the label is {(hidden ? "hidden" : "shown")}");
                fixture.Face(fixture.Heading);

                fixture.Navigate(target);
                Compass.Update(fixture.Player);
                var facedText = LabelText(Compass.FacedLabelRect);
                var targetText = LabelText(Compass.TargetLabelRect);
                var targetX = Compass.TargetLabelRect.anchoredPosition.x;
                Check(fixture.Run, "compass_label_target",
                    targetText == fixture.LabelOf(target) && Mathf.Abs(targetX - X(-45f)) <= Tolerance &&
                    facedText == fixture.LabelOf(faced),
                    $"the target at -45 degrees is labelled '{targetText ?? "(hidden)"}' at x={targetX:0.0}, expected '{fixture.LabelOf(target)}' at {X(-45f):0.0}; the faced pin keeps '{facedText ?? "(hidden)"}'");

                CheckLabelsKept(fixture, facedText, targetText);

                fixture.Navigate(faced);
                Compass.Update(fixture.Player);
                facedText = LabelText(Compass.FacedLabelRect);
                targetText = LabelText(Compass.TargetLabelRect);
                targetX = Compass.TargetLabelRect.anchoredPosition.x;
                Check(fixture.Run, "compass_label_faced_target_once",
                    facedText == null && targetText == fixture.LabelOf(faced) && Mathf.Abs(targetX - X(4f)) <= Tolerance,
                    $"the faced pin made the target: the faced label is {(facedText == null ? "hidden" : $"'{facedText}'")}, the target label '{targetText ?? "(hidden)"}' at x={targetX:0.0}");

                fixture.Navigate(target);
                fixture.Write(MappingConfig.EnableCompassLabelsEntry, false);
                Compass.Update(fixture.Player);
                var off = LabelText(Compass.FacedLabelRect) == null && LabelText(Compass.TargetLabelRect) == null;
                fixture.Write(MappingConfig.EnableCompassLabelsEntry, true);
                Compass.Update(fixture.Player);
                var on = LabelText(Compass.FacedLabelRect) == fixture.LabelOf(faced) &&
                         LabelText(Compass.TargetLabelRect) == fixture.LabelOf(target);
                Check(fixture.Run, "compass_labels_option_off", off && on,
                    $"with enable_compass_labels off both labels are hidden={off}; turned back on, both show again={on}");
            }
            finally
            {
                Navigation.ClearTarget();
                fixture.Remove(new[] { faced, target });
            }
        }

        // A pin at the centre is named while the target lies outside the focus window, and not
        // while the target lies inside it, though the pin is nearer the centre: then only the
        // target is named.
        private static void CheckLabelTargetInWindow(Fixture fixture)
        {
            var centre = fixture.Manual(fixture.At(fixture.Heading, 30f), Minimap.PinType.Icon0,
                "Automatics smoke compass centre");
            var inside = fixture.Manual(fixture.At(fixture.Heading + 4f, 40f), Minimap.PinType.Icon1,
                "Automatics smoke compass inside");
            var outside = fixture.Manual(fixture.At(fixture.Heading + 20f, 40f), Minimap.PinType.Icon1,
                "Automatics smoke compass outside");
            try
            {
                fixture.Face(fixture.Heading);
                fixture.Navigate(outside);
                Compass.Update(fixture.Player);
                var blocked = fixture.FacedBlocked(centre);
                if (blocked != null)
                {
                    fixture.Run.Skip("compass_label_target_in_window", blocked);
                    return;
                }
                var controlFaced = LabelText(Compass.FacedLabelRect);
                var controlTarget = LabelText(Compass.TargetLabelRect);
                var control = controlFaced == fixture.LabelOf(centre) && controlTarget == fixture.LabelOf(outside);

                fixture.Navigate(inside);
                Compass.Update(fixture.Player);
                var facedText = LabelText(Compass.FacedLabelRect);
                var targetText = LabelText(Compass.TargetLabelRect);
                var targetX = Compass.TargetLabelRect.anchoredPosition.x;
                var only = facedText == null && targetText == fixture.LabelOf(inside) &&
                           Mathf.Abs(targetX - X(4f)) <= Tolerance;
                Check(fixture.Run, "compass_label_target_in_window", control && only,
                    $"with the target at 20 degrees the pin at 0 degrees is labelled '{controlFaced ?? "(hidden)"}' and the target '{controlTarget ?? "(hidden)"}'; with the target at 4 degrees the pin's label is {(facedText == null ? "hidden" : $"'{facedText}'")} and the target's '{targetText ?? "(hidden)"}' at x={targetX:0.0}");
            }
            finally
            {
                Navigation.ClearTarget();
                fixture.Remove(new[] { centre, inside, outside });
            }
        }

        // The labels shown come back with the same text after Top, Bottom and Top again, on the
        // side of the strip away from the screen's edge, and after the strip is recreated.
        private static void CheckLabelsKept(Fixture fixture, string facedText, string targetText)
        {
            var sides = new List<string>();
            var kept = facedText != null && targetText != null;
            foreach (var position in new[] { CompassPosition.Bottom, CompassPosition.Top })
            {
                fixture.Write(MappingConfig.CompassPositionEntry, position);
                Compass.Update(fixture.Player);
                ForceUpdateCanvases();
                kept &= LabelText(Compass.FacedLabelRect) == facedText && LabelText(Compass.TargetLabelRect) == targetText;
                var strip = ParentRect((RectTransform)Compass.Root.transform);
                foreach (var label in new[] { Compass.FacedLabelRect, Compass.TargetLabelRect })
                {
                    var rect = ParentRect(label);
                    var side = position == CompassPosition.Top
                        ? rect.yMax <= strip.yMin + Tolerance
                        : rect.yMin >= strip.yMax - Tolerance;
                    if (!side)
                        sides.Add($"at the {position} a label spans y {rect.yMin:0.0}-{rect.yMax:0.0} against the strip's {strip.yMin:0.0}-{strip.yMax:0.0}");
                }
            }
            Check(fixture.Run, "compass_labels_after_position_change", kept && sides.Count == 0,
                $"both labels read the same after Bottom and Top={kept}; " +
                (sides.Count == 0 ? "below the strip at the top and above it at the bottom" : string.Join("; ", sides)));

            fixture.Write(MappingConfig.EnableCompassEntry, false);
            Compass.Update(fixture.Player);
            var gone = !Compass.Root;
            fixture.Write(MappingConfig.EnableCompassEntry, true);
            Compass.Update(fixture.Player);
            var back = LabelText(Compass.FacedLabelRect) == facedText && LabelText(Compass.TargetLabelRect) == targetText;
            Check(fixture.Run, "compass_labels_after_recreation", facedText != null && targetText != null && gone && back,
                $"the strip was removed with enable_compass off={gone}; recreated, both labels read the same={back}");
        }

        // At the top, the event bar and a boss's bar moved over the band the strip and its labels
        // take push the strip down until it clears them, and it goes back once they hide; at the
        // bottom it stays.
        private static void CheckAvoidance(Fixture fixture, Hud hud)
        {
            var enemyHud = EnemyHud.instance;
            if (enemyHud.ShowingBossHud())
            {
                const string reason = "inconclusive: a boss's bar is already shown; run the section away from bosses";
                fixture.Run.Skip("compass_avoids_event_bar", reason);
                fixture.Run.Skip("compass_avoids_boss_bar", reason);
                fixture.Run.Skip("compass_bottom_ignores_bars", reason);
                return;
            }

            var eventBar = fixture.Track((RectTransform)hud.m_eventBar.transform);
            eventBar.gameObject.SetActive(false);
            CheckAvoid(fixture, "compass_avoids_event_bar", eventBar);

            var reasonBoss = BossPrecondition();
            if (reasonBoss != null)
            {
                fixture.Run.Skip("compass_avoids_boss_bar", reasonBoss);
                return;
            }
            var boss = fixture.SpawnBoss(ZNetScene.instance.GetPrefab("Boar"));
            if (!boss)
            {
                fixture.Run.Skip("compass_avoids_boss_bar", "the test creature could not be spawned with a valid ZDO");
                return;
            }
            ShowHudMethod.Invoke(enemyHud, new object[] { boss, false });
            var bar = BossBar(enemyHud, boss);
            if (!bar || enemyHud.GetActiveBoss() != boss)
            {
                fixture.Run.Fail("compass_avoids_boss_bar",
                    "fixture precondition failed: the test creature marked as a boss got no bar, or another boss is the active one");
                return;
            }
            CheckAvoid(fixture, "compass_avoids_boss_bar", fixture.Track(bar));
        }

        // One bar: moved over the default band, shown, then hidden, at the top; then moved over the
        // band at the bottom and shown again. Its place and visibility are put back by the caller.
        private static void CheckAvoid(Fixture fixture, string name, RectTransform bar)
        {
            var player = fixture.Player;
            bar.gameObject.SetActive(false);
            Compass.Update(player);
            ForceUpdateCanvases();
            var strip = (RectTransform)Compass.Root.transform;
            var start = ParentRect(strip);
            var band = Rect.MinMaxRect(start.xMin, start.yMin - Compass.LabelHeight, start.xMax, start.yMax);
            MoveOnto(bar, band);
            bar.gameObject.SetActive(true);
            var barRect = ParentRect(bar);
            var overlaps = bar.gameObject.activeInHierarchy && barRect.Overlaps(band);
            if (!overlaps)
            {
                var detail =
                    $"fixture precondition failed: the bar moved onto the band is {(bar.gameObject.activeInHierarchy ? "shown" : "not shown")} and spans x {barRect.xMin:0.0}-{barRect.xMax:0.0}, y {barRect.yMin:0.0}-{barRect.yMax:0.0}, against the band's x {band.xMin:0.0}-{band.xMax:0.0}, y {band.yMin:0.0}-{band.yMax:0.0}";
                bar.gameObject.SetActive(false);
                fixture.Run.Fail(name, detail);
                fixture.Note("compass_bottom_ignores_bars", false, $"{name}: not checked; {detail}");
                return;
            }

            Compass.Update(player);
            ForceUpdateCanvases();
            var moved = ParentRect(strip);
            var movedBand = Rect.MinMaxRect(moved.xMin, moved.yMin - Compass.LabelHeight, moved.xMax, moved.yMax);
            var down = moved.yMax < start.yMax - Tolerance;
            var clear = !movedBand.Overlaps(barRect);
            var gap = Mathf.Abs(moved.yMax - (barRect.yMin - Compass.AvoidanceGap)) <= Tolerance;
            bar.gameObject.SetActive(false);
            Compass.Update(player);
            ForceUpdateCanvases();
            var back = ParentRect(strip);
            var returned = Mathf.Abs(back.yMax - start.yMax) <= Tolerance && Mathf.Abs(back.yMin - start.yMin) <= Tolerance;
            Check(fixture.Run, name, down && clear && gap && returned,
                $"with the bar at y {barRect.yMin:0.0}-{barRect.yMax:0.0} over the band, the strip's top went from {start.yMax:0.0} to {moved.yMax:0.0} (expected {barRect.yMin - Compass.AvoidanceGap:0.0}); clear of the bar with its labels={clear}; back at {back.yMax:0.0} once the bar hid={returned}");

            fixture.Write(MappingConfig.CompassPositionEntry, CompassPosition.Bottom);
            try
            {
                Compass.Update(player);
                ForceUpdateCanvases();
                strip = (RectTransform)Compass.Root.transform;
                var bottom = ParentRect(strip);
                var bottomBand = Rect.MinMaxRect(bottom.xMin, bottom.yMin, bottom.xMax, bottom.yMax + Compass.LabelHeight);
                MoveOnto(bar, bottomBand);
                bar.gameObject.SetActive(true);
                var bottomOverlaps = ParentRect(bar).Overlaps(bottomBand);
                Compass.Update(player);
                ForceUpdateCanvases();
                var after = ParentRect(strip);
                var stayed = Mathf.Abs(after.yMin - bottom.yMin) <= Tolerance && Mathf.Abs(after.yMax - bottom.yMax) <= Tolerance;
                bar.gameObject.SetActive(false);
                fixture.Note("compass_bottom_ignores_bars", bottomOverlaps && stayed,
                    $"{name}: over the bottom band={bottomOverlaps}, the strip stayed at y {bottom.yMin:0.0}-{bottom.yMax:0.0}={stayed}");
            }
            finally
            {
                fixture.Write(MappingConfig.CompassPositionEntry, CompassPosition.Top);
                Compass.Update(player);
            }
        }

        // The boss check spawns a creature, which needs a single-player world.
        private static string BossPrecondition()
        {
            if (!ZNetScene.instance || !ZNet.instance || !ZNet.instance.IsServer() || ZNet.instance.GetPeerConnections() > 0)
                return "spawning the test creature needs a single-player world";
            return ZNetScene.instance.GetPrefab("Boar") ? null : "the test creature's prefab is unavailable";
        }

        private static RectTransform BossBar(EnemyHud enemyHud, Character boss)
        {
            var huds = (IDictionary)HudsField.GetValue(enemyHud);
            var data = huds.Contains(boss) ? huds[boss] : null;
            var gui = data == null ? null : AccessTools.Field(data.GetType(), "m_gui").GetValue(data) as GameObject;
            return gui ? gui.transform as RectTransform : null;
        }

        // Moves the rect's centre onto the given one, both in the strip parent's space.
        private static void MoveOnto(RectTransform rect, Rect target)
        {
            var parent = (RectTransform)Compass.Root.transform.parent;
            var offset = parent.TransformPoint(target.center) - parent.TransformPoint(ParentRect(rect).center);
            offset.z = 0f;
            rect.position += offset;
            ForceUpdateCanvases();
        }

        private static void CheckNavigationRestored(FeatureRun run, Navigation.TargetState original)
        {
            var now = Navigation.CaptureTarget();
            if (original.Pin == null)
            {
                Check(run, "compass_navigation_restored", now.Pin == null,
                    now.Pin == null ? "there was no navigation before the section, and none is left" : "a navigation target is left that was not there before the section");
                return;
            }
            var position = original.Temporary ? original.LastPosition : original.Pin.m_pos;
            var restored = now.Pin != null && now.Temporary == original.Temporary && now.Pin.m_name == original.Pin.m_name &&
                           now.Pin.m_type == original.Pin.m_type && now.Pin.m_pos == position &&
                           now.CreatureId == original.CreatureId && now.LastPosition == original.LastPosition;
            Check(run, "compass_navigation_restored", restored,
                now.Pin == null
                    ? "your navigation target was not set again"
                    : $"set again to '{now.Pin.m_name}' ({now.Pin.m_type}, {(now.Temporary ? "temporary" : "map pin")}) at {now.Pin.m_pos}, creature {now.CreatureId}; before: '{original.Pin.m_name}' ({original.Pin.m_type}, {(original.Temporary ? "temporary" : "map pin")}) at {position}, creature {original.CreatureId}");
        }

        // The strip's screen rectangle within the screen, horizontally centred, inside the top or
        // the bottom fifth, under the HUD's root.
        private static bool InBand(Hud hud, bool bottom, out string detail)
        {
            ForceUpdateCanvases();
            var root = Compass.Root;
            if (!root)
            {
                detail = "the strip is gone";
                return false;
            }
            var rect = MapFilter.ScreenRect((RectTransform)root.transform);
            var inBand = rect.width > 0f && rect.height > 0f && rect.xMin >= -1f && rect.xMax <= Screen.width + 1f &&
                         (bottom
                             ? rect.yMin >= -1f && rect.yMax <= Screen.height * 0.2f
                             : rect.yMax <= Screen.height + 1f && rect.yMin >= Screen.height * 0.8f) &&
                         Mathf.Abs(rect.center.x - Screen.width / 2f) <= 2f;
            detail = $"screen rect x {rect.xMin:0}-{rect.xMax:0}, y {rect.yMin:0}-{rect.yMax:0} on a {Screen.width}x{Screen.height} screen";
            return inBand && root.transform.IsChildOf(hud.m_rootObject.transform);
        }

        private static void ForceUpdateCanvases()
        {
            ForceUpdateCanvasesMethod.Invoke(null, null);
        }

        // In the space of the strip's parent, which the compass reads the bars in.
        private static Rect ParentRect(RectTransform rect)
        {
            return MapFilter.LocalRect((RectTransform)Compass.Root.transform.parent, rect);
        }

        // The size a marker is drawn at, in canvas units.
        private static float Size(RectTransform marker)
        {
            return marker.rect.width * marker.localScale.x;
        }

        private static Color ColorOf(RectTransform marker)
        {
            return (Color)ColorProperty.GetValue(marker.GetComponent(ImageType));
        }

        // The label's text, or null while it is hidden.
        private static string LabelText(RectTransform label)
        {
            return label && label.gameObject.activeSelf ? (string)TextProperty.GetValue(label.GetComponent(TextType)) : null;
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

        private struct BarState
        {
            internal RectTransform Rect;
            internal Vector3 Position;
            internal bool Active;
        }

        private sealed class Fixture
        {
            private readonly Dictionary<string, List<KeyValuePair<bool, string>>> _notes =
                new Dictionary<string, List<KeyValuePair<bool, string>>>();
            private readonly List<BarState> _bars = new List<BarState>();
            private Character _boss;
            private bool _bossFlag;

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

            // Navigates to the pin as the map does, without the start message.
            internal void Navigate(Minimap.PinData pin)
            {
                Navigation.RestoreTarget(new Navigation.TargetState { Pin = pin, LastPosition = pin.m_pos });
            }

            // The label the compass gives the pin, in the navigation label's format, with the
            // distance to the pin or to the position given.
            internal string LabelOf(Minimap.PinData pin)
            {
                return LabelOf(pin, pin.m_pos);
            }

            internal string LabelOf(Minimap.PinData pin, Vector3 position)
            {
                var name = (pin.m_name ?? string.Empty).Replace("\n", " ").Trim();
                var meters = Mathf.RoundToInt(global::Utils.DistanceXZ(Player.transform.position, position));
                return Localization.instance.Localize(name) + " [" + meters + "m]";
            }

            // The angle from the heading to the pin, in degrees.
            internal float Delta(Minimap.PinData pin)
            {
                var origin = Player.transform.position;
                return Mathf.DeltaAngle(Camera.eulerAngles.y,
                    Mathf.Atan2(pin.m_pos.x - origin.x, pin.m_pos.z - origin.z) * Mathf.Rad2Deg);
            }

            // The other pins shown at least as near the centre as this one.
            internal int NearerCentre(Minimap.PinData pin)
            {
                var offset = Mathf.Abs(Delta(pin));
                return Expected().Count(other => !ReferenceEquals(other, pin) && Mathf.Abs(Delta(other)) <= offset + 0.01f);
            }

            // The pins shown within the focus angle of the centre.
            internal int InFocus()
            {
                return Expected().Count(pin => Mathf.Abs(Delta(pin)) <= Compass.FocusAngle);
            }

            // Why a test pin cannot be judged as the one faced: it is pushed out of the markers,
            // or a pin of yours shown lies at least as near the centre. Null when it can.
            internal string FacedBlocked(Minimap.PinData pin)
            {
                var crowded = Crowded(pin);
                if (crowded != null) return crowded;
                var nearer = NearerCentre(pin);
                return nearer == 0
                    ? null
                    : $"inconclusive: {nearer} pin(s) of yours lie at least as near the centre as the test pin; run the section away from them";
            }

            // Records a bar's place and visibility, to be put back, before the section changes them.
            internal RectTransform Track(RectTransform bar)
            {
                _bars.Add(new BarState { Rect = bar, Position = bar.localPosition, Active = bar.gameObject.activeSelf });
                return bar;
            }

            // A non-persistent creature 5 m ahead, recorded with its boss flag before the flag is
            // set. Null when it has no valid ZDO.
            internal Character SpawnBoss(GameObject prefab)
            {
                var go = UnityEngine.Object.Instantiate(prefab, At(Heading, 5f), Quaternion.identity);
                SmokeSession.TrackSpawn(go);
                var view = go.GetComponent<ZNetView>();
                var character = go.GetComponent<Character>();
                if (!view || !view.IsValid() || view.GetZDO() == null || !character)
                {
                    if (SmokeSession.DestroyTestObject(go, 0, "compass smoke")) SmokeSession.ReleaseSpawn(go);
                    return null;
                }
                view.GetZDO().Persistent = false;
                _boss = character;
                _bossFlag = character.m_boss;
                character.m_boss = true;
                return character;
            }

            // Puts the bars back, newest change first, then takes the creature's flag, its bar
            // and the creature away. Safe to run more than once.
            internal void RestoreBars()
            {
                for (var i = _bars.Count - 1; i >= 0; i--)
                {
                    var bar = _bars[i];
                    if (!bar.Rect) continue;
                    bar.Rect.localPosition = bar.Position;
                    bar.Rect.gameObject.SetActive(bar.Active);
                }
                _bars.Clear();
                var boss = _boss;
                _boss = null;
                if (!boss) return;
                boss.m_boss = _bossFlag;
                if (EnemyHud.instance) EnemyHud.instance.RemoveCharacterHud(boss);
                if (SmokeSession.DestroyTestObject(boss.gameObject, 0, "compass smoke"))
                    SmokeSession.ReleaseSpawn(boss.gameObject);
            }

            // The pins the map's own rule shows at the current heading: those the minimap would
            // draw, within range and the half view, nearest first, at most the marker count. The
            // navigation target has its own marker, so it is left out unless asked for.
            internal List<Minimap.PinData> Expected(bool excludeTarget = true)
            {
                var origin = Player.transform.position;
                var heading = Camera.eulerAngles.y;
                var types = (bool[])VisibleIconTypesField.GetValue(MapInstance);
                var shared = (float)SharedFadeField.GetValue(MapInstance) > 0f;
                var target = excludeTarget ? Navigation.TargetPin : null;
                return Map.GetAllPins()
                    .Where(pin => pin != null && !ReferenceEquals(pin, target) && pin.m_icon && pin.m_worldSize <= 0f &&
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
