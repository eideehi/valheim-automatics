using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Automatics.AutomaticMapping;
using HarmonyLib;
using UnityEngine;
using MappingConfig = Automatics.AutomaticMapping.Config;
using MappingModule = Automatics.AutomaticMapping.Module;
using Object = UnityEngine.Object;

namespace Automatics.SmokeTest
{
    // Navigates to an unsaved test pin about 60 m behind the camera, lets the guide swarm run for
    // real frames and reads its lights back, following each light by its random seed: the burst
    // that spreads out from your body in every direction, the stream's height over the ground and
    // its flow toward the target with the later lights leaving you along the line, the column over
    // the target and how it follows a moved target, the stream ending at a near target, hiding on
    // the large map and the stream resuming without a burst, the navigation ending within the
    // arrival distance and the swarm scattering there, a navigation started next to its target,
    // the runtime option and the teardown when the navigation is cleared. It also logs which
    // particle material the swarm resolved. Bounds that follow the swarm's look are derived from
    // its constants.
    //
    // You stand still: the section moves the test pin, and the game's own player update drives the
    // navigation every frame. After each arrival it waits for the scattering swarm to remove itself
    // and starts navigation on the pin at 60 m again, so the later checks have one.
    //
    // The test pin is unsaved, so no map save can keep it, and it is the only pin the section
    // removes. The navigation target, the map mode and enable_navigation_guide are put back when
    // the section ends, and by the session hand-back when the run is cut short.
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
        private const float MaxColumnSpread =
            NavigationGuide.ColumnRadius + NavigationGuide.JitterAmplitude + ColumnMargin;
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

        private const float OutsideArrivalDistance = Navigation.ArrivalDistance + 0.5f;
        private const float InsideArrivalDistance = Navigation.ArrivalDistance - 0.5f;
        private const float ArrivalProbeRise = 3f;
        private const int OutsideFrames = 3;
        private const int ArrivalFrames = 2;

        private const float ScatterWatchSeconds = 0.8f;
        private const float RemovalMargin = 0.5f;

        private const float StartInsideDistance = 3f;
        private const float RearmDistance = 8f;
        private const float KeepSeconds = 1f;
        private const int RearmFrames = 2;

