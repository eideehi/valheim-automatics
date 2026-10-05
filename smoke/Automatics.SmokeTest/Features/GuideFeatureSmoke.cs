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
    // real frames and reads it back: its particles, their height over the ground, the emitter's
    // sweep toward the target, hiding on the large map, the stop within the arrival distance, the
    // runtime option and the teardown when the navigation is cleared. It also logs which particle
    // material the swarm resolved.
    //
    // The test pin is unsaved, so no map save can keep it, and it is the only pin the section
    // removes. The navigation target, the map mode and enable_navigation_guide are put back when
    // the section ends, and by the session hand-back when the run is cut short.
    internal static class GuideFeatureSmoke
    {
        private const float TargetDistance = 60f;
        private const float InsideArrivalDistance = 4.5f;
        private const float OutsideArrivalDistance = 5.5f;
        private const float ArrivalProbeRise = 3f;
        private const float StartTimeoutSeconds = 3f;
        private const float WarmUpSeconds = 1.5f;
        private const float SweepWatchSeconds = 3f;
        private const float FadeTimeoutSeconds = 4f;
        private const float MinHeightAboveGround = NavigationGuide.HoverHeight - 0.5f;
        private const float MaxHeightAboveGround = NavigationGuide.HoverHeight + 0.5f;
        private const float RequiredShareInBand = 0.8f;

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
                testPin = map.AddPin(GroundPoint(playerPos - cameraForward * TargetDistance, playerPos.y),
                    Minimap.PinType.Icon3, "", false, false);
                if (testPin == null)
                {
                    run.Skip("guide_section", "the test pin could not be added");
                    yield break;
                }
                CheckMapClick(run, map, testPin);
                SetTargetMethod.Invoke(null, new object[] { testPin });

                // The first frames create the swarm; the next let it fill.
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

                deadline = Time.time + WarmUpSeconds;
                while (run.IsCurrent && Time.time < deadline) yield return null;
                if (!run.IsCurrent) yield break;

                CheckAlive(run, player);
                CheckHeights(run, player);

                var sweep = new SweepWatch();
                deadline = Time.time + SweepWatchSeconds;
                while (run.IsCurrent && Time.time < deadline)
                {
                    sweep.Sample(player, testPin);
                    yield return null;
                }
                if (!run.IsCurrent) yield break;
                sweep.Report(run, player, testPin);

                var steps = CheckLargeMap(run, map, originalMode);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;

                steps = CheckArrival(run, player, testPin);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;

                CheckDistanceFormat(run, player, map, testPin);
                steps = CheckOption(run);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;

                Navigation.ClearTarget();
                yield return null;
                var left = CountGuideObjects();
                if (!NavigationGuide.Root && left == 0 && TargetPinField.GetValue(null) == null)
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

        private static void CheckMaterial(FeatureRun run)
        {
            var particles = NavigationGuide.Particles;
            var renderer = particles ? particles.GetComponent<ParticleSystemRenderer>() : null;
            var material = renderer ? renderer.sharedMaterial : null;
            if (NavigationGuide.MaterialSource == null || !material || !material.shader)
            {
                run.Fail("guide_material", "the swarm has no resolved particle material");
                return;
            }

            // Additive: a fixed additive shader, or blend factors set to add onto the background.
            var shader = material.shader;
            var byName = shader.name.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0;
            var byBlend = material.HasProperty("_DstBlend") &&
                          material.GetInt("_DstBlend") == (int)UnityEngine.Rendering.BlendMode.One;
            var blend = material.HasProperty("_SrcBlend") && material.HasProperty("_DstBlend")
                ? $"src={(UnityEngine.Rendering.BlendMode)material.GetInt("_SrcBlend")} dst={(UnityEngine.Rendering.BlendMode)material.GetInt("_DstBlend")}"
                : "fixed blend";
            var detail =
                $"{NavigationGuide.MaterialSource}; renderer shader '{shader.name}', supported={shader.isSupported}, {blend}";
            if (shader.isSupported && (byName || byBlend))
                run.Pass("guide_material", detail + ", additive");
            else
                run.Fail("guide_material", detail + (shader.isSupported ? ", not additive" : ""));
        }

        private static void CheckAlive(FeatureRun run, Player player)
        {
            var root = NavigationGuide.Root;
            var particles = NavigationGuide.Particles;
            var overlay = Navigation.Label;
            if (!root || !particles)
            {
                run.Fail("guide_swarm_alive", "the guide swarm is gone");
                return;
            }

            var systems = root.GetComponentsInChildren<ParticleSystem>(true).Length;
            var lights = root.GetComponentsInChildren<Light>(true);
            var networked = root.GetComponentsInChildren<ZNetView>(true).Length;
            var labelHidden = overlay && !overlay.activeSelf;
            var alive = particles.particleCount;
            if (root.activeInHierarchy && alive >= 30 && systems == 1 && lights.Length >= 1 &&
                lights.Length <= 2 && lights.All(x => x.type == LightType.Point) && networked == 0 &&
                labelHidden && root.transform.parent == null)
                run.Pass("guide_swarm_alive",
                    $"{alive} particles alive in one particle system with {lights.Length} point light(s), no ZNetView, while the HUD label is off-screen");
            else
                run.Fail("guide_swarm_alive",
                    $"active={root.activeInHierarchy} particles={alive} systems={systems} lights={lights.Length} znetviews={networked} labelHidden={labelHidden} (overlay {(overlay ? "exists" : "missing")})");
        }

        private static void CheckHeights(FeatureRun run, Player player)
        {
            var particles = NavigationGuide.Particles;
            if (!particles)
            {
                run.Fail("guide_height_above_ground", "the guide swarm is gone");
                return;
            }

            var buffer = new ParticleSystem.Particle[particles.main.maxParticles];
            var count = particles.GetParticles(buffer);
            if (count == 0)
            {
                run.Fail("guide_height_above_ground", "no particle is alive");
                return;
            }

            var heights = new List<float>(count);
            for (var i = 0; i < count; i++)
            {
                var position = buffer[i].position;
                heights.Add(position.y - GroundPoint(position, player.transform.position.y).y);
            }
            heights.Sort();
            var inBand = heights.Count(x => x >= MinHeightAboveGround && x <= MaxHeightAboveGround);
            var share = (float)inBand / count;
            var mean = heights.Average();
            var detail =
                $"mean {mean:0.00} m; {inBand}/{count} particles between {MinHeightAboveGround} and {MaxHeightAboveGround} m above the ground or sea surface (min {heights[0]:0.00}, median {heights[count / 2]:0.00}, max {heights[count - 1]:0.00})";
            if (mean >= MinHeightAboveGround && mean <= MaxHeightAboveGround && share >= RequiredShareInBand)
                run.Pass("guide_height_above_ground", detail);
            else
                run.Fail("guide_height_above_ground", detail);
        }

        private static IEnumerator CheckLargeMap(FeatureRun run, Minimap map, Minimap.MapMode originalMode)
        {
            if (Game.m_noMap)
            {
                run.Skip("guide_hidden_on_large_map", "the world has no map, so the large map cannot open");
                yield break;
            }

            try
            {
                map.SetMapMode(Minimap.MapMode.Large);
                yield return null;
                yield return null;
                var root = NavigationGuide.Root;
                var hidden = map.m_mode == Minimap.MapMode.Large && root && !root.activeInHierarchy;
                var modeSeen = map.m_mode;
                map.SetMapMode(originalMode == Minimap.MapMode.Large ? Minimap.MapMode.Small : originalMode);
                yield return null;
                yield return null;
                root = NavigationGuide.Root;
                var shownAgain = root && root.activeInHierarchy;
                if (hidden && shownAgain)
                    run.Pass("guide_hidden_on_large_map", "hidden with the large map open and shown again once it closed");
                else
                    run.Fail("guide_hidden_on_large_map",
                        $"map mode {modeSeen}: swarm {(hidden ? "hidden" : "still shown or gone")}; after closing: {(shownAgain ? "shown" : "not shown")}");
            }
            finally
            {
                if (map && map.m_mode != originalMode) map.SetMapMode(originalMode);
            }
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

        // The pin is moved next to the player rather than the player to the pin, first just
        // outside and then just inside the arrival distance, and raised well above the ground both
        // times, so only a horizontal measurement puts it outside and then inside.
        private static IEnumerator CheckArrival(FeatureRun run, Player player, Minimap.PinData pin)
        {
            var playerPos = player.transform.position;
            var farPos = pin.m_pos;
            var forward = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
            Vector3 ProbePoint(float distance)
            {
                var point = GroundPoint(playerPos + forward * distance, playerPos.y);
                point.y += ArrivalProbeRise;
                return point;
            }

            try
            {
                Map.MovePin(pin, ProbePoint(OutsideArrivalDistance));
                // The shorter trail moves the emitter in one jump, and that frame emits nothing.
                for (var i = 0; i < 3; i++) yield return null;
                var particles = NavigationGuide.Particles;
                var outsideRate = particles ? particles.emission.rateOverTime.constant : -1f;
                var outsideDistance = Utils.DistanceXZ(player.transform.position, pin.m_pos);
                var outsideEmitting = NavigationGuide.Emitting && outsideRate > 0f;

                Map.MovePin(pin, ProbePoint(InsideArrivalDistance));
                yield return null;
                yield return null;
                particles = NavigationGuide.Particles;
                if (!particles)
                {
                    run.Fail("guide_no_spawn_on_arrival", "the guide swarm is gone near the target");
                    yield break;
                }

                var insideRate = particles.emission.rateOverTime.constant;
                var insideDistance = Utils.DistanceXZ(player.transform.position, pin.m_pos);
                var insideEmitting = NavigationGuide.Emitting;
                var stillTarget = ReferenceEquals(TargetPinField.GetValue(null), pin);
                var deadline = Time.time + FadeTimeoutSeconds;
                while (run.IsCurrent && Time.time < deadline && particles &&
                       (particles.particleCount > 0 || MaxLightIntensity() > 0f))
                    yield return null;
                if (!run.IsCurrent) yield break;
                var left = particles ? particles.particleCount : -1;
                var light = MaxLightIntensity();
                var lights = NavigationGuide.Root
                    ? NavigationGuide.Root.GetComponentsInChildren<Light>(true).Length
                    : 0;

                var observed =
                    $"target {outsideDistance:0.0} m away horizontally ({ArrivalProbeRise} m above the ground): rate {outsideRate}; target {insideDistance:0.0} m away: rate {insideRate}, then {left} particle(s) left and brightest of {lights} light(s) at {light:0.00} after the fade; navigation {(stillTarget ? "kept" : "cleared")}";
                if (outsideEmitting && insideRate == 0f && !insideEmitting && stillTarget &&
                    left == 0 && lights > 0 && light == 0f)
                    run.Pass("guide_no_spawn_on_arrival", observed);
                else
                    run.Fail("guide_no_spawn_on_arrival", observed);
            }
            finally
            {
                Map.MovePin(pin, farPos);
            }

            for (var i = 0; i < 3; i++) yield return null;
            if (NavigationGuide.Emitting && NavigationGuide.Particles &&
                NavigationGuide.Particles.emission.rateOverTime.constant > 0f)
                run.Pass("guide_resumes_after_leaving", "the swarm spawns again with the target 60 m away");
            else
                run.Fail("guide_resumes_after_leaving", "the swarm did not spawn again once the target was far away");
        }

        private static float MaxLightIntensity()
        {
            var root = NavigationGuide.Root;
            return root
                ? root.GetComponentsInChildren<Light>(true).Select(x => x.intensity).DefaultIfEmpty(0f).Max()
                : 0f;
        }

        // Turning the option off destroys the swarm; turning it back on brings it back.
        private static IEnumerator CheckOption(FeatureRun run)
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
                    yield break;
                }

                yield return null;
                yield return null;
                var left = CountGuideObjects();
                if (!NavigationGuide.Root && left == 0)
                    run.Pass("guide_option_off_destroys");
                else
                    run.Fail("guide_option_off_destroys",
                        $"{left} guide object(s) are left with enable_navigation_guide off");
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

        private static int CountGuideObjects()
        {
            return Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                         .Count(x => x && x.name == NavigationGuide.ObjectName);
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

        // The emitter's positions over one sweep, measured along the line from the player to the
        // target and across it.
        private sealed class SweepWatch
        {
            private float _nearest = float.MaxValue;
            private float _farthest = float.MinValue;
            private float _widest;
            private float _particleReach = float.MinValue;
            private int _samples;

            public void Sample(Player player, Minimap.PinData pin)
            {
                var particles = NavigationGuide.Particles;
                if (!particles || !player) return;

                var origin = player.transform.position;
                var direction = pin.m_pos - origin;
                direction.y = 0f;
                if (direction.sqrMagnitude < 0.001f) return;
                direction.Normalize();

                var offset = particles.transform.position - origin;
                offset.y = 0f;
                var along = Vector3.Dot(offset, direction);
                var across = (offset - direction * along).magnitude;
                _nearest = Mathf.Min(_nearest, along);
                _farthest = Mathf.Max(_farthest, along);
                _widest = Mathf.Max(_widest, across);
                _samples++;

                var buffer = new ParticleSystem.Particle[particles.main.maxParticles];
                var count = particles.GetParticles(buffer);
                for (var i = 0; i < count; i++)
                {
                    var particleOffset = buffer[i].position - origin;
                    particleOffset.y = 0f;
                    _particleReach = Mathf.Max(_particleReach, Vector3.Dot(particleOffset, direction));
                }
            }

            public void Report(FeatureRun run, Player player, Minimap.PinData pin)
            {
                var distance = Utils.DistanceXZ(player.transform.position, pin.m_pos);
                var detail =
                    $"over {_samples} frames the emitter ran from {_nearest:0.0} to {_farthest:0.0} m toward the target ({distance:0} m away), at most {_widest:0.00} m off the line; particles reached {_particleReach:0.0} m";
                if (_samples > 0 && _nearest <= 3f && _farthest >= 12f && _widest <= 0.5f &&
                    _particleReach >= 10f && _particleReach <= distance)
                    run.Pass("guide_advances_toward_target", detail);
                else
                    run.Fail("guide_advances_toward_target", detail);
            }
        }
    }
}
