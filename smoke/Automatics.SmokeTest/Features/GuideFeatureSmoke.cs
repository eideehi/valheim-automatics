using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Automatics.AutomaticMapping;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using MappingConfig = Automatics.AutomaticMapping.Config;
using MappingModule = Automatics.AutomaticMapping.Module;
using Object = UnityEngine.Object;

namespace Automatics.SmokeTest
{
    // Navigates to an unsaved test pin about 60 m behind the camera, lets the guide swarm run for
    // real frames and reads its lights back, following each light by its random seed: the burst
    // that spreads out from your body in every direction and stays above the ground, also on a
    // steep slope, the stream's height over the ground and its flow toward the target with the
    // later lights leaving you along the line, the column over the target, its sway, how it follows
    // a moved or moving target and how it stands on the surface over a buried or sunk target, the
    // stream ending at a near target, hiding on the large map and the stream resuming without a
    // burst, the swarm scattering when navigation is cleared by hand (also held while the large map
    // is open), the navigation ending within the arrival distance and the swarm scattering there,
    // a navigation started next to its target, the arrival distance setting, the runtime option and
    // the teardown when the navigation is cleared. It also logs which particle material the swarm
    // resolved. Bounds that follow the swarm's look are derived from its constants.
    //
    // You stand still: the section moves the test pin, and the game's own player update drives the
    // navigation every frame. After each scatter it waits for the scattering swarm to remove itself
    // and starts navigation on the pin at 60 m again, so the later checks have one.
    //
    // The test pin is unsaved, so no map save can keep it, and it is the only pin the section
    // removes. navigation_arrival_distance is set to its default for the section. Automatic
    // Mapping and enable_navigation_guide are turned on for the section when they are off, and
    // an unbound navigation_start_key is bound to its default. The navigation target, the map
    // mode and these settings are put back when the section ends, and by the session hand-back
    // when the run is cut short.
    internal static class GuideFeatureSmoke
    {
        private const float TargetDistance = 60f;
        private const float StartTimeoutSeconds = 3f;
        private const int MinLights = 30;
        private const float MinAlpha = 0.1f;
        private const float MinVisibleShare = 0.5f;
        private const float ColorStep = 1f / 255f;

        // A light's age is the time since it was born; travel lights are stream lights this much
        // older than the time they take to spread out and gather into the stream.
        private const float TravelMargin = 0.3f;
        private const float TravelAge = NavigationGuide.RadiateTime + NavigationGuide.GatherTime + TravelMargin;

        // Burst lights are born with a longer lifetime than the lights that join the stream
        // directly; the two are told apart halfway between.
        private const float BurstLifetimeSplit = (NavigationGuide.BurstLifetime + NavigationGuide.FlowLifetime) * 0.5f;

        private const float BurstWindowSeconds = 0.1f;
        private const float BodyStartRadius = 0.3f;
        private const float MinSpreadDistance = 0.8f;
        private const int MinQuadrants = 3;
        private const float BehindCosine = -0.3f;
        private const float MinBehindShare = 0.1f;
        private const float MinBurstRise = 0.4f;
        private const float MinBurstDrop = 0.2f;
        private const float MinVerticalShare = 0.15f;
        private const float TravelWatchSeconds = 0.5f;
        private const float BurstSlackSeconds = 1f;

        // The lights born from FlowBirthDelay after a swarm starts, and those born in the first
        // ResumeWindowSeconds after it is shown again (watched for ResumeWatchSeconds), leave you
        // along the line toward the target: none is more than MaxBehind behind you.
        private const float FlowBirthDelay = 1f;
        private const float ResumeWindowSeconds = 0.5f;
        private const float ResumeWatchSeconds = 1f;
        private const float MaxBehind = 0.3f;

        private const float HeightMargin = 0.4f;
        private const float MinHeightAboveGround =
            NavigationGuide.HoverHeight - (NavigationGuide.StreamHalfHeight + HeightMargin);
        private const float MaxHeightAboveGround =
            NavigationGuide.HoverHeight + (NavigationGuide.StreamHalfHeight + HeightMargin);
        private const float RequiredShareInBand = 0.8f;

        private const float FlowWatchSeconds = 3f;
        private const float MinFlowReach = 12f;
        private const float MaxSideways =
            NavigationGuide.StreamHalfWidth + NavigationGuide.WobbleAmplitude + 0.5f;
        private const float MaxFlowReach = NavigationGuide.TrailLength + 1f;

        private const float ColumnMargin = 0.2f;

        // The column's top sways by up to SwayAmplitude along each horizontal axis, so a light may
        // lie that much farther out along a diagonal.
        private const float Sqrt2 = 1.41421356f;
        private const float SwayReach = Sqrt2 * NavigationGuide.SwayAmplitude;
        private const float MaxColumnSpread =
            NavigationGuide.ColumnRadius + NavigationGuide.JitterAmplitude + SwayReach + ColumnMargin;
        private const float MinColumnHeight = NavigationGuide.ColumnCentreHeight -
                                              (NavigationGuide.ColumnHalfHeight + NavigationGuide.JitterAmplitude + ColumnMargin);
        private const float MaxColumnHeight = NavigationGuide.ColumnCentreHeight +
                                              (NavigationGuide.ColumnHalfHeight + NavigationGuide.JitterAmplitude + ColumnMargin);
        private const float ColumnMotionSeconds = 0.5f;
        private const float MinColumnMotion = 0.05f;
        private const float ColumnSideStep = 3f;
        private const float ColumnFollowSeconds = 1f;
        private const float ColumnFollowTolerance = 0.5f;
        private const float ColumnResettleSeconds = 1.5f;

        private const float NearTargetDistance = 12f;
        private const float TrailSettleSeconds = 1.5f;
        private const float TrailWatchSeconds = 2f;
        private const float MaxOvershoot = NavigationGuide.ColumnRadius + 0.5f;
        private const float NearCentreDistance = 2f;

        // Over a time longer than both sway periods, the column's mean offset from its axis follows
        // the mean of the sway its lights are prescribed at their heights.
        private const float SwayWatchSeconds = 7.5f;
        private const float MinSwaySlope = 0.5f;
        private const float MaxSwaySlope = 1.5f;
        private const float MinSwaySpan = NavigationGuide.SwayAmplitude * 0.5f;

        // A target moving sideways keeps its column around it: after the first moments the column
        // lags it by less than half of what following alone would leave.
        private const float MovingTargetSpeed = 8f;
        private const float MovingTargetSeconds = 2f;
        private const float MovingTargetSkipSeconds = 0.3f;
        private const float MaxFollowLag = 0.5f * MovingTargetSpeed / NavigationGuide.ColumnFollowRate;

        // Where the column stands over a buried, raised or sunk target, searched for on a grid of
        // the loaded terrain around you, the points kept clear of the arrival distance.
        private const float BuriedDepth = 2f;
        private const float RaisedHeight = 3f;
        private const float SunkDepth = 2f;
        private const float MinWaterDepth = 1f;
        private const float SearchRadius = 60f;
        private const float SearchStep = 2f;
        private const float PinClearance = 5f;

        // A burst on a steep slope, evaluated through the burst step the stream makes each frame.
        private const float MinSlopeRise = 1.5f;
        private const int SlopeSeeds = 300;
        private const int SlopeAges = 10;
        private const float SlopeFrameRate = 60f;
        private const float FloorTolerance = 0.05f;
        private const float MinBurstFloor = NavigationGuide.BurstFloor - FloorTolerance;
        private const float BurstAge = NavigationGuide.RadiateTime + NavigationGuide.GatherTime;

        // With no such slope loaded, one is raised: a round mound MoundHeight high whose sides fall
        // straight to the ground MoundRadius out (about 2 m over 2 m), its top on the grid
        // MoundDistance from you and with no piece within MoundClearance. It stays, as the smoke
        // runs in a test world.
        private const float MoundDistance = 20f;
        private const float MoundRadius = 3f;
        private const float MoundHeight = 3f;
        private const float MoundClearance = 5f;
        private const int MoundFrames = 30;
        private static readonly float[] MoundBearings = { 0f, 45f, -45f, 90f, -90f };

        private const float ArrivalMargin = 0.5f;
        private const float ArrivalProbeRise = 3f;
        private const int OutsideFrames = 3;
        private const int ArrivalFrames = 2;
        private const float WideArrivalDistance = 10f;
        private const float OffArrivalDistance = 0f;

        private const float ScatterWatchSeconds = 0.8f;
        private const float RemovalMargin = 0.5f;
        private const float MinOutwardShare = 0.75f;

        // A swarm held from a map click keeps its lights while the map stays open, and scatters
        // within a few frames of the map closing.
        private const float ReadyTimeoutSeconds = 3f;
        private const float SpineTolerance = 0.25f;
        private const float HoldSeconds = 0.5f;
        private const float HeldPositionTolerance = 0.001f;
        private const int ReleaseFrames = 2;
        private const int OptionOffFrames = 2;
        private const float AbsentWatchSeconds = 0.5f;

        private const float StartInsideDistance = 3f;
        private const float RearmDistance = 8f;
        private const float KeepSeconds = 1f;
        private const int RearmFrames = 2;

        private static readonly FieldInfo TargetPinField = AccessTools.Field(typeof(Navigation), "_targetPin");
        private static readonly MethodInfo SetTargetMethod = AccessTools.Method(typeof(Navigation), "SetTarget");

        // How fast a stream light closes on its place, for the follow step of the slope check.
        private static readonly FieldInfo FollowRateField = AccessTools.Field(typeof(NavigationGuide), "FollowRate");

        // Applies terrain settings to a terrain compiler at once. A TerrainOp object would send
        // only its prefab's settings, so the mound's own settings go through this.
        private static readonly MethodInfo DoOperationMethod = AccessTools.Method(typeof(TerrainComp), "DoOperation");

        public static IEnumerator Run(FeatureRun run)
        {
            var player = Player.m_localPlayer;
            var map = Minimap.instance;
            if (!run.IsCurrent || !player || !map || !GameCamera.instance || !Hud.instance ||
                !ZoneSystem.instance || !ZNet.instance || !ZNet.instance.IsServer() ||
                ZNet.instance.GetPeerConnections() > 0)
            {
                run.Skip("guide_section", "requires a loaded single-player world, minimap, HUD and camera");
                yield break;
            }
            if (player.InInterior())
            {
                run.Skip("guide_section", "the swarm flies at your height indoors; run it outdoors");
                yield break;
            }
            if (Game.IsPaused())
            {
                run.Skip("guide_section", "the game is paused, so navigation is not shown");
                yield break;
            }
            if (TargetPinField == null || SetTargetMethod == null)
            {
                run.Fail("guide_section", "Navigation's target or SetTarget could not be found by reflection");
                yield break;
            }

            // The module, the option and the click key, set up for the section.
            var state = new SectionState();
            var held = new Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration>();
            var module = new SmokeSetup.SetupResult();
            var setup = SmokeSetup.EnableModule(run.RunId, held, MappingConfig.ModuleEntry,
                () => MappingModule.Running, module);
            while (setup.MoveNext()) yield return setup.Current;
            if (!run.IsCurrent) yield break;
            if (!module.Ok)
            {
                run.Skip("guide_section", "Automatic Mapping is not running: " + module.Reason);
                ReportSettingsRestored(run, state);
                yield break;
            }
            var changed = new List<string>();
            if (module.Changed) changed.Add("Automatic Mapping turned on");
            if (!MappingConfig.EnableNavigationGuide)
            {
                if (!TakeGuideSetting(run, state, out var guideRefusal) ||
                    !SmokeSession.WriteSetting(run.RunId, state.GuideSetting, true, out guideRefusal))
                {
                    run.Skip("guide_section", "enable_navigation_guide is off and could not be turned on for the section: " + guideRefusal);
                    ReportSettingsRestored(run, state);
                    yield break;
                }
                changed.Add("enable_navigation_guide turned on");
            }
            if (MappingConfig.NavigationStartKey.MainKey == KeyCode.None)
            {
                // A refusal is logged; the map click checks then skip with the key unbound.
                var key = SmokeSetup.Field<KeyboardShortcut>(typeof(MappingConfig), "_navigationStartKey");
                if (key != null && SmokeSetup.Write(run, held, key, key.DefaultValue))
                    changed.Add($"navigation_start_key bound to {key.Value}");
            }
            if (changed.Count > 0)
                run.Pass("guide_setup", string.Join("; ", changed) + " for the section, and put back at its end");

            var originalTarget = Navigation.CaptureTarget();
            var originalMode = map.m_mode;
            var cameraForward = Vector3.ProjectOnPlane(GameCamera.instance.transform.forward, Vector3.up);
            if (cameraForward.sqrMagnitude < 0.001f) cameraForward = player.transform.forward;
            cameraForward.Normalize();

            Minimap.PinData testPin = null;
            var restoredNavigation = false;
            Func<string, bool> handBack = _ =>
            {
                if (map && map.m_mode != originalMode) map.SetMapMode(originalMode);
                if (testPin != null && map) map.RemovePin(testPin);
                testPin = null;
                if (!restoredNavigation) restoredNavigation = RestoreTarget(originalTarget);
                return true;
            };
            SmokeSession.TrackHandBack(handBack);

            try
            {
                var playerPos = player.transform.position;
                var farPos = GroundPoint(playerPos - cameraForward * TargetDistance, playerPos.y);
                testPin = map.AddPin(farPos, Minimap.PinType.Icon3, "", false, false);
                if (testPin == null)
                {
                    run.Skip("guide_section", "the test pin could not be added");
                    yield break;
                }

                // The arrival checks are made at the setting's default distance, whatever yours is;
                // your value is put back with the section's other setting.
                if (!TakeArrivalSetting(run, state, out var arrivalRefusal))
                {
                    run.Skip("guide_section", "navigation_arrival_distance could not be set for the section: " + arrivalRefusal);
                    yield break;
                }
                CheckMapClick(run, map, testPin);
                SetTargetMethod.Invoke(null, new object[] { testPin });

                // The first update creates the swarm with its burst from your body, so the burst
                // is watched from the frame the swarm appears.
                var deadline = Time.time + StartTimeoutSeconds;
                while (run.IsCurrent && Time.time < deadline && !NavigationGuide.Root) yield return null;
                if (!run.IsCurrent) yield break;
                if (!NavigationGuide.Root)
                {
                    run.Fail("guide_swarm_alive",
                        $"no guide swarm was created within {StartTimeoutSeconds}s (material: {NavigationGuide.MaterialSource ?? "none"})");
                    yield break;
                }
                CheckMaterial(run);

                var burst = new BurstWatch();
                var steps = burst.Watch(run, player);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;
                if (burst.Evaluate(out var burstDetail)) run.Pass("guide_radiates_from_body", burstDetail);
                else run.Fail("guide_radiates_from_body", burstDetail);

                CheckAlive(run);
                CheckHeights(run, player);
                steps = CheckFlow(run, player, burst.Start);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;

                steps = CheckDestination(run, player, testPin);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;

                steps = CheckColumnSways(run);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;

                steps = CheckColumnFollowsMovingTarget(run, player, testPin);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;

                // One look at the loaded terrain around you, for the raised column and the slope;
                // the slope check looks again when it raises a mound.
                var grid = new TerrainGrid(player.transform.position);
                steps = CheckColumnRaised(run, player, testPin, grid);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;
                steps = CheckBurstAboveSlope(run, player, grid);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;

                steps = CheckTrailEnd(run, player, testPin);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;

                steps = CheckLargeMap(run, player, map, originalMode);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;

                steps = CheckMapClear(run, map, testPin, farPos, state);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;

                steps = CheckManualClear(run, testPin, farPos, state);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;

                steps = CheckArrival(run, player, testPin, state);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;
                steps = Restart(run, testPin, farPos, true);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;

                steps = CheckStartedInside(run, player, testPin, state);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;
                steps = Restart(run, testPin, farPos, true);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;

                steps = CheckArrivalSetting(run, player, testPin, farPos, state);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;

                CheckDistanceFormat(run, player, map, testPin);
                steps = CheckOption(run, player, testPin, farPos, state);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;

                Navigation.ClearTarget();
                yield return null;
                var left = CountGuideObjects();
                if (state.Leftover != null)
                    run.Skip("guide_destroyed_on_clear", "a guide object left from an arrival would be counted: " + state.Leftover);
                else if (!NavigationGuide.Root && left == 0 && TargetPinField.GetValue(null) == null)
                    run.Pass("guide_destroyed_on_clear");
                else
                    run.Fail("guide_destroyed_on_clear",
                        $"{left} guide object(s) are left in the scene a frame after the navigation was cleared");
            }
            finally
            {
                try
                {
                    try
                    {
                        // Every setting the section took on, put back at its end.
                        if (!state.SettingsReported) ReportSettingsRestored(run, state);
                    }
                    finally
                    {
                        if (handBack("guide feature smoke")) SmokeSession.ReleaseHandBack(handBack);
                    }
                }
                finally
                {
                    if (originalTarget.Pin == null)
                        run.Pass("guide_navigation_restored", "there was no navigation before the section, and none is left");
                    else if (restoredNavigation)
                        run.Pass("guide_navigation_restored", "your navigation target is set again");
                    else
                        run.Fail("guide_navigation_restored",
                            "your original pin disappeared; navigation was restored through a temporary marker instead");
                }
            }
        }

