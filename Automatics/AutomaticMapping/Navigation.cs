using System;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    internal static class Navigation
    {
        /// <summary>
        ///     Navigation ends once the local player comes within this horizontal distance of the
        ///     target, after having been farther away during the same navigation.
        /// </summary>
        internal const float ArrivalDistance = 5f;

        private const float TargetPinValidationInterval = 0.5f;
        private const float UnresolvedTargetHeight = 2f;
        private const float ManualPinMapPlaneHeightEpsilon = 0.01f;
        private const float GuideRetryDelay = 5f;
        private const float GroundTargetRadius = 2f;
        private static readonly MappingOverlay Overlay = new MappingOverlay("AutomaticsNavigationOverlay");

        private static Minimap.PinData _targetPin;
        private static Minimap.PinData _temporaryMarker;
        private static ZDOID _creatureId = ZDOID.None;
        private static Vector3 _lastPosition;
        private static GameObject _aimedInstance;
        private static ZDOID _aimedId = ZDOID.None;
        private static ZDOMan _zdoMan;

        private static float _nextTargetPinValidationTime;
        private static float _nextGuideAttemptTime;
        private static bool _guideErrorLogged;
        private static bool _arrivalArmed;

        internal static GameObject Label => Overlay.Root;
        internal static Minimap.PinData TemporaryMarker => _temporaryMarker;
        internal static Minimap.PinData TargetPin => _targetPin;
        internal static Vector3 TargetPosition => _creatureId != ZDOID.None
            ? _lastPosition : GetNavigationTargetPosition(_targetPin);

        public static void Cleanup()
        {
            if (_zdoMan != null) _zdoMan.m_onZDODestroyed -= OnZDODestroyed;
            _zdoMan = null;
            _targetPin = null;
            RemoveTemporaryMarker();
            _creatureId = ZDOID.None;
            _lastPosition = Vector3.zero;
            _aimedInstance = null;
            _aimedId = ZDOID.None;
            _nextTargetPinValidationTime = 0f;
            _nextGuideAttemptTime = 0f;
            _guideErrorLogged = false;
            _arrivalArmed = false;
            DestroyOverlay();
            AimedObjectBar.Cleanup();
            Compass.Cleanup();
            MapFilter.Cleanup();
            PinTooltip.Cleanup();
            PinCategories.Cleanup();
            NavigationGuide.Dispose();
        }

        public static void OnRemovePin(Minimap.PinData pinData)
        {
            if (pinData == null || !ReferenceEquals(pinData, _targetPin)) return;
            if (Module.Running && _creatureId != ZDOID.None && !ReferenceEquals(pinData, _temporaryMarker))
                ReplaceMissingCreaturePin();
            else
                ClearTarget(false);
        }

        public static void ClearTarget()
        {
            ClearTarget(false);
        }

        public static bool TryHandleMapClick(Minimap map)
        {
            // False hands the click back to the game's own handling.
            if (!Module.Running) return false;
            if (!map || Minimap.InTextInput()) return false;
            if (Config.NavigationStartKey.MainKey == KeyCode.None) return false;
            if (map.m_mode != Minimap.MapMode.Large) return false;
            if (!Config.NavigationStartKey.IsPressed()) return false;

            var pos = Reflections.InvokeMethod<Vector3>(map, "ScreenToWorldPoint",
                ZInput.pointerPosition);
            var removeRadius = Reflections.GetField<float>(map, "m_removeRadius");
            var largeZoom = Reflections.GetField<float>(map, "m_largeZoom");
            var radius = removeRadius * (largeZoom * 2f);
            var pinData = Map.GetClosestPin(pos, radius);
            if (pinData == null) return false;

            if (ReferenceEquals(pinData, _targetPin))
                ClearTarget(true);
            else
                SetTarget(pinData);

            return true;
        }

        internal static void OnPlayerUpdate(Player player, bool takeInput)
        {
            if (!CanHandleAimedKey(player, takeInput)) return;
            if (!Config.NavigateToAimedObjectKey.IsDown() || !GameCamera.instance) return;
            try
            {
                var camera = GameCamera.instance.transform;
                TryNavigateFromRay(player, new Ray(camera.position, camera.forward), takeInput);
            }
            catch (Exception e)
            {
                Automatics.Logger.Error($"Failed to navigate to the object in view\n{e}");
            }
        }

        private static bool CanHandleAimedKey(Player player, bool takeInput)
        {
            return Module.Running && player && player == Player.m_localPlayer && player.IsOwner() &&
                   takeInput && !Minimap.InTextInput() && Config.NavigateToAimedObjectKey.MainKey != KeyCode.None;
        }

        internal static void TryNavigateFromRay(Player player, Ray ray, bool takeInput)
        {
            if (!CanHandleAimedKey(player, takeInput)) return;
            if (!Minimap.instance || Localization.instance == null) return;
            if (!ObjectPinning.TryResolveFromRay(player, ray, out var aimed)) return;

            Minimap.PinData pin = null;
            if (aimed.Location)
                pin = Map.GetClosestPin(aimed.Location.transform.position, aimed.Location.m_exteriorRadius,
                    candidate => Map.IsAutomaticPin(candidate) &&
                                 (candidate.m_name == aimed.StoredName || candidate.m_name == aimed.Name),
                    includeInactive: true);
            else if (aimed.UniqueId != ZDOID.None)
                pin = DynamicObjectMapping.GetCachedPin(aimed.UniqueId) ?? ObjectPinning.FindStaticPin(aimed);
            if (pin != null && Map.ShouldHideAutomaticPinIgnoringSearch(pin)) pin = null;
            if (_targetPin != null && ((pin != null && ReferenceEquals(pin, _targetPin)) ||
                (aimed.UniqueId != ZDOID.None && (aimed.UniqueId == _creatureId || aimed.UniqueId == _aimedId)) ||
                (_aimedInstance && _aimedInstance == aimed.Instance &&
                 (aimed.Component || Utils.DistanceXZ(aimed.Position, _lastPosition) <= GroundTargetRadius))))
            {
                ClearTarget(true);
                return;
            }

            if (pin != null)
                SetTarget(pin);
            else
            {
                ClearTarget(false);
                // A flora patch is one target, as it is for the pin key: the marker stands at its
                // centre under its name, not at the single plant that was looked at.
                var markerPosition = aimed.Position;
                var markerName = aimed.Name;
                if (ObjectPinning.TryGetPatchPin(aimed, out var patchPosition, out var patchName))
                {
                    markerPosition = patchPosition;
                    markerName = patchName;
                }
                pin = Minimap.instance.AddPin(markerPosition, aimed.Type, markerName, false, false);
                SetTarget(pin);
                _temporaryMarker = pin;
            }
            _aimedInstance = aimed.Instance;
            _aimedId = aimed.UniqueId;
            if (aimed.Instance.GetComponent<Character>()) _creatureId = aimed.UniqueId;
            _lastPosition = aimed.Position;
        }

        private static void RemoveTemporaryMarker()
        {
            var marker = _temporaryMarker;
            _temporaryMarker = null;
            if (marker != null && Minimap.instance) Minimap.instance.RemovePin(marker);
        }

        // A creature gets its automatic pin once it comes within the dynamic mapping range; the
        // marker would follow it as a second pin. _targetPin is set before the marker is removed,
        // as removing the marker reaches OnRemovePin, which would put a new marker in its place.
        private static void AdoptCreaturePin()
        {
            if (_temporaryMarker == null || _creatureId == ZDOID.None) return;
            var pin = DynamicObjectMapping.GetCachedPin(_creatureId);
            if (pin == null || Map.ShouldHideAutomaticPinIgnoringSearch(pin)) return;
            _targetPin = pin;
            RemoveTemporaryMarker();
            MapFilter.RefreshSearchPins();
        }

        private static void ReplaceMissingCreaturePin()
        {
            var pin = _targetPin;
            _targetPin = Minimap.instance.AddPin(_lastPosition, pin.m_type, pin.m_name, false, false);
            _temporaryMarker = _targetPin;
        }

        private static void EnsureDestructionSubscription()
        {
            if (_zdoMan == ZDOMan.instance) return;
            if (_zdoMan != null) _zdoMan.m_onZDODestroyed -= OnZDODestroyed;
            _zdoMan = ZDOMan.instance;
            if (_zdoMan != null) _zdoMan.m_onZDODestroyed += OnZDODestroyed;
        }

        private static void OnZDODestroyed(ZDO zdo)
        {
            if (!Module.Running) return;
            if (_creatureId != ZDOID.None && zdo.m_uid == _creatureId) ClearTarget(false);
        }

        internal sealed class TargetState
        {
            internal Minimap.PinData Pin;
            internal bool Temporary;
            internal ZDOID CreatureId;
            internal Vector3 LastPosition;
            internal GameObject AimedInstance;
            internal ZDOID AimedId;
        }

        internal static TargetState CaptureTarget()
        {
            return new TargetState
            {
                Pin = _targetPin,
                Temporary = _temporaryMarker != null,
                CreatureId = _creatureId,
                LastPosition = _lastPosition,
                AimedInstance = _aimedInstance,
                AimedId = _aimedId
            };
        }

        internal static void RestoreTarget(TargetState state)
        {
            ClearTarget(false);
            if (state.Pin == null) return;
            if (state.Temporary || !Map.ContainsPin(state.Pin))
            {
                _temporaryMarker = Minimap.instance.AddPin(state.LastPosition, state.Pin.m_type,
                    state.Pin.m_name, false, false);
                _targetPin = _temporaryMarker;
            }
            else
                _targetPin = state.Pin;
            _creatureId = state.CreatureId;
            _lastPosition = state.LastPosition;
            _aimedInstance = state.AimedInstance;
            _aimedId = state.AimedId;
            EnsureDestructionSubscription();
            MapFilter.RefreshSearchPins();
        }

        public static void Update(Player player)
        {
            // Stopped: the overlay and the guide swarm are hidden in the frame the stop is
            // requested, and destroyed with the rest of the session when the module is turned off.
            if (!Module.Running)
            {
                AimedObjectBar.Cleanup();
                SetVisible(false);
                NavigationGuide.Hide();
                return;
            }

            EnsureDestructionSubscription();
            ValidateTargetPin();
            AimedObjectBar.Update(player);
            var targetInstance = ResolveTargetInstance();
            if (_creatureId != ZDOID.None && targetInstance)
                _lastPosition = targetInstance.transform.position;

            UpdateArrival(player);

            // Turned off, the swarm goes whether or not navigation is shown this frame.
            if (!Config.EnableNavigationGuide)
                NavigationGuide.Destroy();

            if (!ShouldShow(player))
            {
                SetVisible(false);
                NavigationGuide.Hide();
                return;
            }

            // The guide swarm leads toward the target whether or not the target is on screen, so
            // it is driven before the label's off-screen return.
            var targetPos = TargetPosition;
            UpdateGuide(player, targetPos);

            if (CanUseEnemyHud(targetInstance ? targetInstance.GetComponent<Character>() : null))
            {
                SetVisible(false);
                return;
            }

            targetPos = targetInstance
                ? Overlay.GetPosition(targetInstance)
                : targetPos + Vector3.up * UnresolvedTargetHeight;

            if (!EnsureOverlay())
                return;

            if (!MappingOverlay.TryGetScreenPoint(targetPos, out var screenPoint))
            {
                SetVisible(false);
                return;
            }

            Overlay.Show(_targetPin.m_name, Utils.DistanceXZ(player.transform.position, targetPos),
                screenPoint, MappingOverlay.GetHealth(targetInstance ? targetInstance : _aimedInstance));
            SetVisible(true);
        }

        /// <summary>
        ///     Ends navigation when the local player reaches the target, whether or not navigation
        ///     is shown this frame and whether or not the guide swarm is on. It counts only once the
        ///     player has been farther than <see cref="ArrivalDistance" />, so a navigation started
        ///     next to its target stays on. A shown swarm scatters instead of vanishing.
        /// </summary>
        private static void UpdateArrival(Player player)
        {
            if (_targetPin == null || player != Player.m_localPlayer || !player.IsOwner()) return;

            var targetPos = TargetPosition;
            if (Utils.DistanceXZ(player.transform.position, targetPos) > ArrivalDistance)
            {
                _arrivalArmed = true;
                return;
            }

            if (!_arrivalArmed) return;
            NavigationGuide.Scatter(targetPos);
            ClearTarget(true);
        }

        /// <summary>
        ///     Drives the guide swarm inside its own boundary: a failure takes down only the swarm,
        ///     which is tried again after a delay, and the label below still updates. The first
        ///     failure of a session is logged.
        /// </summary>
        private static void UpdateGuide(Player player, Vector3 targetPos)
        {
            if (!Config.EnableNavigationGuide) return;
            if (Time.unscaledTime < _nextGuideAttemptTime) return;

            try
            {
                NavigationGuide.Update(player, targetPos);
            }
            catch (Exception e)
            {
                _nextGuideAttemptTime = Time.unscaledTime + GuideRetryDelay;
                try
                {
                    NavigationGuide.Destroy();
                }
                catch (Exception)
                {
                    // The swarm is recreated from scratch at the next attempt either way.
                }

                if (_guideErrorLogged) return;
                _guideErrorLogged = true;
                Automatics.Logger.Error(
                    $"Navigation guide failed and is retried every {GuideRetryDelay}s: {e}");
            }
        }

        private static bool ShouldShow(Player player)
        {
            return _targetPin != null && CanShowHud(player);
        }

        internal static bool CanShowHud(Player player)
        {
            return player &&
                   player == Player.m_localPlayer &&
                   player.IsOwner() &&
                   !Game.IsPaused() &&
                   Hud.instance &&
                   !Hud.IsUserHidden() &&
                   Minimap.instance &&
                   Minimap.instance.m_mode != Minimap.MapMode.Large;
        }

        internal static Character GetHudTarget()
        {
            if (!Module.Running || !ShouldShow(Player.m_localPlayer) || !EnemyHud.instance)
                return null;

            var instance = ResolveTargetInstance();
            var character = instance ? instance.GetComponent<Character>() : null;
            return CanUseEnemyHud(character) ? character : null;
        }

        private static GameObject ResolveTargetInstance()
        {
            if (_creatureId != ZDOID.None)
                return ZNetScene.instance ? ZNetScene.instance.FindInstance(_creatureId) : null;
            var instance = DynamicObjectMapping.FindLiveInstance(_targetPin);
            if (!instance)
                instance = StaticObjectMapping.FindLiveInstance(_targetPin);
            return instance;
        }

        internal static bool CanUseEnemyHud(Character character)
        {
            return character && !character.IsBoss() && !character.m_hideHud &&
                   character != Player.m_localPlayer && EnemyHud.instance;
        }

        internal static bool ShowsInstance(GameObject instance)
        {
            return ShouldShow(Player.m_localPlayer) &&
                   ((_aimedInstance && _aimedInstance == instance) || ResolveTargetInstance() == instance);
        }

        private static void SetTarget(Minimap.PinData pinData)
        {
            _targetPin = null;
            _creatureId = ZDOID.None;
            RemoveTemporaryMarker();
            _targetPin = pinData;
            _creatureId = DynamicObjectMapping.GetCreatureId(pinData);
            _lastPosition = pinData.m_pos;
            _aimedInstance = null;
            _aimedId = ZDOID.None;
            EnsureDestructionSubscription();
            _nextTargetPinValidationTime = 0f;
            _arrivalArmed = false;
            // Each navigation starts its own swarm from the player, at its own target.
            NavigationGuide.Destroy();
            ShowStartMessage(pinData);
            MapFilter.RefreshSearchPins();
        }

        private static void ClearTarget(bool showMessage)
        {
            _targetPin = null;
            RemoveTemporaryMarker();
            _creatureId = ZDOID.None;
            _lastPosition = Vector3.zero;
            _aimedInstance = null;
            _aimedId = ZDOID.None;
            _nextTargetPinValidationTime = 0f;
            _arrivalArmed = false;
            DestroyOverlay();
            NavigationGuide.Destroy();
            MapFilter.RefreshSearchPins();
            if (showMessage)
                ShowMessage(Automatics.L10N.Localize(
                    "@message_automatic_mapping_navigation_cleared"));
        }

        private static void SetVisible(bool visible)
        {
            Overlay.SetVisible(visible);
        }

        private static bool EnsureOverlay()
        {
            return Overlay.Ensure();
        }

        private static void DestroyOverlay()
        {
            Overlay.Destroy();
        }

        private static void ShowStartMessage(Minimap.PinData pinData)
        {
            var pinName = GetDisplayPinName(pinData);

            ShowMessage(string.IsNullOrEmpty(pinName)
                ? Automatics.L10N.Localize("@message_automatic_mapping_navigation_started")
                : Automatics.L10N.Localize("@message_automatic_mapping_navigation_started_to",
                    pinName));
        }

        private static void ShowMessage(string message)
        {
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, message);
        }

        private static void ValidateTargetPin()
        {
            if (_targetPin == null) return;
            AdoptCreaturePin();
            if (Time.time < _nextTargetPinValidationTime) return;

            _nextTargetPinValidationTime = Time.time + TargetPinValidationInterval;
            if (_creatureId != ZDOID.None)
            {
                var instance = ResolveTargetInstance();
                var character = instance ? instance.GetComponent<Character>() : null;
                if (character && character.IsDead())
                {
                    ClearTarget(false);
                    return;
                }
                if (instance) _lastPosition = instance.transform.position;
                if (!Map.ContainsPin(_targetPin)) ReplaceMissingCreaturePin();
                if (_temporaryMarker != null && _temporaryMarker.m_pos != _lastPosition)
                {
                    Map.MovePin(_temporaryMarker, _lastPosition);
                    // MovePin only writes the position, so ask the map to redraw the marker.
                    Map.RefreshPins();
                }
            }
            else if (!Map.ContainsPin(_targetPin) || Map.ShouldHideAutomaticPin(_targetPin))
                ClearTarget(false);
        }

        private static string GetDisplayPinName(Minimap.PinData pinData)
        {
            var pinName = pinData?.m_name;
            if (string.IsNullOrEmpty(pinName))
                return string.Empty;

            pinName = pinName.Replace("\n", " ").Trim();
            return Localization.instance != null
                ? Localization.instance.Localize(pinName)
                : pinName;
        }

        private static Vector3 GetNavigationTargetPosition(Minimap.PinData pinData)
        {
            if (pinData == null) return Vector3.zero;

            var pos = pinData.m_pos;
            if (!ShouldResolveTerrainHeight(pinData)) return pos;
            return TryResolveTerrainHeight(pos, out var height)
                ? new Vector3(pos.x, height, pos.z)
                : pos;
        }

        private static bool ShouldResolveTerrainHeight(Minimap.PinData pinData)
        {
            return pinData.m_save &&
                   Mathf.Abs(pinData.m_pos.y) <= ManualPinMapPlaneHeightEpsilon;
        }

        private static bool TryResolveTerrainHeight(Vector3 pos, out float height)
        {
            try
            {
                if (Heightmap.GetHeight(pos, out height))
                    return true;

                if (WorldGenerator.instance != null)
                {
                    height = WorldGenerator.instance.GetHeight(pos.x, pos.z);
                    return true;
                }
            }
            catch (Exception)
            {
                // Fall through to the original pin position if Valheim cannot
                // resolve terrain height in the current world state.
            }

            height = pos.y;
            return false;
        }
    }
}