        private static readonly FieldInfo TargetPinField = AccessTools.Field(typeof(Navigation), "_targetPin");
        private static readonly MethodInfo SetTargetMethod = AccessTools.Method(typeof(Navigation), "SetTarget");

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
            if (!MappingModule.Running)
            {
                run.Skip("guide_section", "Automatic Mapping is not running; enable the module first");
                yield break;
            }
            if (!MappingConfig.EnableNavigationGuide)
            {
                run.Skip("guide_section", "enable_navigation_guide is off; turn it on first");
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

                steps = CheckTrailEnd(run, player, testPin);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;

                steps = CheckLargeMap(run, player, map, originalMode);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;

                var state = new SectionState();
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
                    if (handBack("guide feature smoke")) SmokeSession.ReleaseHandBack(handBack);
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
            var detail =
                $"{systems} particle system(s) and {lights.Length} light(s), {coloured} of them point lights in navigation_guide_color, {networked} ZNetView, root object={root.transform.parent == null}, active={root.activeInHierarchy}; " +
                $"stream {streamLights.Length} lights, {streamVisible:P0} with alpha above {MinAlpha} in the guide colour; column {columnLights.Length} lights, {columnVisible:P0} (bounds: at least {MinLights} lights and {MinVisibleShare:P0} each); " +
                $"HUD label {(labelHidden ? "off-screen" : overlay ? "shown" : "missing")}";
            if (root.activeInHierarchy && root.transform.parent == null && systems == 2 && lights.Length == 2 &&
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
                $"bounds {MaxColumnSpread:0.00} m from the axis (ColumnRadius + JitterAmplitude + {ColumnMargin} m) and {MinColumnHeight:0.00}–{MaxColumnHeight:0.00} m above the target (ColumnCentreHeight ± (ColumnHalfHeight + JitterAmplitude + {ColumnMargin} m))";
            if (held.Holds && tracked > 0 && motion > MinColumnMotion && followedAfter >= 0f && resettled.Holds)
                run.Pass("guide_destination_swarm", detail);
            else
                run.Fail("guide_destination_swarm", detail);
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
            var modeSeen = map.m_mode;
            var shownAgain = false;
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
                hidden = map.m_mode == Minimap.MapMode.Large && root && !root.activeInHierarchy;
                modeSeen = map.m_mode;
                map.SetMapMode(originalMode == Minimap.MapMode.Large ? Minimap.MapMode.Small : originalMode);
                var closeTime = Time.time;
                resumed = new FlowWatch(closeTime, closeTime + ResumeWindowSeconds);
                yield return null;
                root = NavigationGuide.Root;
                shownAgain = root && root.activeInHierarchy;
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
                $"map mode {modeSeen}: swarm {(hidden ? "hidden" : "still shown or gone")}; after closing: {(shownAgain ? "shown" : "not shown")}, column {refilled} lights with {stale} left from before the map opened (bounds: at least {MinLights}, none left); " +
                (resumed == null
                    ? "the stream was not watched"
                    : $"over {ResumeWatchSeconds} s, {resumed.BurstLights} burst light(s) (start lifetime above {BurstLifetimeSplit:0.00} s; bound none), and the lights born in the first {ResumeWindowSeconds} s: {resumed.Describe()} (bound {MaxBehind} m behind)");
            if (hidden && shownAgain && refilled >= MinLights && stale == 0 && resumed != null &&
                resumed.BurstLights == 0 && resumed.Lights > 0 && resumed.MostBehind <= MaxBehind)
                run.Pass("guide_hidden_on_large_map", detail);
            else
                run.Fail("guide_hidden_on_large_map", detail);
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

            // The least a light flies out from the axis by now, leaving last at the lowest speed
            // and straight out only by the smallest share of its direction; the zigzag around
            // its path averages out over a system's lights.
            var elapsed = Time.time - clearTime;
            var share = (1f - NavigationGuide.ScatterSpread) /
                        (1f + NavigationGuide.ScatterSpread + NavigationGuide.ScatterLift);
            var flying = Mathf.Max(0f, elapsed - NavigationGuide.ScatterDelay);
            var travel = NavigationGuide.MinScatterSpeed / NavigationGuide.ScatterDecay *
                         (1f - Mathf.Exp(-NavigationGuide.ScatterDecay * flying));
            var bound = 0.5f * share * travel;
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
                $"started {insideDistance:0.0} m away (ArrivalDistance {Navigation.ArrivalDistance} m) from a navigation {(armed ? "on" : "not on")} the test pin: {(freshSwarm ? "a new swarm" : newRoot ? "the same swarm" : "no swarm")} a frame later, navigation {(kept ? "kept" : "ended")} after {KeepSeconds} s; " +
                $"{outsideDistance:0.0} m away for {RearmFrames} frames: navigation {(keptOutside ? "kept" : "ended")}; back at {StartInsideDistance} m: {(frames > 0 ? $"ended in {frames} frame(s)" : $"still on after {ArrivalFrames} frames")}";
            if (armed && freshSwarm && kept && keptOutside && frames > 0)
                run.Pass("navigation_kept_when_started_inside", detail);
            else
                run.Fail("navigation_kept_when_started_inside", detail);

            if (frames == 0) yield break;
            var steps = WaitForRemoval(run, clearTime, state);
            while (steps.MoveNext()) yield return steps.Current;
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
                if (!SmokeSession.BeginSetting(run.RunId, entry, out var setting, out var refusal) ||
                    !SmokeSession.WriteSetting(run.RunId, setting, false, out refusal))
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
                var results = SmokeSession.RestoreSettings(run.RunId);
                if (results.Count > 0)
                {
                    var detail = string.Join("; ", results.Select(x => x.Value));
                    if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.Failed))
                        run.Fail("guide_settings_restored", detail);
                    else if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.LeftForUser ||
                                              x.Key == SmokeSession.ToggleOutcome.NotThisRun))
                        run.Skip("guide_settings_restored", "restoration skipped: " + detail);
                    else
                        run.Pass("guide_settings_restored", detail);
                }
            }

            yield return null;
            yield return null;
            if (MappingConfig.EnableNavigationGuide && NavigationGuide.Root &&
                NavigationGuide.Root.activeInHierarchy)
                run.Pass("guide_option_on_recreates");
            else if (!MappingConfig.EnableNavigationGuide)
                run.Skip("guide_option_on_recreates", "enable_navigation_guide was not turned back on");
            else
                run.Fail("guide_option_on_recreates", "the swarm did not come back with enable_navigation_guide on");
        }

        // The pin is moved next to you rather than you to the pin, first just outside and then
        // just inside the arrival distance, and raised above your own height both times, so only a
        // horizontal measurement puts it inside. It ends in the frame navigation ends, so the
        // caller reads that frame.
        private static IEnumerator ProbeArrival(FeatureRun run, Player player, Minimap.PinData pin,
            ArrivalProbe probe)
        {
            var playerPos = player.transform.position;
            var forward = Forward(player);
            Vector3 ProbePoint(float distance)
            {
                var point = playerPos + forward * distance;
                point.y = playerPos.y + ArrivalProbeRise;
                return point;
            }

            Map.MovePin(pin, ProbePoint(OutsideArrivalDistance));
            for (var i = 0; i < OutsideFrames; i++) yield return null;
            if (!run.IsCurrent) yield break;
            probe.OutsideDistance = Utils.DistanceXZ(player.transform.position, pin.m_pos);
            probe.KeptOutside = ReferenceEquals(Navigation.TargetPin, pin);
            probe.Root = NavigationGuide.Root;
            probe.Stream = NavigationGuide.Stream;
            probe.Column = NavigationGuide.Column;
            probe.ShownOutside = probe.Root && probe.Root.activeInHierarchy;

            Map.MovePin(pin, ProbePoint(InsideArrivalDistance));
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

        // Waits for a swarm scattering since an arrival to remove itself. When it is still there
        // after the scatter and a margin, it is named for the later checks that count guide
        // objects, which then skip.
        private static IEnumerator WaitForRemoval(FeatureRun run, float since, SectionState state)
        {
            var deadline = since + NavigationGuide.ScatterTime + RemovalMargin;
            while (run.IsCurrent && Time.time < deadline && CountGuideObjects() > 0) yield return null;
            if (!run.IsCurrent || CountGuideObjects() == 0 || state.Leftover != null) yield break;

            var names = FindGuideObjects().Select(x =>
                $"'{x.name}' ({(x.gameObject.activeInHierarchy ? "active" : "inactive")}{(x.GetComponent<NavigationGuideScatter>() ? ", still scattering" : "")}; children {string.Join(", ", x.Cast<Transform>().Select(c => c.name))})");
            state.Leftover =
                $"{string.Join(", ", names)} still in the scene {Time.time - since:0.0} s after an arrival (bound {NavigationGuide.ScatterTime + RemovalMargin:0.0} s: ScatterTime + {RemovalMargin} s)";
        }

        private static Vector3 _clickPosition;
        private static int _pressedCalls;
        private static bool _clickPositionObserved;

        internal static bool ClickPin(Minimap map, Minimap.PinData pin, out string detail)
        {
            var mode = map.m_mode;
            var image = AccessTools.Field(typeof(Minimap), "m_mapImageLarge").GetValue(map);
            var uvRectProperty = AccessTools.Property(image.GetType(), "uvRect");
            var uvRect = uvRectProperty.GetValue(image, null);
            var shader = (Material)AccessTools.Field(typeof(Minimap), "m_mapLargeShader").GetValue(map);
            var shaderCenter = shader.GetVector("_mapCenter");
            var shaderZoom = shader.GetFloat("_zoom");
            var shaderPixelSize = shader.GetFloat("_pixelSize");
            var mapState = new[]
            {
                "m_mapOffset", "m_dragView", "m_namePin", "m_previousMapCenter",
                "m_previousLargeZoom", "m_pinUpdateRequired"
            }.Select(name => AccessTools.Field(typeof(Minimap), name))
                .ToDictionary(field => field, field => field.GetValue(map));
            var harmony = new Harmony("AutomaticsSmoke-navigation-click");
            try
            {
                map.SetMapMode(Minimap.MapMode.Large);
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
                var isPressed = AccessTools.Method(typeof(BepInEx.Configuration.KeyboardShortcut), "IsPressed");
                harmony.Patch(isPressed,
                    prefix: new HarmonyMethod(typeof(GuideFeatureSmoke), nameof(Pressed)));
                harmony.Patch(AccessTools.Method(typeof(Minimap), "ScreenToWorldPoint"),
                    postfix: new HarmonyMethod(typeof(GuideFeatureSmoke), nameof(ClickPosition)));
                var keyProbe = (bool)isPressed.Invoke(MappingConfig.NavigationStartKey, null);
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
                map.SetMapMode(mode);
                uvRectProperty.SetValue(image, uvRect, null);
                shader.SetVector("_mapCenter", shaderCenter);
                shader.SetFloat("_zoom", shaderZoom);
                shader.SetFloat("_pixelSize", shaderPixelSize);
                foreach (var state in mapState) state.Key.SetValue(map, state.Value);
                Map.RefreshPins();
            }
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
                run.Skip("guide_map_click_toggle", "navigation_start_key is unbound");
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

        // What the section carries from one check to the next: a guide object an arrival left
        // behind, named for the checks that count guide objects.
        private sealed class SectionState
        {
            public string Leftover;
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
            public int Frames;
            public Vector3 Target;
            public GameObject Root;
            public ParticleSystem Stream;
            public ParticleSystem Column;

            public string Describe()
            {
                return
                    $"{OutsideDistance:0.0} m away horizontally: navigation {(KeptOutside ? "kept" : "ended")}, swarm {(ShownOutside ? "shown" : "not shown")}; " +
                    $"{InsideDistance:0.0} m away horizontally and {Rise:0.0} m above you ({Distance:0.0} m in 3D; ArrivalDistance {Navigation.ArrivalDistance} m): " +
                    (Frames > 0 ? $"navigation ended in {Frames} frame(s)" : $"navigation still on after {ArrivalFrames} frames");
            }
        }

        // The burst lights a new swarm starts with, each followed by its seed: where it was first
        // seen, where it was once it had spread out and how low it went until then, and how far
        // along the line toward the target it was once it travelled with the stream and half a
        // second later.
        private sealed class BurstWatch
        {
            private readonly Dictionary<uint, Track> _tracks = new Dictionary<uint, Track>();
            private readonly HashSet<uint> _seen = new HashSet<uint>();

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
                    $"from age {TravelAge:0.0} s (RadiateTime + GatherTime + {TravelMargin} s), {grown} of {growth.Count} moved farther along the line over {TravelWatchSeconds} s (least {least:0.00} m)";
                return farthestStart <= BodyStartRadius && spread.Count > 0 && median >= MinSpreadDistance &&
                       quadrants >= MinQuadrants && behindShare >= MinBehindShare &&
                       aboveShare >= MinVerticalShare && belowShare >= MinVerticalShare && lowest >= 0f &&
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
    }
}