        // Both particle systems draw with the swarm's one material.
        private static void CheckMaterial(FeatureRun run)
        {
            var systems = new[] { NavigationGuide.Stream, NavigationGuide.Column };
            var names = new[] { "stream", "column" };
            var details = new List<string>();
            var passed = NavigationGuide.MaterialSource != null;
            for (var i = 0; i < systems.Length; i++)
            {
                var renderer = systems[i] ? systems[i].GetComponent<ParticleSystemRenderer>() : null;
                var material = renderer ? renderer.sharedMaterial : null;
                if (!material || !material.shader)
                {
                    details.Add($"{names[i]}: no resolved particle material");
                    passed = false;
                    continue;
                }

                // Additive: a fixed additive shader, or blend factors set to add onto the background.
                var shader = material.shader;
                var byName = shader.name.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0;
                var byBlend = material.HasProperty("_DstBlend") &&
                              material.GetInt("_DstBlend") == (int)UnityEngine.Rendering.BlendMode.One;
                var blend = material.HasProperty("_SrcBlend") && material.HasProperty("_DstBlend")
                    ? $"src={(UnityEngine.Rendering.BlendMode)material.GetInt("_SrcBlend")} dst={(UnityEngine.Rendering.BlendMode)material.GetInt("_DstBlend")}"
                    : "fixed blend";
                var additive = shader.isSupported && (byName || byBlend);
                details.Add(
                    $"{names[i]}: renderer shader '{shader.name}', supported={shader.isSupported}, {blend}{(additive ? ", additive" : shader.isSupported ? ", not additive" : "")}");
                passed &= additive;
            }

            var detail = $"{NavigationGuide.MaterialSource ?? "no material source"}; {string.Join("; ", details)}";
            if (passed) run.Pass("guide_material", detail);
            else run.Fail("guide_material", detail);
        }

        private static void CheckAlive(FeatureRun run)
        {
            var root = NavigationGuide.Root;
            var stream = NavigationGuide.Stream;
            var column = NavigationGuide.Column;
            var overlay = Navigation.Label;
            if (!root || !stream || !column)
            {
                run.Fail("guide_swarm_alive", "the guide swarm is gone");
                return;
            }

            var color = MappingConfig.NavigationGuideColor;
            var systems = root.GetComponentsInChildren<ParticleSystem>(true).Length;
            var lights = root.GetComponentsInChildren<Light>(true);
            var coloured = lights.Count(x => x.type == LightType.Point && SameColor(x.color, color));
            var networked = root.GetComponentsInChildren<ZNetView>(true).Length;
            var labelHidden = overlay && !overlay.activeSelf;
            var streamLights = ReadLights(stream);
            var columnLights = ReadLights(column);
            var streamVisible = VisibleShare(streamLights, color);
            var columnVisible = VisibleShare(columnLights, color);
            var drawing = Drawing.Of(root);
            var shown = NavigationGuide.Shown && drawing.Drawn;
            var detail =
                $"{systems} particle system(s) and {lights.Length} light(s), {coloured} of them point lights in navigation_guide_color, {networked} ZNetView, root object={root.transform.parent == null}, shown={shown} ({drawing}); " +
                $"stream {streamLights.Length} lights, {streamVisible:P0} with alpha above {MinAlpha} in the guide colour; column {columnLights.Length} lights, {columnVisible:P0} (bounds: at least {MinLights} lights and {MinVisibleShare:P0} each); " +
                $"HUD label {(labelHidden ? "off-screen" : overlay ? "shown" : "missing")}";
            if (shown && root.transform.parent == null && systems == 2 && lights.Length == 2 &&
                coloured == 2 && networked == 0 && streamLights.Length >= MinLights &&
                columnLights.Length >= MinLights && streamVisible >= MinVisibleShare &&
                columnVisible >= MinVisibleShare && labelHidden)
                run.Pass("guide_swarm_alive", detail);
            else
                run.Fail("guide_swarm_alive", detail);
        }

        // The travel lights' height over the ground under each of them, measured by the check.
        private static void CheckHeights(FeatureRun run, Player player)
        {
            var travelling = ReadLights(NavigationGuide.Stream).Where(x => Age(x) > TravelAge).ToArray();
            if (travelling.Length == 0)
            {
                run.Fail("guide_height_above_ground", $"no travel light (older than {TravelAge:0.0} s) is alive");
                return;
            }

            var heights = travelling
                          .Select(x => x.position.y - GroundPoint(x.position, player.transform.position.y).y)
                          .OrderBy(x => x)
                          .ToList();
            var count = heights.Count;
            var inBand = heights.Count(x => x >= MinHeightAboveGround && x <= MaxHeightAboveGround);
            var share = (float)inBand / count;
            var mean = heights.Average();
            var detail =
                $"mean {mean:0.00} m; {inBand}/{count} travel lights (older than {TravelAge:0.0} s: RadiateTime + GatherTime + {TravelMargin} s) between {MinHeightAboveGround:0.0} and {MaxHeightAboveGround:0.0} m above the ground or sea surface (HoverHeight ± (StreamHalfHeight + {HeightMargin} m)); min {heights[0]:0.00}, median {heights[count / 2]:0.00}, max {heights[count - 1]:0.00}";
            if (mean >= MinHeightAboveGround && mean <= MaxHeightAboveGround && share >= RequiredShareInBand)
                run.Pass("guide_height_above_ground", detail);
            else
                run.Fail("guide_height_above_ground", detail);
        }

        // The travel lights flow along the straight line toward the target, close to it and no
        // farther than the trail reaches, and the lights born after the burst leave you along that
        // line instead of spreading out around you.
        private static IEnumerator CheckFlow(FeatureRun run, Player player, float swarmStart)
        {
            var later = new FlowWatch(swarmStart + FlowBirthDelay, float.MaxValue);
            var reach = float.MinValue;
            var widest = 0f;
            var samples = 0;
            var frames = 0;
            var start = Time.time;
            while (run.IsCurrent && Time.time - start < FlowWatchSeconds)
            {
                var playerPos = player.transform.position;
                var toward = Toward(playerPos);
                var side = Side(toward);
                foreach (var light in ReadLights(NavigationGuide.Stream))
                {
                    if (Age(light) <= TravelAge) continue;
                    var offset = Flat(light.position - playerPos);
                    reach = Mathf.Max(reach, Vector3.Dot(offset, toward));
                    widest = Mathf.Max(widest, Mathf.Abs(Vector3.Dot(offset, side)));
                    samples++;
                }
                later.Sample(player);
                frames++;
                yield return null;
            }
            if (!run.IsCurrent) yield break;

            var distance = Utils.DistanceXZ(player.transform.position, Navigation.TargetPosition);
            var detail = samples == 0
                ? $"no travel light (older than {TravelAge:0.0} s) over {frames} frames"
                : $"over {frames} frames ({samples} travel-light samples) with the target {distance:0} m away, the farthest was {reach:0.0} m along the line (bounds: at least {MinFlowReach} m, at most {MaxFlowReach} m: TrailLength + 1 m) and the widest {widest:0.00} m to its side (bound {MaxSideways:0.00} m: StreamHalfWidth + WobbleAmplitude + 0.5 m)";
            detail += $"; lights born {FlowBirthDelay} s or more after the start: {later.Describe()} (bounds {MaxBehind} m behind, {MaxSideways:0.00} m to the side)";
            if (samples > 0 && reach >= MinFlowReach && reach <= MaxFlowReach && widest <= MaxSideways &&
                later.Lights > 0 && later.MostBehind <= MaxBehind && later.Widest <= MaxSideways)
                run.Pass("guide_flows_toward_target", detail);
            else
                run.Fail("guide_flows_toward_target", detail);
        }

        // The column's lights stay around the vertical axis through the target and keep moving,
        // and the column follows the target when it moves sideways.
        private static IEnumerator CheckDestination(FeatureRun run, Player player, Minimap.PinData pin)
        {
            var farPos = pin.m_pos;
            var held = new ColumnBounds();
            var resettled = new ColumnBounds();
            var tracked = 0;
            var motion = 0f;
            var followedAfter = -1f;
            var lastOffset = float.NaN;
            try
            {
                var before = Positions(ReadLights(NavigationGuide.Column));
                held.Sample(NavigationGuide.Column);
                var start = Time.time;
                while (run.IsCurrent && Time.time - start < ColumnMotionSeconds)
                {
                    yield return null;
                    held.Sample(NavigationGuide.Column);
                }
                if (!run.IsCurrent) yield break;
                var after = Positions(ReadLights(NavigationGuide.Column));
                var moves = before.Where(x => after.ContainsKey(x.Key))
                                  .Select(x => Vector3.Distance(x.Value, after[x.Key]))
                                  .ToList();
                tracked = moves.Count;
                motion = tracked > 0 ? moves.Average() : 0f;

                var side = Side(Toward(player.transform.position));
                Map.MovePin(pin, GroundPoint(farPos + side * ColumnSideStep, farPos.y));
                var moved = Time.time;
                while (run.IsCurrent)
                {
                    yield return null;
                    var since = Time.time - moved;
                    if (followedAfter < 0f && since <= ColumnFollowSeconds)
                    {
                        lastOffset = ColumnOffset(NavigationGuide.Column);
                        if (lastOffset <= ColumnFollowTolerance) followedAfter = since;
                    }
                    if (since >= ColumnResettleSeconds) break;
                }
                if (!run.IsCurrent) yield break;
                resettled.Sample(NavigationGuide.Column);
            }
            finally
            {
                Map.MovePin(pin, farPos);
            }

            var detail =
                $"over {ColumnMotionSeconds} s: {held.Describe()}; {tracked} lights tracked by seed moved a mean {motion:0.000} m (bound above {MinColumnMotion} m); " +
                $"with the target moved {ColumnSideStep} m sideways the column's mean was {lastOffset:0.00} m from the new axis {(followedAfter >= 0f ? $"after {followedAfter:0.00} s" : $"at {ColumnFollowSeconds} s")} (bound {ColumnFollowTolerance} m within {ColumnFollowSeconds} s); " +
                $"after {ColumnResettleSeconds} s: {resettled.Describe()}; " +
                $"bounds {MaxColumnSpread:0.00} m from the axis (ColumnRadius + JitterAmplitude + √2 × SwayAmplitude + {ColumnMargin} m) and {MinColumnHeight:0.00}–{MaxColumnHeight:0.00} m above the target (ColumnCentreHeight ± (ColumnHalfHeight + JitterAmplitude + {ColumnMargin} m))";
            if (held.Holds && tracked > 0 && motion > MinColumnMotion && followedAfter >= 0f && resettled.Holds)
                run.Pass("guide_destination_swarm", detail);
            else
                run.Fail("guide_destination_swarm", detail);
        }

        // With the pin still, the column bends slowly: at each look, the column's mean horizontal
        // offset from its axis is set against the mean of the sway each light is prescribed at the
        // height it is seen at, and over a time longer than both sway periods the one follows the
        // other with a slope near 1. A still column gives a slope near 0, and the whole column
        // shifted by the top's sway one near 2.
        private static IEnumerator CheckColumnSways(FeatureRun run)
        {
            // The column was just moved back by the previous check.
            var settle = Time.time;
            while (run.IsCurrent && Time.time - settle < ColumnResettleSeconds) yield return null;
            if (!run.IsCurrent) yield break;

            var prescribed = new List<float>();
            var observed = new List<float>();
            var low = new Vector2(float.MaxValue, float.MaxValue);
            var high = new Vector2(float.MinValue, float.MinValue);
            var widest = 0f;
            var frames = 0;
            var positions = 0;
            var start = Time.time;
            while (run.IsCurrent && Time.time - start < SwayWatchSeconds)
            {
                yield return null;
                var column = ReadLights(NavigationGuide.Column);
                if (column.Length == 0) continue;

                var axis = Navigation.TargetPosition;
                var centre = NavigationGuide.ColumnCentre;
                var time = Time.time;
                var offset = Vector2.zero;
                var sway = Vector2.zero;
                foreach (var light in column)
                {
                    var flat = new Vector2(light.position.x - axis.x, light.position.z - axis.z);
                    offset += flat;
                    sway += PrescribedSway(light, centre, time);
                    widest = Mathf.Max(widest, flat.magnitude);
                }
                offset /= column.Length;
                sway /= column.Length;
                prescribed.Add(sway.x);
                observed.Add(offset.x);
                prescribed.Add(sway.y);
                observed.Add(offset.y);
                low = Vector2.Min(low, sway);
                high = Vector2.Max(high, sway);
                frames++;
                positions += column.Length;
            }
            if (!run.IsCurrent) yield break;

            var slope = Slope(prescribed, observed);
            var span = frames > 0 ? high - low : Vector2.zero;
            var detail = frames == 0
                ? $"no column light over {SwayWatchSeconds} s"
                : $"over {SwayWatchSeconds} s (SwayPeriodX {NavigationGuide.SwayPeriodX} s, SwayPeriodZ {NavigationGuide.SwayPeriodZ} s) and {frames} frames ({positions} light positions): the least-squares slope of the column's mean horizontal offset from the axis on the mean prescribed sway (SwayAt at each light's height level, both axes pooled, with an intercept) was {slope:0.00} (bounds {MinSwaySlope}–{MaxSwaySlope}); " +
                  $"the prescribed mean spanned {span.x:0.000} m along x and {span.y:0.000} m along z (bound at least {MinSwaySpan:0.000} m each: half SwayAmplitude {NavigationGuide.SwayAmplitude} m); " +
                  $"the farthest light was {widest:0.00} m from the axis (bound {MaxColumnSpread:0.00} m: ColumnRadius + JitterAmplitude + √2 × SwayAmplitude + {ColumnMargin} m)";
            if (frames > 0 && slope >= MinSwaySlope && slope <= MaxSwaySlope && span.x >= MinSwaySpan &&
                span.y >= MinSwaySpan && widest <= MaxColumnSpread)
                run.Pass("guide_column_sways", detail);
            else
                run.Fail("guide_column_sways", detail);
        }

        // With the pin moving sideways at a steady speed, the column keeps its bounds around the
        // moving axis and, once its own sway is taken out, lags the target by less than half of
        // what following alone would leave (speed / ColumnFollowRate). The pin is put back.
        private static IEnumerator CheckColumnFollowsMovingTarget(FeatureRun run, Player player, Minimap.PinData pin)
        {
            var farPos = pin.m_pos;
            var side = Side(Toward(player.transform.position));
            var widest = 0f;
            var lagSum = 0f;
            var observations = 0;
            var positions = 0;
            var travelled = 0f;
            try
            {
                var start = Time.time;
                while (run.IsCurrent)
                {
                    yield return null;
                    var since = Time.time - start;
                    // This frame's lights followed the target where it was put a frame ago, which
                    // is where it still is.
                    var column = ReadLights(NavigationGuide.Column);
                    if (since > MovingTargetSkipSeconds && column.Length > 0)
                    {
                        var axis = Navigation.TargetPosition;
                        var centre = NavigationGuide.ColumnCentre;
                        var time = Time.time;
                        var along = 0f;
                        foreach (var light in column)
                        {
                            var flat = Flat(light.position - axis);
                            widest = Mathf.Max(widest, flat.magnitude);
                            var sway = PrescribedSway(light, centre, time);
                            along += Vector3.Dot(flat - new Vector3(sway.x, 0f, sway.y), side);
                        }
                        lagSum -= along / column.Length;
                        observations++;
                        positions += column.Length;
                    }
                    if (since >= MovingTargetSeconds) break;

                    travelled = MovingTargetSpeed * since;
                    Map.MovePin(pin, GroundPoint(farPos + side * travelled, farPos.y));
                }
                if (!run.IsCurrent) yield break;
            }
            finally
            {
                Map.MovePin(pin, farPos);
            }

            var moved = Time.time;
            while (run.IsCurrent && Time.time - moved < ColumnResettleSeconds) yield return null;
            if (!run.IsCurrent) yield break;

            var lag = observations > 0 ? lagSum / observations : float.NaN;
            var detail = observations == 0
                ? $"no column light seen after the first {MovingTargetSkipSeconds} s"
                : $"with the pin moved {travelled:0.0} m sideways at {MovingTargetSpeed} m/s for {MovingTargetSeconds} s, over {observations} looks after the first {MovingTargetSkipSeconds} s ({positions} light positions): the farthest light was {widest:0.00} m from the moving axis (bound {MaxColumnSpread:0.00} m: ColumnRadius + JitterAmplitude + √2 × SwayAmplitude + {ColumnMargin} m); " +
                  $"the column's mean, its prescribed sway taken out light by light, was on average {lag:0.000} m behind along the motion (bound below {MaxFollowLag:0.000} m: half of {MovingTargetSpeed} m/s / ColumnFollowRate {NavigationGuide.ColumnFollowRate}/s); the pin was put back and the column given {ColumnResettleSeconds} s to settle";
            if (observations > 0 && widest <= MaxColumnSpread && lag < MaxFollowLag)
                run.Pass("guide_column_follows_moving_target", detail);
            else
                run.Fail("guide_column_follows_moving_target", detail);
        }

        // The column stands on the surface over a target buried under the ground or sunk under
        // the water, and on the target itself when it is above the ground. Each case is watched
        // after the column has settled; the pin is put back.
        private static IEnumerator CheckColumnRaised(FeatureRun run, Player player, Minimap.PinData pin, TerrainGrid grid)
        {
            var farPos = pin.m_pos;
            var playerPos = player.transform.position;
            var water = ZoneSystem.instance.m_waterLevel;
            var clearance = MappingConfig.NavigationArrivalDistance + PinClearance;

            // Land: where the pin is, or the loaded point nearest to it whose terrain is above the
            // water level. Water: the loaded point nearest to you whose terrain lies deep enough.
            Vector3 land;
            var hasLand = Terrain(farPos, out var farGround) && farGround > water;
            if (hasLand)
                land = new Vector3(farPos.x, farGround, farPos.z);
            else
                hasLand = grid.Nearest(farPos, x => x.y > water && Utils.DistanceXZ(x, playerPos) >= clearance, out land);
            var hasSea = grid.Nearest(playerPos,
                x => x.y <= water - MinWaterDepth && Utils.DistanceXZ(x, playerPos) >= clearance, out var sea);

            var cases = new List<RaisedCase>();
            if (hasLand)
            {
                cases.Add(new RaisedCase($"{BuriedDepth} m under the ground", new Vector3(land.x, land.y - BuriedDepth, land.z),
                    land.y, "the ground"));
                cases.Add(new RaisedCase($"{RaisedHeight} m above the ground", new Vector3(land.x, land.y + RaisedHeight, land.z),
                    land.y + RaisedHeight, "the pin"));
            }
            if (hasSea)
                cases.Add(new RaisedCase($"{SunkDepth} m under the water surface", new Vector3(sea.x, water - SunkDepth, sea.z),
                    water, "the water surface"));

            try
            {
                foreach (var raised in cases)
                {
                    Map.MovePin(pin, raised.Pin);
                    var moved = Time.time;
                    while (run.IsCurrent && Time.time - moved < ColumnResettleSeconds) yield return null;
                    if (!run.IsCurrent) yield break;

                    var start = Time.time;
                    while (run.IsCurrent)
                    {
                        foreach (var light in ReadLights(NavigationGuide.Column))
                            raised.Sample(light.position.y - raised.Base);
                        if (Time.time - start >= ColumnMotionSeconds) break;
                        yield return null;
                    }
                    if (!run.IsCurrent) yield break;
                    raised.Distance = Utils.DistanceXZ(player.transform.position, raised.Pin);
                }
            }
            finally
            {
                Map.MovePin(pin, farPos);
            }

            var settle = Time.time;
            while (run.IsCurrent && Time.time - settle < ColumnResettleSeconds) yield return null;
            if (!run.IsCurrent) yield break;

            var parts = cases.Select(x => x.Describe()).ToList();
            if (!hasLand)
                parts.Add($"under and above the ground: SKIPPED, no loaded point within {SearchRadius} m and at least {clearance:0} m from you (the arrival distance + {PinClearance} m) has terrain above the water level");
            if (!hasSea)
                parts.Add($"under the water: SKIPPED, no loaded point within {SearchRadius} m and at least {clearance:0} m from you (the arrival distance + {PinClearance} m) has terrain at least {MinWaterDepth} m below the water level ({water:0.0} m); the raised column over water is judged by eye");
            var detail =
                $"{string.Join("; ", parts)}; bounds {MinColumnHeight:0.00}–{MaxColumnHeight:0.00} m above the expected base (ColumnCentreHeight ± (ColumnHalfHeight + JitterAmplitude + {ColumnMargin} m)), each case watched for {ColumnMotionSeconds} s after {ColumnResettleSeconds} s to settle; the terrain from {grid.Count} points on a {SearchStep} m grid within {SearchRadius} m; the pin was put back";
            if (cases.Count == 0)
                run.Skip("guide_column_raised", detail);
            else if (cases.All(x => x.Holds))
                run.Pass("guide_column_raised", detail);
            else
                run.Fail("guide_column_raised", detail);
        }

        // The burst function the stream uses keeps a burst light, its place and the light itself
        // as it trails that place, above the terrain under it with your feet at a steep point of
        // the loaded terrain, though nobody stands there: the function is evaluated for many
        // lights and ages, and the terrain under each point is measured by the check itself.
        // When no loaded slope is steep enough, a mound is raised and the terrain looked at again.
        private static IEnumerator CheckBurstAboveSlope(FeatureRun run, Player player, TerrainGrid grid)
        {
            var raised = "";
            if (!grid.Steepest(out var feet, out var uphill, out var rise) || rise < MinSlopeRise)
            {
                var none =
                    $"no loaded terrain within {SearchRadius} m rises {MinSlopeRise} m within {SearchStep} m horizontally (steepest {(grid.Count > 0 ? rise.ToString("0.00") : "none")} m over {grid.Count} points on a {SearchStep} m grid)";
                // The new grid is centred on a heightmap vertex (they are 1 m apart on whole
                // coordinates), so the mound's top and the grid points around it are vertices.
                var playerPos = player.transform.position;
                var centre = new Vector3(Mathf.Round(playerPos.x), playerPos.y, Mathf.Round(playerPos.z));
                var mound = new SmokeSetup.SetupResult();
                var steps = RaiseMound(run, centre, -Toward(playerPos), mound);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;
                if (!mound.Ok)
                {
                    run.Skip("guide_burst_above_slope",
                        $"{none}, and no mound could be raised: {mound.Reason}; the burst on a slope is judged by eye");
                    yield break;
                }
                grid = new TerrainGrid(centre);
                if (!grid.Steepest(out feet, out uphill, out rise) || rise < MinSlopeRise)
                {
                    run.Skip("guide_burst_above_slope",
                        $"{none}; {mound.Reason}, yet the steepest loaded terrain then rose {(grid.Count > 0 ? rise.ToString("0.00") : "none")} m within {SearchStep} m over {grid.Count} points; the burst on a slope is judged by eye");
                    yield break;
                }
                raised = $"{none}, so {mound.Reason}; ";
            }

            var followRate = FollowRateField == null
                ? float.NaN
                : Convert.ToSingle(FollowRateField.IsLiteral ? FollowRateField.GetRawConstantValue() : FollowRateField.GetValue(null));
            if (float.IsNaN(followRate))
            {
                run.Fail("guide_burst_above_slope", "NavigationGuide's FollowRate could not be read by reflection");
                yield break;
            }

            // A light's step toward its place in one frame at the frame rate below.
            var follow = 1f - Mathf.Exp(-followRate / SlopeFrameRate);
            var bodyPoint = feet + Vector3.up * NavigationGuide.BodyHeight;
            var ahead = feet + uphill * SearchStep;
            ahead.y = (Terrain(ahead, out var aheadGround) ? aheadGround : feet.y) + NavigationGuide.HoverHeight;

            var lowestGoal = float.MaxValue;
            var lowestPosition = float.MaxValue;
            var measured = 0;
            var unmeasured = 0;
            for (var i = 0; i < SlopeSeeds; i++)
            {
                uint seed;
                unchecked
                {
                    seed = (uint)(i + 1) * 2654435761u;
                }
                for (var k = 0; k < SlopeAges; k++)
                {
                    var age = (k + 0.5f) / SlopeAges * BurstAge;
                    // The step the stream makes for a bursting light each frame, from the body point.
                    var position = NavigationGuide.BurstStep(seed, age, bodyPoint, ahead, bodyPoint, follow,
                        out var goal);
                    if (Terrain(goal, out var goalGround) && Terrain(position, out var positionGround))
                    {
                        lowestGoal = Mathf.Min(lowestGoal, goal.y - goalGround);
                        lowestPosition = Mathf.Min(lowestPosition, position.y - positionGround);
                        measured++;
                    }
                    else
                        unmeasured++;
                }
            }

            var degrees = Mathf.Atan2(rise, SearchStep) * Mathf.Rad2Deg;
            var detail = raised +
                $"steepest loaded terrain within {SearchRadius} m: rising {rise:0.00} m over {SearchStep} m (about {degrees:0} degrees; bound at least {MinSlopeRise} m) at {Utils.DistanceXZ(feet, Player.m_localPlayer ? Player.m_localPlayer.transform.position : feet):0} m from you, out of {grid.Count} points on a {SearchStep} m grid; " +
                $"with the feet there, the body point {NavigationGuide.BodyHeight} m above them and the stream's place {NavigationGuide.HoverHeight} m (HoverHeight) over the terrain {SearchStep} m uphill, {SlopeSeeds} seeds × {SlopeAges} ages within {BurstAge:0.0} s (RadiateTime + GatherTime) gave {measured} points measured ({unmeasured} without terrain under them): " +
                $"the lowest place was {lowestGoal:0.00} m and the lowest light after the stream's own burst step, one frame's follow ({follow:0.000}: 1 - exp(-FollowRate {followRate}/s / {SlopeFrameRate} fps)) from the body point, {lowestPosition:0.00} m above the terrain under it (bound {MinBurstFloor:0.00} m: BurstFloor {NavigationGuide.BurstFloor} m - {FloorTolerance} m)";
            if (measured > 0 && lowestGoal >= MinBurstFloor && lowestPosition >= MinBurstFloor)
                run.Pass("guide_burst_above_slope", detail);
            else
                run.Fail("guide_burst_above_slope", detail);
        }

        // Raises the mound on loaded, dry ground with no piece near, on the grid around centre
        // MoundDistance away along ahead, or 45 or 90 degrees to either side, and waits until the
        // terrain collider shows its top. Reason is why it failed, or what was raised.
        private static IEnumerator RaiseMound(FeatureRun run, Vector3 centre, Vector3 ahead,
            SmokeSetup.SetupResult result)
        {
            if (DoOperationMethod == null)
            {
                result.Reason = "TerrainComp's DoOperation could not be found by reflection";
                yield break;
            }

            var water = ZoneSystem.instance.m_waterLevel;
            var clear = SmokeSetup.Clearance(MoundClearance, SmokeSetup.SiteKinds.Pieces);
            var refused = new List<string>();
            var top = Vector3.zero;
            var bearing = 0f;
            foreach (var candidate in MoundBearings)
            {
                var offset = Quaternion.Euler(0f, candidate, 0f) * ahead * MoundDistance;
                var site = centre + new Vector3(Mathf.Round(offset.x / SearchStep) * SearchStep, 0f,
                    Mathf.Round(offset.z / SearchStep) * SearchStep);
                var problem = !Terrain(site, out var ground) ? "no loaded terrain"
                    : ground <= water ? "under the water"
                    : clear(site);
                if (problem == null)
                {
                    top = new Vector3(site.x, ground, site.z);
                    bearing = candidate;
                    break;
                }
                refused.Add($"{candidate:0} degrees: {problem}");
            }
            if (refused.Count == MoundBearings.Length)
            {
                result.Reason =
                    $"no site {MoundDistance:0} m from you ahead or 45 or 90 degrees to a side ({string.Join("; ", refused)})";
                yield break;
            }

            var settings = new TerrainOp.Settings
            {
                m_raise = true,
                m_raiseRadius = MoundRadius,
                m_raiseDelta = MoundHeight,
                m_raisePower = 1f,
                m_square = false,
                m_paintCleared = false
            };
            var heightmaps = new List<Heightmap>();
            Heightmap.FindHeightmap(top, MoundRadius, heightmaps);
            foreach (var heightmap in heightmaps)
                DoOperationMethod.Invoke(heightmap.GetAndCreateTerrainCompiler(),
                    new object[] { top, Vector3.zero, settings });

            // Each heightmap rebuilds its collision mesh in the LateUpdate after the operation;
            // that the raycast sees it within MoundFrames is unverified.
            var rose = 0f;
            for (var frame = 0; frame < MoundFrames && rose < MoundHeight / 2f; frame++)
            {
                yield return null;
                if (!run.IsCurrent) yield break;
                rose = Terrain(top, out var now) ? now - top.y : 0f;
            }
            if (rose < MoundHeight / 2f)
            {
                result.Reason =
                    $"the terrain at the top of a mound raised {MoundDistance:0} m from you on {heightmaps.Count} heightmap(s) rose {rose:0.00} m of {MoundHeight} m within {MoundFrames} frames";
                yield break;
            }

            result.Ok = true;
            result.Changed = true;
            result.Position = top;
            result.Reason =
                $"a round mound {MoundHeight} m high and {MoundRadius} m in radius was raised {Utils.DistanceXZ(top, centre):0} m from you, {bearing:0} degrees from straight ahead (away from the target), with TerrainComp.DoOperation on {heightmaps.Count} heightmap(s) (its top rose {rose:0.00} m; the mound stays)";
        }

        // With the target nearer than the trail's length, the stream ends at the target and flows
        // into the column there instead of past it.
        private static IEnumerator CheckTrailEnd(FeatureRun run, Player player, Minimap.PinData pin)
        {
            var farPos = pin.m_pos;
            var overshoot = float.MinValue;
            var targetDistance = 0f;
            var samples = 0;
            var reached = new HashSet<uint>();
            try
            {
                var playerPos = player.transform.position;
                Map.MovePin(pin, GroundPoint(playerPos + Toward(playerPos) * NearTargetDistance, playerPos.y));
                var moved = Time.time;
                while (run.IsCurrent && Time.time - moved < TrailSettleSeconds) yield return null;
                if (!run.IsCurrent) yield break;

                var start = Time.time;
                while (run.IsCurrent && Time.time - start < TrailWatchSeconds)
                {
                    playerPos = player.transform.position;
                    var target = Navigation.TargetPosition;
                    var toward = Toward(playerPos);
                    targetDistance = Utils.DistanceXZ(playerPos, target);
                    foreach (var light in ReadLights(NavigationGuide.Stream))
                    {
                        overshoot = Mathf.Max(overshoot,
                            Vector3.Dot(Flat(light.position - playerPos), toward) - targetDistance);
                        if (Utils.DistanceXZ(light.position, target) <= NearCentreDistance)
                            reached.Add(light.randomSeed);
                        samples++;
                    }
                    yield return null;
                }
                if (!run.IsCurrent) yield break;
            }
            finally
            {
                Map.MovePin(pin, farPos);
            }

            var detail = samples == 0
                ? $"no stream light over {TrailWatchSeconds} s with the target {targetDistance:0.0} m away"
                : $"target {targetDistance:0.0} m away, watched for {TrailWatchSeconds} s after {TrailSettleSeconds} s to settle: the farthest stream light was {overshoot:0.00} m past the target along the line (bound {MaxOvershoot:0.00} m: ColumnRadius + 0.5 m); {reached.Count} lights came within {NearCentreDistance} m horizontally of the column's centre";
            if (samples > 0 && overshoot <= MaxOvershoot && reached.Count > 0)
                run.Pass("guide_trail_ends_at_target", detail);
            else
                run.Fail("guide_trail_ends_at_target", detail);
        }

        // Hidden while the large map is open; once it closes, the column is refilled and the stream
        // flows from you again along the line, without a burst.
        private static IEnumerator CheckLargeMap(FeatureRun run, Player player, Minimap map,
            Minimap.MapMode originalMode)
        {
            if (Game.m_noMap)
            {
                run.Skip("guide_hidden_on_large_map", "the world has no map, so the large map cannot open");
                yield break;
            }

            var hidden = false;
            var hiddenDrawing = "";
            var modeSeen = map.m_mode;
            var shownAgain = false;
            var shownDrawing = "";
            var refilled = 0;
            var stale = 0;
            FlowWatch resumed = null;
            try
            {
                var before = new HashSet<uint>(ReadLights(NavigationGuide.Column).Select(x => x.randomSeed));
                map.SetMapMode(Minimap.MapMode.Large);
                yield return null;
                yield return null;
                var root = NavigationGuide.Root;
                var drawing = Drawing.Of(root);
                hidden = map.m_mode == Minimap.MapMode.Large && root && !NavigationGuide.Shown && drawing.Hidden;
                hiddenDrawing = drawing.ToString();
                modeSeen = map.m_mode;
                map.SetMapMode(originalMode == Minimap.MapMode.Large ? Minimap.MapMode.Small : originalMode);
                var closeTime = Time.time;
                resumed = new FlowWatch(closeTime, closeTime + ResumeWindowSeconds);
                yield return null;
                drawing = Drawing.Of(NavigationGuide.Root);
                shownAgain = NavigationGuide.Shown && drawing.Drawn;
                shownDrawing = drawing.ToString();
                var column = ReadLights(NavigationGuide.Column);
                refilled = column.Length;
                stale = column.Count(x => before.Contains(x.randomSeed));

                while (run.IsCurrent)
                {
                    resumed.Sample(player);
                    if (Time.time - closeTime >= ResumeWatchSeconds) break;
                    yield return null;
                }
            }
            finally
            {
                if (map && map.m_mode != originalMode) map.SetMapMode(originalMode);
            }
            if (!run.IsCurrent) yield break;

            var detail =
                $"map mode {modeSeen}: swarm {(hidden ? "hidden" : "still shown or gone")} ({hiddenDrawing}); after closing: {(shownAgain ? "shown" : "not shown")} ({shownDrawing}), column {refilled} lights with {stale} left from before the map opened (bounds: at least {MinLights}, none left); " +
                (resumed == null
                    ? "the stream was not watched"
                    : $"over {ResumeWatchSeconds} s, {resumed.BurstLights} burst light(s) (start lifetime above {BurstLifetimeSplit:0.00} s; bound none), and the lights born in the first {ResumeWindowSeconds} s: {resumed.Describe()} (bound {MaxBehind} m behind)");
            if (hidden && shownAgain && refilled >= MinLights && stale == 0 && resumed != null &&
                resumed.BurstLights == 0 && resumed.Lights > 0 && resumed.MostBehind <= MaxBehind)
                run.Pass("guide_hidden_on_large_map", detail);
            else
                run.Fail("guide_hidden_on_large_map", detail);
        }

        // A map click on the target clears navigation while the large map hides the swarm: the
        // swarm is held, hidden, with its lights where they were, until the map closes, and then
        // scatters, its stream's lights flying off from the line they flowed along. A navigation
        // started on the map before it closes, or the option turned off, drops the held swarm
        // instead. After each case the section waits for the scatter to go and starts navigation
        // on the pin at 60 m again.
        private static IEnumerator CheckMapClear(FeatureRun run, Minimap map, Minimap.PinData pin, Vector3 farPos,
            SectionState state)
        {
            const string name = "guide_scatters_after_map_clear";
            if (Game.m_noMap)
            {
                run.Skip(name, "the world has no map, so the large map cannot open");
                yield break;
            }
            if (MappingConfig.NavigationStartKey.MainKey == KeyCode.None)
            {
                run.Skip(name, "navigation_start_key is unbound and could not be bound for the section, so a map click cannot clear navigation");
                yield break;
            }
            if (state.Leftover != null)
            {
                run.Skip(name, "a guide object left from an earlier scatter would be counted: " + state.Leftover);
                yield break;
            }

            // Navigation cleared by the click; the held swarm scatters once the map closes.
            var ready = new ReadySwarm();
            var steps = AwaitReady(run, ready);
            while (steps.MoveNext()) yield return steps.Current;
            if (!run.IsCurrent) yield break;
            if (!ready.Ready)
            {
                run.Skip(name, "the swarm was not ready for the map click: " + ready.Describe());
                yield break;
            }
            var held = new HeldClick();
            steps = ClickPinAndHold(run, map, pin, held, null);
            while (steps.MoveNext()) yield return steps.Current;
            if (!run.IsCurrent) yield break;
            var closed = Time.time;
            var releasedIn = 0;
            for (var frame = 1; frame <= ReleaseFrames; frame++)
            {
                yield return null;
                if (!run.IsCurrent) yield break;
                if (!held.Held || !held.Held.GetComponent<NavigationGuideScatter>() || !Drawing.Of(held.Held).Drawn)
                    continue;
                releasedIn = frame;
                break;
            }
            var scatter = new ScatterOutcome();
            if (releasedIn > 0)
            {
                steps = WatchOutwardScatter(run, held.Held, held.Stream, held.Column, held.Line, held.Axis, state,
                    scatter);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;
            }
            var main =
                $"before the map opened: {ready.Describe()}; {held.Describe()}; after closing: " +
                (releasedIn > 0
                    ? $"shown and scattering in {releasedIn} frame(s) (bound {ReleaseFrames}): {scatter.Detail}"
                    : $"not shown and scattering within {ReleaseFrames} frames ({Drawing.Of(held.Held)}, {(held.Held && held.Held.GetComponent<NavigationGuideScatter>() ? "scattering" : "no scatter driver")}), {Time.time - closed:0.00} s after closing");
            var mainPassed = held.HeldSwarm && held.Kept && releasedIn > 0 && scatter.Passed;
            steps = WaitForOthersRemoved(run, state);
            while (steps.MoveNext()) yield return steps.Current;
            if (!run.IsCurrent) yield break;
            steps = Restart(run, pin, farPos, true);
            while (steps.MoveNext()) yield return steps.Current;
            if (!run.IsCurrent) yield break;

            // A navigation started on the map before it closes drops the held swarm.
            ready = new ReadySwarm();
            steps = AwaitReady(run, ready);
            while (steps.MoveNext()) yield return steps.Current;
            if (!run.IsCurrent) yield break;
            if (!ready.Ready)
            {
                run.Skip(name, $"{main}; the swarm was not ready for the map click before a navigation started on the map: {ready.Describe()}");
                yield break;
            }
            var restarted = new HeldClick();
            steps = ClickPinAndHold(run, map, pin, restarted, x => StartOnOpenMap(map, pin, x));
            while (steps.MoveNext()) yield return steps.Current;
            if (!run.IsCurrent) yield break;
            var restartWatch = new AfterCloseWatch();
            steps = restartWatch.Watch(run);
            while (steps.MoveNext()) yield return steps.Current;
            if (!run.IsCurrent) yield break;
            var objects = FindGuideObjects();
            var onlyNew = objects.Count == 1 && NavigationGuide.Root && objects[0] == NavigationGuide.Root.transform &&
                          !restarted.Held;
            var started =
                $"a navigation started on the map before closing: held swarm {(restarted.HeldSwarm ? "made" : "not made")} by the click, the second click {(restarted.SecondHandled ? "handled" : "not handled")}, navigation {(restarted.Restarted ? "on the test pin again" : "not started")}, held swarm {(restarted.DroppedBeforeClose ? "dropped" : "kept")} before closing; " +
                $"over {AbsentWatchSeconds} s after closing {restartWatch.Describe()}; then {objects.Count} guide object(s){(onlyNew ? ", the new swarm" : "")}";
            var startedPassed = restarted.HeldSwarm && restarted.SecondHandled && restarted.Restarted &&
                                restarted.DroppedBeforeClose && !restartWatch.ScatterSeen && restartWatch.BurstSeen &&
                                restartWatch.NewSwarmSeen && onlyNew;
            steps = WaitForOthersRemoved(run, state);
            while (steps.MoveNext()) yield return steps.Current;
            if (!run.IsCurrent) yield break;
            steps = Restart(run, pin, farPos, true);
            while (steps.MoveNext()) yield return steps.Current;
            if (!run.IsCurrent) yield break;

            // The option turned off while a swarm is held drops it; nothing scatters on closing.
            ready = new ReadySwarm();
            steps = AwaitReady(run, ready);
            while (steps.MoveNext()) yield return steps.Current;
            if (!run.IsCurrent) yield break;
            if (!ready.Ready)
            {
                run.Skip(name, $"{main}; {started}; the swarm was not ready for the map click before the option was turned off: {ready.Describe()}");
                yield break;
            }
            var dropped = new HeldClick();
            var offWatch = new AfterCloseWatch();
            try
            {
                steps = ClickPinAndHold(run, map, pin, dropped, x => TurnGuideOffWhileHeld(run, state, x));
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;
                steps = offWatch.Watch(run);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;
            }
            finally
            {
                if (dropped.TurnedOff)
                    dropped.TurnedOn = SmokeSession.WriteSetting(run.RunId, state.GuideSetting, true, out dropped.Refusal);
            }
            var optionOff = dropped.TurnedOff
                ? $"with enable_navigation_guide turned off while held: held swarm {(dropped.HeldSwarm ? "made" : "not made")} by the click, {(dropped.DroppedBeforeClose ? "no guide object left" : "a guide object left")} {OptionOffFrames} frames later with the map open; over {AbsentWatchSeconds} s after closing {offWatch.Describe()}; the option {(dropped.TurnedOn ? "turned back on" : "not turned back on: " + dropped.Refusal)}"
                : $"the option-off case could not run: enable_navigation_guide could not be turned off: {dropped.Refusal}";
            var optionOffPassed = dropped.HeldSwarm && dropped.TurnedOff && dropped.DroppedBeforeClose &&
                                  !offWatch.ScatterSeen && !offWatch.ObjectSeen && dropped.TurnedOn;
            steps = WaitForOthersRemoved(run, state);
            while (steps.MoveNext()) yield return steps.Current;
            if (!run.IsCurrent) yield break;
            steps = Restart(run, pin, farPos, true);
            while (steps.MoveNext()) yield return steps.Current;
            if (!run.IsCurrent) yield break;

            var detail = $"{main}; {started}; {optionOff}";
            if (mainPassed && startedPassed && optionOffPassed)
                run.Pass(name, detail);
            else if (mainPassed && startedPassed && !dropped.TurnedOff)
                run.Skip(name, detail);
            else
                run.Fail(name, detail);
        }

        // Clearing navigation by hand with the swarm shown, through the routine the map click and
        // the looked-at-object key both use, shows the cleared message and scatters the swarm at
        // once, its stream's lights flying off from the line they flowed along and its column's from
        // the column's axis. The section then waits for the scatter to go and starts navigation on
        // the pin at 60 m again.
        private static IEnumerator CheckManualClear(FeatureRun run, Minimap.PinData pin, Vector3 farPos,
            SectionState state)
        {
            const string name = "guide_scatters_on_manual_clear";
            if (state.Leftover != null)
            {
                run.Skip(name, "a guide object left from an earlier scatter would be counted: " + state.Leftover);
                yield break;
            }

            var ready = new ReadySwarm();
            var steps = AwaitReady(run, ready);
            while (steps.MoveNext()) yield return steps.Current;
            if (!run.IsCurrent) yield break;
            if (!ready.Ready)
            {
                run.Skip(name, "the swarm was not ready for the clear: " + ready.Describe());
                yield break;
            }

            var root = NavigationGuide.Root;
            var stream = NavigationGuide.Stream;
            var column = NavigationGuide.Column;
            // Taken by the check itself in the frame of the clear, whatever the scatter is handed.
            var line = ExpectedStreamLine();
            var axis = Navigation.TargetPosition;
            var messages = ClearByHand(out var messageObserved);
            var expected = global::Automatics.Automatics.L10N.Localize("@message_automatic_mapping_navigation_cleared");
            var messageShown = messages.Contains(expected);
            var cleared = Navigation.TargetPin == null;
            var objects = FindGuideObjects();
            var single = objects.Count == 1 && root && objects[0] == root.transform && !NavigationGuide.Root;
            var drawing = Drawing.Of(root);
            var scattering = root && root.GetComponent<NavigationGuideScatter>();

            var scatter = new ScatterOutcome();
            steps = WatchOutwardScatter(run, root, stream, column, line, axis, state, scatter);
            while (steps.MoveNext()) yield return steps.Current;
            if (!run.IsCurrent) yield break;

            var detail =
                $"navigation {(cleared ? "cleared" : "still on")}, message {(messageShown ? $"'{expected}' shown" : messageObserved ? $"'{expected}' not among [{string.Join(", ", messages)}]" : "not observed (the player's message method was not found)")}; " +
                $"before the clear: {ready.Describe()}; {objects.Count} guide object(s) at the clear{(single ? ", the swarm that was shown" : "")}, {(drawing.Drawn ? "shown" : "not shown")} ({drawing}), {(scattering ? "scattering" : "no scatter driver")}; {scatter.Detail}";
            if (cleared && messageShown && single && drawing.Drawn && scattering && scatter.Passed)
                run.Pass(name, detail);
            else
                run.Fail(name, detail);

            steps = WaitForOthersRemoved(run, state);
            while (steps.MoveNext()) yield return steps.Current;
            if (!run.IsCurrent) yield break;
            steps = Restart(run, pin, farPos, true);
            while (steps.MoveNext()) yield return steps.Current;
        }

        private static void CheckDistanceFormat(FeatureRun run, Player player, Minimap map, Minimap.PinData pin)
        {
            var position = pin.m_pos;
            var mode = map.m_mode;
            try
            {
                var camera = GameCamera.instance.transform;
                map.SetMapMode(Minimap.MapMode.Small);
                Map.MovePin(pin, camera.position + camera.forward * 20f - Vector3.up * 2f);
                // Started again, so a camera looking steeply down cannot bring the pin within the
                // arrival distance of an armed navigation and end it before the label is read.
                SetTargetMethod.Invoke(null, new object[] { pin });
                Navigation.Update(player);
                var label = Navigation.Label;
                var textType = AccessTools.Property(typeof(Minimap.PinNameData), "PinNameText").PropertyType;
                var text = label ? (string)AccessTools.Property(textType, "text").GetValue(
                    label.transform.Find("Name").GetComponent(textType)) : "";
                var expected = pin.m_name + " [" + Mathf.RoundToInt(Utils.DistanceXZ(player.transform.position, pin.m_pos)) + "m]";
                if (label && label.activeInHierarchy && text == expected) run.Pass("guide_distance_format");
                else run.Fail("guide_distance_format", "navigation label did not show bracketed horizontal distance: " + text);
            }
            finally
            {
                Map.MovePin(pin, position);
                map.SetMapMode(mode);
            }
        }

        // Navigation ends just inside the arrival distance, measured horizontally, and the shown
        // swarm scatters from there.
        private static IEnumerator CheckArrival(FeatureRun run, Player player, Minimap.PinData pin,
            SectionState state)
        {
            var probe = new ArrivalProbe();
            var steps = ProbeArrival(run, player, pin, probe);
            while (steps.MoveNext()) yield return steps.Current;
            if (!run.IsCurrent) yield break;

            // Read in the frame the navigation ended, before the scatter moves a light.
            var labelGone = !Navigation.Label;
            var liveGone = !NavigationGuide.Root;
            var pinKept = Map.ContainsPin(pin);
            var detail =
                $"{probe.Describe()}; label {(labelGone ? "gone" : "left")}, live swarm {(liveGone ? "gone" : "left")}, test pin {(pinKept ? "still on the map" : "gone")}";
            if (probe.KeptOutside && probe.ShownOutside && probe.Frames > 0 && labelGone && liveGone && pinKept)
                run.Pass("navigation_clears_on_arrival", detail);
            else
                run.Fail("navigation_clears_on_arrival", detail);

            if (probe.Frames == 0)
            {
                run.Skip("guide_scatters_on_arrival", "navigation did not end on arrival, so no swarm scattered");
                yield break;
            }

            steps = CheckScatter(run, probe, state);
            while (steps.MoveNext()) yield return steps.Current;
        }

        // The swarm let go on arrival: no light is added and none is cut short, each system's
        // visible lights fly outward from the column's axis and fade, the point lights dim, and the
        // object removes itself.
        private static IEnumerator CheckScatter(FeatureRun run, ArrivalProbe probe, SectionState state)
        {
            var clearTime = Time.time;
            var objects = FindGuideObjects();
            var single = objects.Count == 1 && probe.Root && objects[0] == probe.Root.transform;
            var stream = new ScatterCohort("stream", probe.Stream, probe.Target);
            var column = new ScatterCohort("column", probe.Column, probe.Target);
            var lights = probe.Root ? probe.Root.GetComponentsInChildren<Light>(true) : new Light[0];
            var firstIntensity = lights.Select(x => x.intensity).ToArray();

            while (run.IsCurrent)
            {
                yield return null;
                stream.Sample();
                column.Sample();
                if (Time.time - clearTime >= ScatterWatchSeconds) break;
            }
            if (!run.IsCurrent) yield break;

            var elapsed = Time.time - clearTime;
            var bound = ScatterBound(elapsed, out var share, out var travel);
            var streamScattered = stream.Evaluate(bound, out var streamDetail);
            var columnScattered = column.Evaluate(bound, out var columnDetail);
            var dimmed = lights.Length == 2 &&
                         Enumerable.Range(0, lights.Length).All(i => lights[i] && lights[i].intensity < firstIntensity[i]);
            var intensities = string.Join(", ", Enumerable.Range(0, lights.Length).Select(i =>
                $"{firstIntensity[i]:0.00} → {(lights[i] ? lights[i].intensity.ToString("0.00") : "gone")}"));

            var steps = WaitForRemoval(run, clearTime, state);
            while (steps.MoveNext()) yield return steps.Current;
            if (!run.IsCurrent) yield break;
            var removed = CountGuideObjects() == 0;

            var detail =
                $"{objects.Count} guide object(s) at the clear{(single ? ", the swarm that was shown" : "")}; by {elapsed:0.00} s: {streamDetail}; {columnDetail}; " +
                $"outward bound {bound:0.000} m, half of {share * travel:0.000} m (MinScatterSpeed {NavigationGuide.MinScatterSpeed} m/s from ScatterDelay {NavigationGuide.ScatterDelay} s, slowing by ScatterDecay {NavigationGuide.ScatterDecay}/s, outward share (1 - ScatterSpread) / (1 + ScatterSpread + ScatterLift) = {share:0.00}); " +
                $"point light intensity {(lights.Length == 0 ? "no lights" : intensities)}; " +
                (removed
                    ? $"no guide object left {Time.time - clearTime:0.00} s after the clear (bound {NavigationGuide.ScatterTime + RemovalMargin:0.0} s: ScatterTime + {RemovalMargin} s)"
                    : state.Leftover);
            if (single && streamScattered && columnScattered && dimmed && removed)
                run.Pass("guide_scatters_on_arrival", detail);
            else
                run.Fail("guide_scatters_on_arrival", detail);
        }

        // The least a scattering light flies out by the given time since the scatter began, leaving
        // last at the lowest speed and straight out only by the smallest share of its direction;
        // the zigzag around its path averages out over a system's lights. The bound is half of it.
        private static float ScatterBound(float elapsed, out float share, out float travel)
        {
            share = (1f - NavigationGuide.ScatterSpread) /
                    (1f + NavigationGuide.ScatterSpread + NavigationGuide.ScatterLift);
            var flying = Mathf.Max(0f, elapsed - NavigationGuide.ScatterDelay);
            travel = NavigationGuide.MinScatterSpeed / NavigationGuide.ScatterDecay *
                     (1f - Mathf.Exp(-NavigationGuide.ScatterDecay * flying));
            return 0.5f * share * travel;
        }

        // A swarm let go by hand, watched from its first look after the scatter began: per system,
        // the lights with alpha above 0.1 then, none added and none lost, at least 75 % of them
        // moved outward - the stream's from their nearest point on the stream's line, the column's
        // from the column's axis, both as the check took them itself before the clear and each
        // light's outward direction taken at that first look - a mean outward move of at least the
        // arrival scatter's bound, the mean alpha below half, and no guide object left within
        // ScatterTime + 0.5 s. The line and the axis the scatter was handed lie at the check's own:
        // the line's two ends within SpineTolerance of the ends of the check's line, the axis
        // within it of the check's.
        private static IEnumerator WatchOutwardScatter(FeatureRun run, GameObject root, ParticleSystem streamSystem,
            ParticleSystem columnSystem, Vector3[] line, Vector3 axis, SectionState state, ScatterOutcome outcome)
        {
            var since = Time.time;
            var driver = root ? root.GetComponent<NavigationGuideScatter>() : null;
            if (!driver)
            {
                outcome.Detail = "no scatter driver on the swarm, so nothing was watched";
                yield break;
            }

            var spine = driver.StreamSpine;
            var startGap = spine != null && spine.Length > 0 ? Utils.DistanceXZ(spine[0], line[0]) : float.MaxValue;
            var endGap = spine != null && spine.Length > 0
                ? Utils.DistanceXZ(spine[spine.Length - 1], line[line.Length - 1])
                : float.MaxValue;
            var axisGap = Utils.DistanceXZ(driver.ColumnCentre, axis);
            var handed = startGap <= SpineTolerance && endGap <= SpineTolerance && axisGap <= SpineTolerance;
            var lineLength = Utils.DistanceXZ(line[0], line[line.Length - 1]);

            var stream = new OutwardCohort("stream", streamSystem, line,
                $"its nearest point on the stream's line ({lineLength:0.0} m from you toward the target)");
            var column = new OutwardCohort("column", columnSystem, new[] { axis }, "the column's axis");
            while (run.IsCurrent)
            {
                yield return null;
                stream.Sample();
                column.Sample();
                if (Time.time - since >= ScatterWatchSeconds) break;
            }
            if (!run.IsCurrent) yield break;

            var elapsed = Time.time - since;
            var bound = ScatterBound(elapsed, out var share, out var travel);
            var streamScattered = stream.Evaluate(bound, out var streamDetail);
            var columnScattered = column.Evaluate(bound, out var columnDetail);

            var steps = WaitForRemoval(run, since, state);
            while (steps.MoveNext()) yield return steps.Current;
            if (!run.IsCurrent) yield break;
            var removed = CountGuideObjects() == 0;

            outcome.Passed = handed && streamScattered && columnScattered && removed;
            outcome.Detail =
                $"the scatter was handed a stream line of {(spine == null ? 0 : spine.Length)} point(s) whose ends lie {startGap:0.00} m and {endGap:0.00} m from the ends of the line taken by the check, and an axis {axisGap:0.00} m from the target (bound {SpineTolerance} m each); " +
                $"by {elapsed:0.00} s: {streamDetail}; {columnDetail}; " +
                $"outward bound {bound:0.000} m, half of {share * travel:0.000} m (MinScatterSpeed {NavigationGuide.MinScatterSpeed} m/s from ScatterDelay {NavigationGuide.ScatterDelay} s, slowing by ScatterDecay {NavigationGuide.ScatterDecay}/s, outward share (1 - ScatterSpread) / (1 + ScatterSpread + ScatterLift) = {share:0.00}); outward share bound {MinOutwardShare:P0}; " +
                (removed
                    ? $"no guide object left {Time.time - since:0.00} s after the scatter began (bound {NavigationGuide.ScatterTime + RemovalMargin:0.0} s: ScatterTime + {RemovalMargin} s)"
                    : state.Leftover);
        }

        // A navigation started on a target already within the arrival distance stays on until you
        // have been farther away during it, and starts a swarm of its own.
        private static IEnumerator CheckStartedInside(FeatureRun run, Player player, Minimap.PinData pin,
            SectionState state)
        {
            var playerPos = player.transform.position;
            var forward = Forward(player);
            Vector3 Point(float distance) => GroundPoint(playerPos + forward * distance, playerPos.y);

            var armed = ReferenceEquals(Navigation.TargetPin, pin);
            var oldRoot = NavigationGuide.Root;
            var hadSwarm = (bool)oldRoot;
            Map.MovePin(pin, Point(StartInsideDistance));
            SetTargetMethod.Invoke(null, new object[] { pin });
            yield return null;
            if (!run.IsCurrent) yield break;
            var newRoot = NavigationGuide.Root;
            var freshSwarm = hadSwarm && newRoot && !ReferenceEquals(newRoot, oldRoot);

            var start = Time.time;
            while (run.IsCurrent && Time.time - start < KeepSeconds) yield return null;
            if (!run.IsCurrent) yield break;
            var kept = ReferenceEquals(Navigation.TargetPin, pin);
            var insideDistance = Utils.DistanceXZ(player.transform.position, pin.m_pos);

            Map.MovePin(pin, Point(RearmDistance));
            for (var i = 0; i < RearmFrames; i++) yield return null;
            if (!run.IsCurrent) yield break;
            var keptOutside = ReferenceEquals(Navigation.TargetPin, pin);
            var outsideDistance = Utils.DistanceXZ(player.transform.position, pin.m_pos);

            Map.MovePin(pin, Point(StartInsideDistance));
            var frames = 0;
            for (var frame = 1; frame <= ArrivalFrames; frame++)
            {
                yield return null;
                if (!run.IsCurrent) yield break;
                if (Navigation.TargetPin != null) continue;
                frames = frame;
                break;
            }
            var clearTime = Time.time;

            var detail =
                $"started {insideDistance:0.0} m away (navigation_arrival_distance {MappingConfig.NavigationArrivalDistance} m) from a navigation {(armed ? "on" : "not on")} the test pin: {(freshSwarm ? "a new swarm" : newRoot ? "the same swarm" : "no swarm")} a frame later, navigation {(kept ? "kept" : "ended")} after {KeepSeconds} s; " +
                $"{outsideDistance:0.0} m away for {RearmFrames} frames: navigation {(keptOutside ? "kept" : "ended")}; back at {StartInsideDistance} m: {(frames > 0 ? $"ended in {frames} frame(s)" : $"still on after {ArrivalFrames} frames")}";
            if (armed && freshSwarm && kept && keptOutside && frames > 0)
                run.Pass("navigation_kept_when_started_inside", detail);
            else
                run.Fail("navigation_kept_when_started_inside", detail);

            if (frames == 0) yield break;
            var steps = WaitForRemoval(run, clearTime, state);
            while (steps.MoveNext()) yield return steps.Current;
        }

        // navigation_arrival_distance moves where navigation ends: set to 10 m, navigation stays on
        // just outside it and ends just inside it; set to 0, it never ends on arrival, even with the
        // pin half a metre away. The setting goes back to the section's distance afterwards, and
        // navigation is started on the pin at 60 m again.
        private static IEnumerator CheckArrivalSetting(FeatureRun run, Player player, Minimap.PinData pin,
            Vector3 farPos, SectionState state)
        {
            const string name = "navigation_arrival_distance_setting";
            var wide = new ArrivalProbe();
            var off = new ArrivalProbe();
            var offKept = false;
            var offFor = 0f;
            var wideRemoved = false;
            string refusal = null;
            var back = false;
            string backRefusal = null;
            try
            {
                if (!SmokeSession.WriteSetting(run.RunId, state.ArrivalSetting, WideArrivalDistance, out refusal))
                {
                    run.Skip(name, $"navigation_arrival_distance could not be set to {WideArrivalDistance} m: {refusal}");
                    yield break;
                }
                var steps = ProbeArrival(run, player, pin, wide, WideArrivalDistance + ArrivalMargin,
                    WideArrivalDistance - ArrivalMargin);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;
                if (wide.Frames > 0)
                {
                    steps = WaitForRemoval(run, Time.time, state);
                    while (steps.MoveNext()) yield return steps.Current;
                    if (!run.IsCurrent) yield break;
                    wideRemoved = CountGuideObjects() == 0;
                }

                steps = Restart(run, pin, farPos, true);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;
                if (!SmokeSession.WriteSetting(run.RunId, state.ArrivalSetting, OffArrivalDistance, out refusal))
                {
                    run.Skip(name, $"navigation_arrival_distance could not be set to {OffArrivalDistance} m: {refusal}");
                    yield break;
                }
                steps = ProbeArrival(run, player, pin, off, WideArrivalDistance + ArrivalMargin,
                    OffArrivalDistance + ArrivalMargin);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;
                offKept = off.Frames == 0;
                while (run.IsCurrent && offKept && Time.time - off.InsideAt < KeepSeconds)
                {
                    yield return null;
                    offKept = ReferenceEquals(Navigation.TargetPin, pin);
                }
                if (!run.IsCurrent) yield break;
                offFor = Time.time - off.InsideAt;
            }
            finally
            {
                back = SmokeSession.WriteSetting(run.RunId, state.ArrivalSetting, state.SectionArrivalDistance,
                    out backRefusal);
            }

            if (!offKept)
            {
                var steps = WaitForRemoval(run, Time.time, state);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;
            }
            var restart = Restart(run, pin, farPos, true);
            while (restart.MoveNext()) yield return restart.Current;
            if (!run.IsCurrent) yield break;

            var detail =
                $"at {WideArrivalDistance} m: {wide.Describe()}{(wide.Frames > 0 ? $", the scatter {(wideRemoved ? "gone" : "still there")} after the wait" : "")}; " +
                $"at {OffArrivalDistance} m: {off.OutsideDistance:0.0} m away horizontally: navigation {(off.KeptOutside ? "kept" : "ended")}; {off.InsideDistance:0.0} m away horizontally: navigation {(offKept ? "kept" : "ended")} for {offFor:0.00} s (bound {KeepSeconds} s); " +
                $"navigation_arrival_distance {(back ? $"back at {state.SectionArrivalDistance} m" : "not put back: " + backRefusal)}";
            if (wide.KeptOutside && wide.Frames > 0 && off.KeptOutside && offKept && offFor >= KeepSeconds && back)
                run.Pass(name, detail);
            else
                run.Fail(name, detail);
        }

        // Turning the option off destroys the swarm, navigation still ends on arrival without it,
        // and turning the option back on brings the swarm back to a new navigation.
        private static IEnumerator CheckOption(FeatureRun run, Player player, Minimap.PinData pin, Vector3 farPos,
            SectionState state)
        {
            var entry = MappingConfig.EnableNavigationGuideEntry;
            if (entry == null)
            {
                run.Fail("guide_option_off_destroys", "the enable_navigation_guide entry is not bound");
                yield break;
            }

            try
            {
                if (!TakeGuideSetting(run, state, out var refusal) ||
                    !SmokeSession.WriteSetting(run.RunId, state.GuideSetting, false, out refusal))
                {
                    run.Skip("guide_option_off_destroys", $"{entry.Definition.Key} could not be turned off: {refusal}");
                    run.Skip("navigation_clears_with_swarm_off", $"{entry.Definition.Key} could not be turned off: {refusal}");
                    yield break;
                }

                yield return null;
                yield return null;
                var left = CountGuideObjects();
                if (state.Leftover != null)
                    run.Skip("guide_option_off_destroys", "a guide object left from an arrival would be counted: " + state.Leftover);
                else if (!NavigationGuide.Root && left == 0)
                    run.Pass("guide_option_off_destroys");
                else
                    run.Fail("guide_option_off_destroys",
                        $"{left} guide object(s) are left with enable_navigation_guide off");

                var probe = new ArrivalProbe();
                var steps = ProbeArrival(run, player, pin, probe);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;
                var detail = probe.Describe() + ", with enable_navigation_guide off";
                if (probe.KeptOutside && probe.Frames > 0)
                    run.Pass("navigation_clears_with_swarm_off", detail);
                else
                    run.Fail("navigation_clears_with_swarm_off", detail);

                // A navigation for the swarm to come back to once the option is on again.
                steps = Restart(run, pin, farPos, false);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;
            }
            finally
            {
                // On again for the rest of the section, as it was set up; the section's settings
                // are put back at its end.
                if (state.GuideSetting != null) SmokeSession.WriteSetting(run.RunId, state.GuideSetting, true, out _);
            }

            yield return null;
            yield return null;
            if (MappingConfig.EnableNavigationGuide && NavigationGuide.Shown && Drawing.Of(NavigationGuide.Root).Drawn)
                run.Pass("guide_option_on_recreates");
            else if (!MappingConfig.EnableNavigationGuide)
                run.Skip("guide_option_on_recreates", "enable_navigation_guide was not turned back on");
            else
                run.Fail("guide_option_on_recreates", "the swarm did not come back with enable_navigation_guide on");
        }

        // Every setting the section took on, put back, and reported once.
        private static void ReportSettingsRestored(FeatureRun run, SectionState state)
        {
            state.SettingsReported = true;
            var results = SmokeSession.RestoreSettings(run.RunId);
            if (results.Count == 0) return;

            var detail = string.Join("; ", results.Select(x => x.Value));
            if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.Failed))
                run.Fail("guide_settings_restored", detail);
            else if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.LeftForUser ||
                                      x.Key == SmokeSession.ToggleOutcome.NotThisRun))
                run.Skip("guide_settings_restored", "restoration skipped: " + detail);
            else
                run.Pass("guide_settings_restored", detail);
        }

        // navigation_arrival_distance, taken on for the section and set to its default.
        private static bool TakeArrivalSetting(FeatureRun run, SectionState state, out string refusal)
        {
            var entry = MappingConfig.NavigationArrivalDistanceEntry;
            if (entry == null)
            {
                refusal = "the entry is not bound";
                return false;
            }
            if (!SmokeSession.BeginSetting(run.RunId, entry, out state.ArrivalSetting, out refusal)) return false;
            state.SectionArrivalDistance = (float)entry.DefaultValue;
            return SmokeSession.WriteSetting(run.RunId, state.ArrivalSetting, state.SectionArrivalDistance, out refusal);
        }

        // enable_navigation_guide, taken on once for every check of the section that turns it off.
        private static bool TakeGuideSetting(FeatureRun run, SectionState state, out string refusal)
        {
            refusal = null;
            if (state.GuideSetting != null) return true;
            var entry = MappingConfig.EnableNavigationGuideEntry;
            if (entry == null)
            {
                refusal = "the entry is not bound";
                return false;
            }
            return SmokeSession.BeginSetting(run.RunId, entry, out state.GuideSetting, out refusal);
        }

        // The pin is moved next to you rather than you to the pin, first just outside and then
        // just inside the arrival distance, and raised above your own height both times, so only a
        // horizontal measurement puts it inside. It ends in the frame navigation ends, so the
        // caller reads that frame.
        private static IEnumerator ProbeArrival(FeatureRun run, Player player, Minimap.PinData pin,
            ArrivalProbe probe)
        {
            var distance = MappingConfig.NavigationArrivalDistance;
            return ProbeArrival(run, player, pin, probe, distance + ArrivalMargin, distance - ArrivalMargin);
        }

        private static IEnumerator ProbeArrival(FeatureRun run, Player player, Minimap.PinData pin,
            ArrivalProbe probe, float outside, float inside)
        {
            probe.ArrivalDistance = MappingConfig.NavigationArrivalDistance;
            var playerPos = player.transform.position;
            var forward = Forward(player);
            Vector3 ProbePoint(float distance)
            {
                var point = playerPos + forward * distance;
                point.y = playerPos.y + ArrivalProbeRise;
                return point;
            }

            Map.MovePin(pin, ProbePoint(outside));
            for (var i = 0; i < OutsideFrames; i++) yield return null;
            if (!run.IsCurrent) yield break;
            probe.OutsideDistance = Utils.DistanceXZ(player.transform.position, pin.m_pos);
            probe.KeptOutside = ReferenceEquals(Navigation.TargetPin, pin);
            probe.Root = NavigationGuide.Root;
            probe.Stream = NavigationGuide.Stream;
            probe.Column = NavigationGuide.Column;
            probe.ShownOutside = NavigationGuide.Shown && Drawing.Of(probe.Root).Drawn;

            Map.MovePin(pin, ProbePoint(inside));
            probe.InsideAt = Time.time;
            probe.Target = pin.m_pos;
            probe.InsideDistance = Utils.DistanceXZ(player.transform.position, pin.m_pos);
            probe.Rise = pin.m_pos.y - player.transform.position.y;
            probe.Distance = Vector3.Distance(player.transform.position, pin.m_pos);
            for (var frame = 1; frame <= ArrivalFrames; frame++)
            {
                yield return null;
                if (!run.IsCurrent) yield break;
                if (Navigation.TargetPin != null) continue;
                probe.Frames = frame;
                yield break;
            }
        }

        // Navigation on the test pin at its place 60 m away again, for the checks after an
        // arrival; with the option on, it waits for the new swarm.
        private static IEnumerator Restart(FeatureRun run, Minimap.PinData pin, Vector3 farPos, bool waitForSwarm)
        {
            Map.MovePin(pin, farPos);
            SetTargetMethod.Invoke(null, new object[] { pin });
            yield return null;
            var deadline = Time.time + StartTimeoutSeconds;
            while (waitForSwarm && run.IsCurrent && Time.time < deadline && !NavigationGuide.Root)
                yield return null;
        }

        // Waits for a swarm scattering since an arrival or a clear by hand to remove itself. When it
        // is still there after the scatter and a margin, it is named for the later checks that
        // count guide objects, which then skip.
        private static IEnumerator WaitForRemoval(FeatureRun run, float since, SectionState state)
        {
            var deadline = since + NavigationGuide.ScatterTime + RemovalMargin;
            while (run.IsCurrent && Time.time < deadline && CountGuideObjects() > 0) yield return null;
            if (!run.IsCurrent || CountGuideObjects() == 0 || state.Leftover != null) yield break;

            var names = FindGuideObjects().Select(x =>
                $"'{x.name}' ({(x.gameObject.activeInHierarchy ? "active" : "inactive")}{(x.GetComponent<NavigationGuideScatter>() ? ", still scattering" : "")}; children {string.Join(", ", x.Cast<Transform>().Select(c => c.name))})");
            state.Leftover =
                $"{string.Join(", ", names)} still in the scene {Time.time - since:0.0} s after the swarm was let go (bound {NavigationGuide.ScatterTime + RemovalMargin:0.0} s: ScatterTime + {RemovalMargin} s)";
        }

        private static Vector3 _clickPosition;
        private static int _pressedCalls;
        private static bool _clickPositionObserved;

        // Opens the large map, clicks the pin with navigation_start_key held and puts the map back
        // as it was, all within the frame.
        internal static bool ClickPin(Minimap map, Minimap.PinData pin, out string detail)
        {
            var view = MapView.Capture(map);
            try
            {
                map.SetMapMode(Minimap.MapMode.Large);
                return ClickOpenMap(map, pin, out detail);
            }
            finally
            {
                view.Restore(map, view.Mode);
            }
        }

        // Opens the large map and waits until the swarm is hidden, clicks the pin with the map
        // left open, then watches the swarm held by that click for HoldSeconds with the map still
        // open. beforeClose, when given, runs next with the map still open. The map is closed and
        // its view put back however the steps end; a map you had open is closed to the small map,
        // so the held swarm can scatter, and the section puts your mode back at its end.
        private static IEnumerator ClickPinAndHold(FeatureRun run, Minimap map, Minimap.PinData pin, HeldClick click,
            Func<HeldClick, IEnumerator> beforeClose)
        {
            var view = MapView.Capture(map);
            var closeMode = view.Mode == Minimap.MapMode.Large ? Minimap.MapMode.Small : view.Mode;
            try
            {
                var root = NavigationGuide.Root;
                var stream = NavigationGuide.Stream;
                var column = NavigationGuide.Column;
                // Taken by the check itself in the swarm's last shown frame, whatever the scatter
                // is handed later.
                click.Line = ExpectedStreamLine();
                click.Axis = Navigation.TargetPosition;
                map.SetMapMode(Minimap.MapMode.Large);
                var opened = Time.time;
                do
                {
                    yield return null;
                    click.FramesToHide++;
                } while (run.IsCurrent && NavigationGuide.Shown && Time.time - opened < StartTimeoutSeconds);
                if (!run.IsCurrent) yield break;

                var drawing = Drawing.Of(root);
                click.HiddenAtClick = root && !NavigationGuide.Shown && drawing.Hidden;
                click.HiddenDrawing = drawing.ToString();
                click.Handled = ClickOpenMap(map, pin, out click.ClickDetail);
                click.ClickTime = Time.time;
                click.Held = NavigationGuide.HeldRoot;
                click.Stream = stream;
                click.Column = column;
                click.HeldSwarm = click.Held && click.Held == root && click.HiddenAtClick && click.Handled &&
                                  Navigation.TargetPin == null && !NavigationGuide.Root;
                click.StreamAtClick = Positions(ReadLights(stream));
                click.ColumnAtClick = Positions(ReadLights(column));

                while (run.IsCurrent)
                {
                    yield return null;
                    click.Sample();
                    if (Time.time - click.ClickTime >= HoldSeconds) break;
                }
                if (!run.IsCurrent) yield break;

                if (beforeClose == null) yield break;
                var steps = beforeClose(click);
                while (steps.MoveNext()) yield return steps.Current;
            }
            finally
            {
                view.Restore(map, closeMode);
            }
        }

        // With the map still open and a swarm held, a click on the pin starts navigation on it.
        private static IEnumerator StartOnOpenMap(Minimap map, Minimap.PinData pin, HeldClick click)
        {
            click.SecondHandled = ClickOpenMap(map, pin, out click.SecondDetail);
            click.Restarted = ReferenceEquals(Navigation.TargetPin, pin);
            yield return null;
            click.DroppedBeforeClose = !click.Held && !NavigationGuide.HeldRoot;
        }

        // With the map still open and a swarm held, enable_navigation_guide is turned off; the
        // caller turns it back on.
        private static IEnumerator TurnGuideOffWhileHeld(FeatureRun run, SectionState state, HeldClick click)
        {
            if (!TakeGuideSetting(run, state, out click.Refusal) ||
                !SmokeSession.WriteSetting(run.RunId, state.GuideSetting, false, out click.Refusal))
                yield break;

            click.TurnedOff = true;
            for (var i = 0; i < OptionOffFrames; i++) yield return null;
            click.DroppedBeforeClose = !click.Held && !NavigationGuide.HeldRoot && CountGuideObjects() == 0;
        }

        // The click itself, on the large map already open: centres the view on the pin and
        // presses it with navigation_start_key held, in this frame.
        private static bool ClickOpenMap(Minimap map, Minimap.PinData pin, out string detail)
        {
            var image = AccessTools.Field(typeof(Minimap), "m_mapImageLarge").GetValue(map);
            var harmony = new Harmony("AutomaticsSmoke-navigation-click");
            try
            {
                Map.RefreshPins();
                var visibleBefore = (bool)AccessTools.Method(typeof(Minimap), "IsPointVisible")
                    .Invoke(map, new[] { (object)pin.m_pos, image });
                var activeBefore = pin.m_uiElement && pin.m_uiElement.gameObject.activeInHierarchy;

                // Opening the map also needs its viewport centered before UpdatePins can build markers.
                AccessTools.Method(AccessTools.TypeByName("UnityEngine.Canvas"), "ForceUpdateCanvases")
                    .Invoke(null, null);
                AccessTools.Method(typeof(Minimap), "CenterMap").Invoke(map, new object[] { pin.m_pos });
                Map.RefreshPins();
                var active = pin.m_uiElement && pin.m_uiElement.gameObject.activeInHierarchy;
                _clickPosition = pin.m_pos;
                var isPressed = AccessTools.Method(typeof(global::Automatics.Valheim.HotkeyInput),
                    nameof(global::Automatics.Valheim.HotkeyInput.IsHeld));
                harmony.Patch(isPressed,
                    prefix: new HarmonyMethod(typeof(GuideFeatureSmoke), nameof(Pressed)));
                harmony.Patch(AccessTools.Method(typeof(Minimap), "ScreenToWorldPoint"),
                    postfix: new HarmonyMethod(typeof(GuideFeatureSmoke), nameof(ClickPosition)));
                var keyProbe = (bool)isPressed.Invoke(null, new object[] { MappingConfig.NavigationStartKey });
                _pressedCalls = 0;
                _clickPositionObserved = false;
                var radius = (float)AccessTools.Field(typeof(Minimap), "m_removeRadius").GetValue(map) *
                             ((float)AccessTools.Field(typeof(Minimap), "m_largeZoom").GetValue(map) * 2f);
                var closest = Map.GetClosestPin(_clickPosition, radius);
                var handled = Navigation.TryHandleMapClick(map);
                detail = $"running={MappingModule.Running}, large map={map.m_mode == Minimap.MapMode.Large}, " +
                         $"key bound={MappingConfig.NavigationStartKey.MainKey != KeyCode.None}, " +
                         $"before centering: in viewport={visibleBefore}, marker active={activeBefore}; " +
                         $"marker active={active}, IsPressed probe={keyProbe}, " +
                         $"key override observed={_pressedCalls > 0}, pointer override observed={_clickPositionObserved}, " +
                         $"closest pin={(closest == null ? "none" : ReferenceEquals(closest, pin) ? "expected" : "other")}, " +
                         $"handled={handled}";
                return handled;
            }
            finally
            {
                harmony.UnpatchSelf();
            }
        }

        private static readonly List<string> MessagesSeen = new List<string>();

        // Clears navigation by hand through the routine both manual entry points use, the way the
        // looked-at-object key does, and returns the messages shown to you meanwhile.
        private static List<string> ClearByHand(out bool observed)
        {
            MessagesSeen.Clear();
            // The player's own message method, or the one it inherits.
            var method = typeof(Player).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                                   BindingFlags.DeclaredOnly).FirstOrDefault(x => x.Name == "Message") ??
                         typeof(Character).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                                      BindingFlags.DeclaredOnly).FirstOrDefault(x => x.Name == "Message");
            observed = method != null;
            var harmony = new Harmony("AutomaticsSmoke-navigation-message");
            try
            {
                if (observed)
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(GuideFeatureSmoke), nameof(MessageShown)));
                Navigation.ClearTargetByHand(false);
            }
            finally
            {
                harmony.UnpatchSelf();
            }
            return MessagesSeen.ToList();
        }

        private static void MessageShown(object[] __args)
        {
            if (__args != null && __args.Length > 1 && __args[1] is string text) MessagesSeen.Add(text);
        }

        private static bool Pressed(ref bool __result)
        {
            _pressedCalls++;
            __result = true;
            return false;
        }

        private static void ClickPosition(ref Vector3 __result)
        {
            _clickPositionObserved = true;
            __result = _clickPosition;
        }

        private static void CheckMapClick(FeatureRun run, Minimap map, Minimap.PinData pin)
        {
            if (MappingConfig.NavigationStartKey.MainKey == KeyCode.None)
            {
                run.Skip("guide_map_click_toggle", "navigation_start_key is unbound and could not be bound for the section");
                return;
            }
            Navigation.ClearTarget();
            var started = ClickPin(map, pin, out var startDetail) && ReferenceEquals(Navigation.TargetPin, pin);
            var cleared = ClickPin(map, pin, out var clearDetail) && Navigation.TargetPin == null;
            var detail = $"start={started} ({startDetail}); clear on same pin={cleared} ({clearDetail})";
            if (started && cleared) run.Pass("guide_map_click_toggle", detail);
            else run.Fail("guide_map_click_toggle", detail);
        }

        private static bool RestoreTarget(Navigation.TargetState originalTarget)
        {
            Navigation.RestoreTarget(originalTarget);
            return originalTarget.Pin == null || originalTarget.Temporary ||
                   ReferenceEquals(Navigation.TargetPin, originalTarget.Pin);
        }

        private static List<Transform> FindGuideObjects()
        {
            return Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                         .Where(x => x && x.name == NavigationGuide.ObjectName)
                         .ToList();
        }

        private static int CountGuideObjects()
        {
            return FindGuideObjects().Count;
        }

        // The lights a particle system holds this frame.
        private static ParticleSystem.Particle[] ReadLights(ParticleSystem system)
        {
            if (!system) return Array.Empty<ParticleSystem.Particle>();
            var lights = new ParticleSystem.Particle[system.particleCount];
            var count = system.GetParticles(lights);
            if (count < lights.Length) Array.Resize(ref lights, count);
            return lights;
        }

        private static Dictionary<uint, Vector3> Positions(IEnumerable<ParticleSystem.Particle> lights)
        {
            var positions = new Dictionary<uint, Vector3>();
            foreach (var light in lights) positions[light.randomSeed] = light.position;
            return positions;
        }

        private static float Age(ParticleSystem.Particle light)
        {
            return light.startLifetime - light.remainingLifetime;
        }

        private static float Alpha(ParticleSystem.Particle light)
        {
            return light.startColor.a / 255f;
        }

        private static bool IsBurstLight(ParticleSystem.Particle light)
        {
            return light.startLifetime > BurstLifetimeSplit;
        }

        // The share of lights that can be seen, in a colour between navigation_guide_color and its
        // most whitened mix, allowing for the rounding of a light's colour to bytes.
        private static float VisibleShare(ParticleSystem.Particle[] lights, Color color)
        {
            if (lights.Length == 0) return 0f;
            var whitest = Color.Lerp(color, Color.white, NavigationGuide.MaxWhiteMix);
            var visible = lights.Count(x =>
            {
                var tint = x.startColor;
                return Alpha(x) > MinAlpha && Between(tint.r / 255f, color.r, whitest.r) &&
                       Between(tint.g / 255f, color.g, whitest.g) && Between(tint.b / 255f, color.b, whitest.b);
            });
            return (float)visible / lights.Length;
        }

        private static bool Between(float value, float from, float to)
        {
            return value >= Mathf.Min(from, to) - ColorStep && value <= Mathf.Max(from, to) + ColorStep;
        }

        private static bool SameColor(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) <= ColorStep && Mathf.Abs(a.g - b.g) <= ColorStep &&
                   Mathf.Abs(a.b - b.b) <= ColorStep && Mathf.Abs(a.a - b.a) <= ColorStep;
        }

        // The column's mean position, measured horizontally from the vertical axis through the
        // target.
        private static float ColumnOffset(ParticleSystem column)
        {
            var lights = ReadLights(column);
            if (lights.Length == 0) return float.MaxValue;
            var mean = Vector3.zero;
            foreach (var light in lights) mean += light.position;
            return Utils.DistanceXZ(mean / lights.Length, Navigation.TargetPosition);
        }

        // The horizontal direction from you toward the navigation target.
        private static Vector3 Toward(Vector3 playerPos)
        {
            var offset = Flat(Navigation.TargetPosition - playerPos);
            return offset.sqrMagnitude > 0.0001f ? offset.normalized : Vector3.forward;
        }

        private static Vector3 Side(Vector3 toward)
        {
            return new Vector3(toward.z, 0f, -toward.x);
        }

        private static Vector3 Forward(Player player)
        {
            var forward = Flat(player.transform.forward);
            return forward.sqrMagnitude < 0.001f ? Vector3.forward : forward.normalized;
        }

        private static Vector3 Flat(Vector3 vector)
        {
            vector.y = 0f;
            return vector;
        }

        // The ground the swarm follows, measured here on its own: the terrain, or the sea
        // surface where that is higher.
        private static Vector3 GroundPoint(Vector3 point, float fallback)
        {
            var zoneSystem = ZoneSystem.instance;
            point.y = zoneSystem && zoneSystem.GetGroundHeight(point, out var height)
                ? Mathf.Max(height, zoneSystem.m_waterLevel)
                : fallback;
            return point;
        }

        // The terrain height under a point, from the terrain collider the burst floor uses too.
        private static bool Terrain(Vector3 point, out float height)
        {
            height = 0f;
            var zoneSystem = ZoneSystem.instance;
            return zoneSystem && zoneSystem.GetGroundHeight(point, out height);
        }

        // Waits up to ReadyTimeoutSeconds for a shown swarm that a clear by hand can be judged on:
        // its burst over (no burst light younger than RadiateTime + GatherTime) and its stream and
        // column each holding at least MinLights lights with alpha above MinAlpha.
        private static IEnumerator AwaitReady(FeatureRun run, ReadySwarm ready)
        {
            var start = Time.time;
            while (run.IsCurrent)
            {
                ready.Look(Time.time - start);
                if (ready.Ready || ready.Waited >= ReadyTimeoutSeconds) yield break;
                yield return null;
            }
        }

        // The stream's line as the check expects it now, measured on its own: from you,
        // horizontally toward the target, as far as the target or TrailLength, whichever is nearer.
        private static Vector3[] ExpectedStreamLine()
        {
            var playerPos = Player.m_localPlayer.transform.position;
            var offset = Flat(Navigation.TargetPosition - playerPos);
            var length = Mathf.Min(offset.magnitude, NavigationGuide.TrailLength);
            var direction = offset.sqrMagnitude > 1e-6f ? offset.normalized : Vector3.forward;
            return new[] { playerPos, playerPos + direction * length };
        }

        // Waits for every guide object but the live swarm to remove itself, up to ScatterTime +
        // RemovalMargin; one still there is named for the later checks that count guide objects.
        private static IEnumerator WaitForOthersRemoved(FeatureRun run, SectionState state)
        {
            var since = Time.time;
            var deadline = since + NavigationGuide.ScatterTime + RemovalMargin;
            while (run.IsCurrent && Time.time < deadline && OtherGuideObjects().Count > 0) yield return null;
            var others = OtherGuideObjects();
            if (!run.IsCurrent || others.Count == 0 || state.Leftover != null) yield break;

            var names = others.Select(x =>
                $"'{x.name}' ({(x.gameObject.activeInHierarchy ? "active" : "inactive")}{(x.GetComponent<NavigationGuideScatter>() ? ", still scattering" : "")}; children {string.Join(", ", x.Cast<Transform>().Select(c => c.name))})");
            state.Leftover =
                $"{string.Join(", ", names)} still in the scene {Time.time - since:0.0} s after a clear by hand was checked (bound {NavigationGuide.ScatterTime + RemovalMargin:0.0} s: ScatterTime + {RemovalMargin} s)";
        }

        private static List<Transform> OtherGuideObjects()
        {
            var live = NavigationGuide.Root;
            return FindGuideObjects().Where(x => !live || x != live.transform).ToList();
        }

        // The sway a column light is prescribed at the height it is seen at: its level in the
        // column from the centre's height, 0 at the bottom and 1 at the top, before its jitter.
        private static Vector2 PrescribedSway(ParticleSystem.Particle light, Vector3 centre, float time)
        {
            var level = (light.position.y - centre.y) / NavigationGuide.ColumnHalfHeight;
            return NavigationGuide.SwayAt(Mathf.Clamp01((level + 1f) * 0.5f), time);
        }

        // The least-squares slope of y on x, with an intercept; NaN when x does not vary.
        private static float Slope(List<float> x, List<float> y)
        {
            if (x.Count < 2) return float.NaN;
            var meanX = x.Average();
            var meanY = y.Average();
            var sxx = 0.0;
            var sxy = 0.0;
            for (var i = 0; i < x.Count; i++)
            {
                sxx += (x[i] - meanX) * (x[i] - meanX);
                sxy += (x[i] - meanX) * (y[i] - meanY);
            }
            return sxx > 1e-12 ? (float)(sxy / sxx) : float.NaN;
        }

        // The point of a line of points nearest to a point, measured horizontally; a line of one
        // point gives that point.
        private static Vector3 NearestOnLine(Vector3[] line, Vector3 point)
        {
            var nearest = line[0];
            var nearestDistance = float.MaxValue;
            for (var i = 1; i < line.Length; i++)
            {
                var from = line[i - 1];
                var span = Flat(line[i] - from);
                var offset = Flat(point - from);
                var length = span.sqrMagnitude;
                var along = length > 0f ? Mathf.Clamp01(Vector3.Dot(offset, span) / length) : 0f;
                var distance = (offset - span * along).sqrMagnitude;
                if (distance >= nearestDistance) continue;
                nearestDistance = distance;
                nearest = from + span * along;
            }
            return nearest;
        }

        // What the section carries from one check to the next: a guide object an arrival left
        // behind, named for the checks that count guide objects.
        // It also holds the settings the section took on, put back and reported once.
        private sealed class SectionState
        {
            public string Leftover;
            public SmokeSession.SettingRestoration ArrivalSetting;
            public float SectionArrivalDistance;
            public SmokeSession.SettingRestoration GuideSetting;
            public bool SettingsReported;
        }

        // What an arrival probe saw: navigation and the swarm just outside the arrival distance,
        // where the pin was put just inside it, and how many frames navigation took to end there
        // (0 when it did not).
        private sealed class ArrivalProbe
        {
            public bool KeptOutside;
            public bool ShownOutside;
            public float OutsideDistance;
            public float InsideDistance;
            public float Rise;
            public float Distance;
            public float ArrivalDistance;
            public float InsideAt;
            public int Frames;
            public Vector3 Target;
            public GameObject Root;
            public ParticleSystem Stream;
            public ParticleSystem Column;

            public string Describe()
            {
                return
                    $"{OutsideDistance:0.0} m away horizontally: navigation {(KeptOutside ? "kept" : "ended")}, swarm {(ShownOutside ? "shown" : "not shown")}; " +
                    $"{InsideDistance:0.0} m away horizontally and {Rise:0.0} m above you ({Distance:0.0} m in 3D; navigation_arrival_distance {ArrivalDistance} m): " +
                    (Frames > 0 ? $"navigation ended in {Frames} frame(s)" : $"navigation still on after {ArrivalFrames} frames");
            }
        }

        // The burst lights a new swarm starts with, each followed by its seed: where it was first
        // seen, where it was once it had spread out and how low it went until then, and how far
        // along the line toward the target it was once it travelled with the stream and half a
        // second later.
        // It also keeps how near the terrain under them they came while they were bursting.
        private sealed class BurstWatch
        {
            private readonly Dictionary<uint, Track> _tracks = new Dictionary<uint, Track>();
            private readonly HashSet<uint> _seen = new HashSet<uint>();
            private float _floorLowest = float.MaxValue;
            private int _floorLooks;

            // When the swarm started: the birth of its oldest light at the first look.
            public float Start { get; private set; } = float.NaN;

            private bool Complete => !float.IsNaN(Start) && Time.time - Start > BurstWindowSeconds &&
                                     _tracks.Values.All(x => x.Later.HasValue);

            // Samples from the current frame on, until every burst light has travelled long enough.
            public IEnumerator Watch(FeatureRun run, Player player)
            {
                var deadline = Time.time + BurstWindowSeconds + TravelAge + TravelWatchSeconds + BurstSlackSeconds;
                while (run.IsCurrent && Time.time < deadline)
                {
                    Sample(player);
                    if (Complete) yield break;
                    yield return null;
                }
            }

            private void Sample(Player player)
            {
                var lights = ReadLights(NavigationGuide.Stream);
                if (lights.Length == 0) return;

                var now = Time.time;
                if (float.IsNaN(Start)) Start = now - lights.Max(x => Age(x));

                var playerPos = player.transform.position;
                var bodyPoint = playerPos + Vector3.up * NavigationGuide.BodyHeight;
                var ground = GroundPoint(playerPos, playerPos.y).y;
                var toward = Toward(playerPos);
                var side = Side(toward);
                foreach (var light in lights)
                {
                    var seed = light.randomSeed;
                    var age = Age(light);
                    // The swarm's first frame also lets out a flow light, told apart by its lifetime.
                    if (_seen.Add(seed) && IsBurstLight(light) && now - age - Start <= BurstWindowSeconds)
                        _tracks[seed] = new Track { StartOffset = Vector3.Distance(light.position, bodyPoint) };
                    if (!_tracks.TryGetValue(seed, out var track)) continue;

                    var offset = Flat(light.position - playerPos);
                    var along = Vector3.Dot(offset, toward);
                    if (!track.Spread.HasValue)
                        track.Lowest = Mathf.Min(track.Lowest, light.position.y - ground);
                    if (age < BurstAge && Terrain(light.position, out var terrain))
                    {
                        _floorLowest = Mathf.Min(_floorLowest, light.position.y - terrain);
                        _floorLooks++;
                    }
                    if (!track.Spread.HasValue && age >= NavigationGuide.RadiateTime)
                    {
                        track.Spread = new Vector2(along, Vector3.Dot(offset, side));
                        track.Reach = Vector3.Distance(light.position, bodyPoint);
                        track.Rise = light.position.y - bodyPoint.y;
                    }
                    if (!track.Travel.HasValue && age >= TravelAge)
                        track.Travel = along;
                    if (!track.Later.HasValue && age >= TravelAge + TravelWatchSeconds)
                        track.Later = along;
                }
            }

            public bool Evaluate(out string detail)
            {
                var tracks = _tracks.Values.ToList();
                if (tracks.Count == 0)
                {
                    detail = $"no burst light (start lifetime above {BurstLifetimeSplit:0.00} s) was seen born in the swarm's first {BurstWindowSeconds} s";
                    return false;
                }

                var farthestStart = tracks.Max(x => x.StartOffset);
                var spread = tracks.Where(x => x.Spread.HasValue).ToList();
                var reaches = spread.Select(x => x.Reach).OrderBy(x => x).ToList();
                var median = reaches.Count > 0 ? reaches[reaches.Count / 2] : 0f;
                var flat = spread.Select(x => x.Spread.Value).ToList();
                var quadrants = flat.Select(x => (x.x >= 0f ? 0 : 2) + (x.y >= 0f ? 0 : 1)).Distinct().Count();
                var behind = flat.Count(x => x.magnitude > 0.001f && x.x / x.magnitude < BehindCosine);
                var behindShare = Share(behind, spread.Count);
                var aboveShare = Share(spread.Count(x => x.Rise > MinBurstRise), spread.Count);
                var belowShare = Share(spread.Count(x => x.Rise < -MinBurstDrop), spread.Count);
                var lowest = tracks.Min(x => x.Lowest);
                var growth = tracks.Where(x => x.Travel.HasValue && x.Later.HasValue)
                                   .Select(x => x.Later.Value - x.Travel.Value)
                                   .ToList();
                var grown = growth.Count(x => x > 0f);
                var least = growth.Count > 0 ? growth.Min() : 0f;

                detail =
                    $"{tracks.Count} burst lights (start lifetime above {BurstLifetimeSplit:0.00} s, between FlowLifetime and BurstLifetime) born in the first {BurstWindowSeconds} s started at most {farthestStart:0.00} m from the body point {NavigationGuide.BodyHeight} m above your feet (bound {BodyStartRadius} m); " +
                    $"at age {NavigationGuide.RadiateTime} s (RadiateTime) {spread.Count} of them were a median {median:0.00} m from the body point (bound {MinSpreadDistance} m) in {quadrants} of 4 quadrants around the line to the target (bound {MinQuadrants}), {behindShare:P0} behind you (bound {MinBehindShare:P0}), {aboveShare:P0} more than {MinBurstRise} m above the body point and {belowShare:P0} more than {MinBurstDrop} m below it (bounds {MinVerticalShare:P0} each); " +
                    $"the lowest was {lowest:0.00} m above the ground under you (bound 0); " +
                    (_floorLooks > 0
                        ? $"younger than {BurstAge:0.0} s (RadiateTime + GatherTime), the lowest was {_floorLowest:0.00} m above the terrain under it over {_floorLooks} looks (bound {MinBurstFloor:0.00} m: BurstFloor {NavigationGuide.BurstFloor} m - {FloorTolerance} m); "
                        : $"no look at a light younger than {BurstAge:0.0} s (RadiateTime + GatherTime) over terrain; ") +
                    $"from age {TravelAge:0.0} s (RadiateTime + GatherTime + {TravelMargin} s), {grown} of {growth.Count} moved farther along the line over {TravelWatchSeconds} s (least {least:0.00} m)";
                return farthestStart <= BodyStartRadius && spread.Count > 0 && median >= MinSpreadDistance &&
                       quadrants >= MinQuadrants && behindShare >= MinBehindShare &&
                       aboveShare >= MinVerticalShare && belowShare >= MinVerticalShare && lowest >= 0f &&
                       _floorLooks > 0 && _floorLowest >= MinBurstFloor &&
                       growth.Count > 0 && grown == growth.Count;
            }

            private static float Share(int count, int total)
            {
                return total > 0 ? (float)count / total : 0f;
            }

            private sealed class Track
            {
                public float StartOffset;
                public float Lowest = float.MaxValue;
                public Vector2? Spread;
                public float Reach;
                public float Rise;
                public float? Travel;
                public float? Later;
            }
        }

        // The stream lights born within a span of time, each looked at in every frame it is
        // sampled: how far behind you along the line toward the target and how far to the line's
        // side the farthest of them came. It also counts every burst light it sees.
        private sealed class FlowWatch
        {
            private readonly float _from;
            private readonly float _to;
            private readonly HashSet<uint> _lights = new HashSet<uint>();
            private readonly HashSet<uint> _burst = new HashSet<uint>();
            private int _samples;

            public FlowWatch(float from, float to)
            {
                _from = from;
                _to = to;
            }

            public int Lights => _lights.Count;
            public int BurstLights => _burst.Count;
            public float MostBehind { get; private set; } = float.MinValue;
            public float Widest { get; private set; }

            public void Sample(Player player)
            {
                var now = Time.time;
                var playerPos = player.transform.position;
                var toward = Toward(playerPos);
                var side = Side(toward);
                foreach (var light in ReadLights(NavigationGuide.Stream))
                {
                    if (IsBurstLight(light)) _burst.Add(light.randomSeed);
                    var born = now - Age(light);
                    if (born < _from || born > _to) continue;

                    var offset = Flat(light.position - playerPos);
                    MostBehind = Mathf.Max(MostBehind, -Vector3.Dot(offset, toward));
                    Widest = Mathf.Max(Widest, Mathf.Abs(Vector3.Dot(offset, side)));
                    _lights.Add(light.randomSeed);
                    _samples++;
                }
            }

            public string Describe()
            {
                return _lights.Count == 0
                    ? "no light seen"
                    : $"{_lights.Count} lights over {_samples} samples, at most {MostBehind:0.00} m behind you along the line and {Widest:0.00} m to its side";
            }
        }

        // The farthest the column's lights were from the vertical axis through the target, and
        // their lowest and highest heights above it, against the column's own extent.
        private sealed class ColumnBounds
        {
            private float _widest;
            private float _lowest = float.MaxValue;
            private float _highest = float.MinValue;
            private int _samples;

            public bool Holds => _samples > 0 && _widest <= MaxColumnSpread && _lowest >= MinColumnHeight &&
                                 _highest <= MaxColumnHeight;

            public void Sample(ParticleSystem column)
            {
                var target = Navigation.TargetPosition;
                foreach (var light in ReadLights(column))
                {
                    _widest = Mathf.Max(_widest, Utils.DistanceXZ(light.position, target));
                    _lowest = Mathf.Min(_lowest, light.position.y - target.y);
                    _highest = Mathf.Max(_highest, light.position.y - target.y);
                    _samples++;
                }
            }

            public string Describe()
            {
                return _samples == 0
                    ? "no column light"
                    : $"{_samples} light positions, at most {_widest:0.00} m from the axis and {_lowest:0.00}–{_highest:0.00} m above the target";
            }
        }

        // One particle system's lights through a scatter, followed by seed: the lights it held at
        // the first look after the arrival, the visible ones among them, any light that comes
        // later, any visible one that goes, and how far out from the column's axis and how bright
        // the visible ones are at the first and the last look.
        private sealed class ScatterCohort
        {
            private readonly string _name;
            private readonly ParticleSystem _system;
            private readonly Vector3 _axis;
            private readonly HashSet<uint> _held = new HashSet<uint>();
            private readonly Dictionary<uint, ParticleSystem.Particle> _first =
                new Dictionary<uint, ParticleSystem.Particle>();
            private readonly HashSet<uint> _added = new HashSet<uint>();
            private readonly HashSet<uint> _lost = new HashSet<uint>();
            private Dictionary<uint, ParticleSystem.Particle> _last = new Dictionary<uint, ParticleSystem.Particle>();

            public ScatterCohort(string name, ParticleSystem system, Vector3 axis)
            {
                _name = name;
                _system = system;
                _axis = axis;
                foreach (var light in ReadLights(system))
                {
                    _held.Add(light.randomSeed);
                    if (Alpha(light) > MinAlpha) _first[light.randomSeed] = light;
                }
            }

            public void Sample()
            {
                _last = new Dictionary<uint, ParticleSystem.Particle>();
                foreach (var light in ReadLights(_system))
                {
                    if (!_held.Contains(light.randomSeed)) _added.Add(light.randomSeed);
                    if (_first.ContainsKey(light.randomSeed)) _last[light.randomSeed] = light;
                }
                foreach (var seed in _first.Keys)
                    if (!_last.ContainsKey(seed))
                        _lost.Add(seed);
            }

            public bool Evaluate(float bound, out string detail)
            {
                if (_first.Count == 0)
                {
                    detail = $"{_name}: no light visible at the clear";
                    return false;
                }

                var kept = _first.Keys.Where(_last.ContainsKey).ToList();
                if (kept.Count == 0)
                {
                    detail = $"{_name}: all {_first.Count} visible lights gone, {_added.Count} new light(s)";
                    return false;
                }

                var firstSpread = kept.Average(x => Utils.DistanceXZ(_first[x].position, _axis));
                var lastSpread = kept.Average(x => Utils.DistanceXZ(_last[x].position, _axis));
                var firstAlpha = kept.Average(x => Alpha(_first[x]));
                var lastAlpha = kept.Average(x => Alpha(_last[x]));
                var growth = lastSpread - firstSpread;
                detail =
                    $"{_name} {_first.Count} visible of {_held.Count} lights at the clear, {_lost.Count} of them gone, {_added.Count} new; mean {firstSpread:0.00} → {lastSpread:0.00} m from the axis (grew {growth:0.000} m, bound {bound:0.000} m); mean alpha {firstAlpha:0.00} → {lastAlpha:0.00} (bound below {firstAlpha * 0.5f:0.00})";
                return _lost.Count == 0 && _added.Count == 0 && growth >= bound && lastAlpha < firstAlpha * 0.5f;
            }
        }

        // How a guide object is drawn: drawn with both particle renderers and both point lights on
        // and its particle systems not paused, hidden with all of them off and the systems paused;
        // the object stays active either way.
        private sealed class Drawing
        {
            private bool _active;
            private int _renderers;
            private int _renderersOn;
            private int _lights;
            private int _lightsOn;
            private int _systems;
            private int _paused;

            public bool Drawn => _active && _renderers == 2 && _renderersOn == 2 && _lights == 2 && _lightsOn == 2 &&
                                 _systems == 2 && _paused == 0;

            public bool Hidden => _active && _renderers == 2 && _renderersOn == 0 && _lights == 2 && _lightsOn == 0 &&
                                  _systems == 2 && _paused == 2;

            public static Drawing Of(GameObject root)
            {
                var drawing = new Drawing();
                if (!root) return drawing;
                drawing._active = root.activeInHierarchy;
                var renderers = root.GetComponentsInChildren<ParticleSystemRenderer>(true);
                drawing._renderers = renderers.Length;
                drawing._renderersOn = renderers.Count(x => x.enabled);
                var lights = root.GetComponentsInChildren<Light>(true);
                drawing._lights = lights.Length;
                drawing._lightsOn = lights.Count(x => x.enabled);
                var systems = root.GetComponentsInChildren<ParticleSystem>(true);
                drawing._systems = systems.Length;
                drawing._paused = systems.Count(x => x.isPaused);
                return drawing;
            }

            public override string ToString()
            {
                return _renderers == 0 && _lights == 0 && _systems == 0
                    ? "no guide object"
                    : $"object {(_active ? "active" : "inactive")}, renderers {_renderersOn}/{_renderers} on, point lights {_lightsOn}/{_lights} on, particle systems {_paused}/{_systems} paused";
            }
        }

        // One particle system's lights through a scatter by hand, followed by seed from the first
        // look after it began: the visible lights then, each one's outward direction from its
        // nearest point on the line it flies off from, any light that comes later, any visible one
        // that goes, and how far outward and how bright the visible ones are at the last look.
        private sealed class OutwardCohort
        {
            private readonly string _name;
            private readonly string _from;
            private readonly ParticleSystem _system;
            private readonly HashSet<uint> _held = new HashSet<uint>();
            private readonly Dictionary<uint, ParticleSystem.Particle> _first =
                new Dictionary<uint, ParticleSystem.Particle>();
            private readonly Dictionary<uint, Vector3> _outward = new Dictionary<uint, Vector3>();
            private readonly HashSet<uint> _added = new HashSet<uint>();
            private readonly HashSet<uint> _lost = new HashSet<uint>();
            private Dictionary<uint, ParticleSystem.Particle> _last = new Dictionary<uint, ParticleSystem.Particle>();

            public OutwardCohort(string name, ParticleSystem system, Vector3[] line, string from)
            {
                _name = name;
                _from = from;
                _system = system;
                foreach (var light in ReadLights(system))
                {
                    _held.Add(light.randomSeed);
                    if (Alpha(light) <= MinAlpha) continue;
                    _first[light.randomSeed] = light;
                    var away = Flat(light.position - NearestOnLine(line, light.position));
                    _outward[light.randomSeed] = away.sqrMagnitude > 1e-8f ? away.normalized : Vector3.zero;
                }
            }

            public void Sample()
            {
                _last = new Dictionary<uint, ParticleSystem.Particle>();
                foreach (var light in ReadLights(_system))
                {
                    if (!_held.Contains(light.randomSeed)) _added.Add(light.randomSeed);
                    if (_first.ContainsKey(light.randomSeed)) _last[light.randomSeed] = light;
                }
                foreach (var seed in _first.Keys)
                    if (!_last.ContainsKey(seed))
                        _lost.Add(seed);
            }

            public bool Evaluate(float bound, out string detail)
            {
                if (_first.Count == 0)
                {
                    detail = $"{_name}: no light visible when the scatter began";
                    return false;
                }

                var kept = _first.Keys.Where(_last.ContainsKey).ToList();
                if (kept.Count == 0)
                {
                    detail = $"{_name}: all {_first.Count} visible lights gone, {_added.Count} new light(s)";
                    return false;
                }

                var moves = kept.Select(x => Vector3.Dot(Flat(_last[x].position - _first[x].position), _outward[x]))
                                .ToList();
                var outward = moves.Count(x => x > 0f);
                var share = (float)outward / _first.Count;
                var mean = moves.Average();
                var firstAlpha = kept.Average(x => Alpha(_first[x]));
                var lastAlpha = kept.Average(x => Alpha(_last[x]));
                detail =
                    $"{_name} {_first.Count} visible of {_held.Count} lights when the scatter began, {_lost.Count} of them gone, {_added.Count} new; {outward} ({share:P0}) moved outward from {_from}, a mean {mean:0.000} m outward (bound {bound:0.000} m); mean alpha {firstAlpha:0.00} → {lastAlpha:0.00} (bound below {firstAlpha * 0.5f:0.00})";
                return _lost.Count == 0 && _added.Count == 0 && share >= MinOutwardShare && mean >= bound &&
                       lastAlpha < firstAlpha * 0.5f;
            }
        }

        // What the wait for a swarm ready to be cleared by hand saw at its last look.
        private sealed class ReadySwarm
        {
            private bool _shown;
            private int _streamVisible;
            private int _columnVisible;
            private int _bursting;

            public float Waited { get; private set; }

            public bool Ready => _shown && _streamVisible >= MinLights && _columnVisible >= MinLights && _bursting == 0;

            public void Look(float waited)
            {
                Waited = waited;
                _shown = NavigationGuide.Shown;
                var stream = ReadLights(NavigationGuide.Stream);
                _streamVisible = stream.Count(x => Alpha(x) > MinAlpha);
                _columnVisible = ReadLights(NavigationGuide.Column).Count(x => Alpha(x) > MinAlpha);
                _bursting = stream.Count(x => IsBurstLight(x) && Age(x) < BurstAge);
            }

            public string Describe()
            {
                return
                    $"swarm {(_shown ? "shown" : "not shown")} with {_streamVisible} stream and {_columnVisible} column lights with alpha above {MinAlpha} (bound at least {MinLights} each) and {_bursting} burst light(s) younger than {BurstAge:0.0} s (RadiateTime + GatherTime; bound none) after {Waited:0.00} s (bound {ReadyTimeoutSeconds} s)";
            }
        }

        private sealed class ScatterOutcome
        {
            public bool Passed;
            public string Detail = "";
        }

        // What a map click with the swarm hidden by the large map did: whether the swarm was hidden
        // at the click, the swarm the click held and its lights then, and, over the hold with the
        // map open, whether navigation stayed cleared, the held swarm stayed the only guide object,
        // hidden, and its lights kept their seeds and positions. The steps before closing record
        // what they did here too.
        private sealed class HeldClick
        {
            public int FramesToHide;
            public bool HiddenAtClick;
            public string HiddenDrawing = "";
            public bool Handled;
            public string ClickDetail = "";
            public float ClickTime;
            public GameObject Held;
            public bool HeldSwarm;
            public ParticleSystem Stream;
            public ParticleSystem Column;
            public Vector3[] Line;
            public Vector3 Axis;
            public Dictionary<uint, Vector3> StreamAtClick = new Dictionary<uint, Vector3>();
            public Dictionary<uint, Vector3> ColumnAtClick = new Dictionary<uint, Vector3>();

            public bool SecondHandled;
            public string SecondDetail = "";
            public bool Restarted;
            public bool DroppedBeforeClose;
            public bool TurnedOff;
            public bool TurnedOn;
            public string Refusal = "";

            private readonly HashSet<uint> _missing = new HashSet<uint>();
            private readonly HashSet<uint> _added = new HashSet<uint>();
            private int _holdFrames;
            private float _heldFor;
            private bool _stayedCleared = true;
            private bool _stayedSingle = true;
            private bool _stayedHidden = true;
            private float _mostMoved;

            public bool Kept => _holdFrames > 0 && _heldFor >= HoldSeconds && _stayedCleared && _stayedSingle &&
                                _stayedHidden && _missing.Count == 0 && _added.Count == 0 &&
                                _mostMoved <= HeldPositionTolerance && StreamAtClick.Count >= MinLights;

            public void Sample()
            {
                _holdFrames++;
                _heldFor = Time.time - ClickTime;
                if (Navigation.TargetPin != null) _stayedCleared = false;
                var objects = FindGuideObjects();
                if (!Held || objects.Count != 1 || objects[0] != Held.transform) _stayedSingle = false;
                if (!Held || !Drawing.Of(Held).Hidden) _stayedHidden = false;
                Compare(StreamAtClick, Stream);
                Compare(ColumnAtClick, Column);
            }

            public string Describe()
            {
                return
                    $"after opening the map the swarm was {(HiddenAtClick ? "hidden" : "not hidden")} in {FramesToHide} frame(s) ({HiddenDrawing}); the map click was {(Handled ? "handled" : "not handled")} ({ClickDetail}) and {(HeldSwarm ? "held that swarm with navigation cleared" : "did not hold the swarm with navigation cleared")}; " +
                    $"over {_heldFor:0.00} s ({_holdFrames} frames; bound at least {HoldSeconds} s) with the map open: navigation {(_stayedCleared ? "stayed cleared" : "came back")}, {(_stayedSingle ? "one guide object, the held swarm" : "not the held swarm alone")}, {(_stayedHidden ? "hidden throughout" : "not hidden throughout")}; " +
                    $"of its {StreamAtClick.Count} stream (bound at least {MinLights}) and {ColumnAtClick.Count} column lights at the click {_missing.Count} gone and {_added.Count} new, the farthest moved {_mostMoved:0.0000} m (bound {HeldPositionTolerance} m)";
            }

            private void Compare(Dictionary<uint, Vector3> atClick, ParticleSystem system)
            {
                var now = Positions(ReadLights(system));
                foreach (var pair in atClick)
                {
                    if (now.TryGetValue(pair.Key, out var position))
                        _mostMoved = Mathf.Max(_mostMoved, Vector3.Distance(position, pair.Value));
                    else
                        _missing.Add(pair.Key);
                }
                foreach (var seed in now.Keys)
                    if (!atClick.ContainsKey(seed))
                        _added.Add(seed);
            }
        }

        // What appears once the map closes after a held swarm was dropped: any scattering guide
        // object, any guide object at all, and a new swarm with its burst.
        private sealed class AfterCloseWatch
        {
            public bool ScatterSeen;
            public bool ObjectSeen;
            public bool NewSwarmSeen;
            public bool BurstSeen;
            private int _frames;

            public IEnumerator Watch(FeatureRun run)
            {
                var start = Time.time;
                while (run.IsCurrent)
                {
                    yield return null;
                    _frames++;
                    var objects = FindGuideObjects();
                    ObjectSeen |= objects.Count > 0;
                    ScatterSeen |= objects.Any(x => x.GetComponent<NavigationGuideScatter>() != null);
                    NewSwarmSeen |= NavigationGuide.Shown;
                    BurstSeen |= ReadLights(NavigationGuide.Stream).Any(IsBurstLight);
                    if (Time.time - start >= AbsentWatchSeconds) break;
                }
            }

            public string Describe()
            {
                return
                    $"over {_frames} frames: {(ScatterSeen ? "a scattering guide object appeared" : "no scattering guide object")}, {(ObjectSeen ? "a guide object" : "no guide object")} seen, {(NewSwarmSeen ? "a new swarm shown" : "no new swarm shown")}, {(BurstSeen ? "with burst lights" : "no burst light")}";
            }
        }

        // The large map's view and mode, put back after a simulated click.
        private sealed class MapView
        {
            private object _image;
            private PropertyInfo _uvRectProperty;
            private object _uvRect;
            private Material _shader;
            private Vector4 _shaderCenter;
            private float _shaderZoom;
            private float _shaderPixelSize;
            private Dictionary<FieldInfo, object> _fields;

            public Minimap.MapMode Mode { get; private set; }

            public static MapView Capture(Minimap map)
            {
                var view = new MapView { Mode = map.m_mode };
                view._image = AccessTools.Field(typeof(Minimap), "m_mapImageLarge").GetValue(map);
                view._uvRectProperty = AccessTools.Property(view._image.GetType(), "uvRect");
                view._uvRect = view._uvRectProperty.GetValue(view._image, null);
                view._shader = (Material)AccessTools.Field(typeof(Minimap), "m_mapLargeShader").GetValue(map);
                view._shaderCenter = view._shader.GetVector("_mapCenter");
                view._shaderZoom = view._shader.GetFloat("_zoom");
                view._shaderPixelSize = view._shader.GetFloat("_pixelSize");
                view._fields = new[]
                {
                    "m_mapOffset", "m_dragView", "m_namePin", "m_previousMapCenter",
                    "m_previousLargeZoom", "m_pinUpdateRequired"
                }.Select(name => AccessTools.Field(typeof(Minimap), name))
                    .ToDictionary(field => field, field => field.GetValue(map));
                return view;
            }

            public void Restore(Minimap map, Minimap.MapMode mode)
            {
                if (!map) return;
                map.SetMapMode(mode);
                _uvRectProperty.SetValue(_image, _uvRect, null);
                _shader.SetVector("_mapCenter", _shaderCenter);
                _shader.SetFloat("_zoom", _shaderZoom);
                _shader.SetFloat("_pixelSize", _shaderPixelSize);
                foreach (var field in _fields) field.Key.SetValue(map, field.Value);
                Map.RefreshPins();
            }
        }

        // The loaded terrain around a point, looked at once on a grid of SearchStep within
        // SearchRadius through the terrain collider.
        private sealed class TerrainGrid
        {
            private readonly Dictionary<long, float> _heights = new Dictionary<long, float>();
            private readonly List<int[]> _cells = new List<int[]>();
            private readonly Vector3 _centre;

            public TerrainGrid(Vector3 centre)
            {
                _centre = centre;
                var reach = Mathf.FloorToInt(SearchRadius / SearchStep);
                for (var i = -reach; i <= reach; i++)
                for (var j = -reach; j <= reach; j++)
                {
                    if ((i * i + j * j) * SearchStep * SearchStep > SearchRadius * SearchRadius) continue;
                    if (!Terrain(Point(i, j, 0f), out var height)) continue;
                    _heights[Key(i, j)] = height;
                    _cells.Add(new[] { i, j });
                }
            }

            public int Count => _cells.Count;

            // The accepted point nearest to a point, measured horizontally, its height the terrain's.
            public bool Nearest(Vector3 to, Func<Vector3, bool> accept, out Vector3 point)
            {
                point = default;
                var found = false;
                var nearest = float.MaxValue;
                foreach (var cell in _cells)
                {
                    var candidate = Point(cell[0], cell[1], _heights[Key(cell[0], cell[1])]);
                    if (!accept(candidate)) continue;
                    var distance = Utils.DistanceXZ(candidate, to);
                    if (distance >= nearest) continue;
                    nearest = distance;
                    point = candidate;
                    found = true;
                }
                return found;
            }

            // The point whose terrain rises most to a neighbour SearchStep away along a grid axis,
            // the direction of that rise and how much it rises.
            public bool Steepest(out Vector3 point, out Vector3 uphill, out float rise)
            {
                point = default;
                uphill = Vector3.forward;
                rise = float.MinValue;
                var found = false;
                var steps = new[] { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 } };
                foreach (var cell in _cells)
                {
                    var height = _heights[Key(cell[0], cell[1])];
                    foreach (var step in steps)
                    {
                        if (!_heights.TryGetValue(Key(cell[0] + step[0], cell[1] + step[1]), out var next)) continue;
                        if (next - height <= rise) continue;
                        rise = next - height;
                        point = Point(cell[0], cell[1], height);
                        uphill = new Vector3(step[0], 0f, step[1]);
                        found = true;
                    }
                }
                return found;
            }

            private Vector3 Point(int i, int j, float height)
            {
                return new Vector3(_centre.x + i * SearchStep, height, _centre.z + j * SearchStep);
            }

            private static long Key(int i, int j)
            {
                return ((long)i << 32) | (uint)j;
            }
        }

        // One placement of the pin for the raised column: where the pin is, the height the column
        // should stand on there, and the lowest and highest column light above it.
        private sealed class RaisedCase
        {
            private readonly string _name;
            private readonly string _baseName;
            private float _lowest = float.MaxValue;
            private float _highest = float.MinValue;
            private int _samples;

            public RaisedCase(string name, Vector3 pin, float expectedBase, string baseName)
            {
                _name = name;
                Pin = pin;
                Base = expectedBase;
                _baseName = baseName;
            }

            public Vector3 Pin { get; }
            public float Base { get; }
            public float Distance;

            public bool Holds => _samples > 0 && _lowest >= MinColumnHeight && _highest <= MaxColumnHeight;

            public void Sample(float aboveBase)
            {
                _lowest = Mathf.Min(_lowest, aboveBase);
                _highest = Mathf.Max(_highest, aboveBase);
                _samples++;
            }

            public string Describe()
            {
                return _samples == 0
                    ? $"{_name}, {Distance:0} m from you: no column light"
                    : $"{_name}, {Distance:0} m from you, expected base {_baseName} at {Base:0.00} m: {_samples} light positions {_lowest:0.00}–{_highest:0.00} m above it";
            }
        }
    }
}
