using System;
using System.Reflection;
using HarmonyLib;
using ModUtils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Automatics.AutomaticMapping
{
    internal static class Navigation
    {
        private const float TargetPinValidationInterval = 0.5f;
        private const float ObjectTopMargin = 0.4f;
        private const float UnresolvedTargetHeight = 2f;
        private const float ManualPinMapPlaneHeightEpsilon = 0.01f;
        private const float GuideRetryDelay = 5f;

        // Resolve GameCamera.m_camera through a cached field-ref delegate; the navigation
        // overlay reads it every frame and a fresh Traverse lookup each frame is wasteful.
        private static readonly AccessTools.FieldRef<GameCamera, Camera> CameraRef =
            AccessTools.FieldRefAccess<GameCamera, Camera>("m_camera");

        private static readonly Type EnemyHudNameType =
            AccessTools.Field(AccessTools.Inner(typeof(EnemyHud), "HudData"), "m_name").FieldType;

        private static readonly PropertyInfo EnemyHudNameTextProperty =
            AccessTools.Property(EnemyHudNameType, "text");

        private static Minimap.PinData _targetPin;
        private static Minimap.PinData _temporaryMarker;
        private static ZDOID _creatureId = ZDOID.None;
        private static Vector3 _lastPosition;
        private static GameObject _aimedInstance;
        private static ZDOID _aimedId = ZDOID.None;
        private static ZDOMan _zdoMan;

        private static GameObject _overlayObject;
        private static EnemyHud _overlayHud;
        private static Component _nameText;
        private static GuiBar _healthFast;
        private static GuiBar _healthSlow;
        private static GuiBar _healthFastFriendly;
        private static string _displayName = string.Empty;
        // Raw (unlocalized) pin name reference last seen, so UpdateName can skip
        // re-localizing every frame when the source m_name reference is unchanged.
        private static string _rawPinName;
        // Cache the rounded distance so UpdateName skips rebuilding the combined label
        // when both the pin name and the displayed distance are unchanged.
        private static int _displayDistanceMeters = int.MinValue;
        private static float _nextTargetPinValidationTime;
        private static float _nextGuideAttemptTime;
        private static bool _guideErrorLogged;
        private static GameObject _boundsInstance;
        private static float _boundsTopOffset;

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
            _displayName = string.Empty;
            _rawPinName = null;
            _displayDistanceMeters = int.MinValue;
            _nextTargetPinValidationTime = 0f;
            _nextGuideAttemptTime = 0f;
            _guideErrorLogged = false;
            _boundsInstance = null;
            DestroyOverlay();
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
            if (!map) return false;
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
                   takeInput && Config.NavigateToAimedObjectKey.MainKey != KeyCode.None;
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
            {
                pin = DynamicObjectMapping.GetCachedPin(aimed.UniqueId);
                if (pin == null)
                    StaticObjectMapping.TryGetCachedPin(
                        new StaticObjectMapping.MapPinIdentify(aimed.UniqueId), out pin);
            }
            if (_targetPin != null && ((pin != null && ReferenceEquals(pin, _targetPin)) ||
                (aimed.UniqueId != ZDOID.None && (aimed.UniqueId == _creatureId || aimed.UniqueId == _aimedId)) ||
                (_aimedInstance && _aimedInstance == aimed.Instance)))
            {
                ClearTarget(true);
                return;
            }

            if (pin != null)
                SetTarget(pin);
            else
            {
                ClearTarget(false);
                pin = Minimap.instance.AddPin(aimed.Position, aimed.Type, aimed.Name, false, false);
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
        }

        public static void Update(Player player)
        {
            // Stopped: the overlay and the guide swarm are hidden in the frame the stop is
            // requested, and destroyed with the rest of the session when the module is turned off.
            if (!Module.Running)
            {
                SetVisible(false);
                NavigationGuide.Hide();
                return;
            }

            EnsureDestructionSubscription();
            ValidateTargetPin();
            var targetInstance = ResolveTargetInstance();
            if (_creatureId != ZDOID.None && targetInstance)
                _lastPosition = targetInstance.transform.position;

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
                ? GetOverlayPosition(targetInstance)
                : targetPos + Vector3.up * UnresolvedTargetHeight;

            if (!EnsureOverlay())
                return;

            if (!TryGetScreenPoint(targetPos, out var screenPoint))
            {
                SetVisible(false);
                return;
            }

            UpdateName(player, targetPos);
            UpdateColor();
            UpdatePosition(screenPoint);
            SetVisible(true);
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
            return _targetPin != null &&
                   player &&
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

        private static bool CanUseEnemyHud(Character character)
        {
            return character && !character.IsBoss() && !character.m_hideHud &&
                   character != Player.m_localPlayer && EnemyHud.instance;
        }

        private static Vector3 GetOverlayPosition(GameObject instance)
        {
            if (!ReferenceEquals(instance, _boundsInstance))
            {
                _boundsInstance = instance;
                _boundsTopOffset = UnresolvedTargetHeight;

                // Visual bounds cover child meshes even when the pin is attached to the root.
                var renderers = instance.GetComponentsInChildren<Renderer>();
                var found = false;
                var bounds = new Bounds();
                foreach (var renderer in renderers)
                {
                    if (!renderer.enabled || renderer is ParticleSystemRenderer) continue;
                    if (!found)
                    {
                        bounds = renderer.bounds;
                        found = true;
                    }
                    else
                        bounds.Encapsulate(renderer.bounds);
                }

                if (found)
                    _boundsTopOffset = bounds.max.y - instance.transform.position.y + ObjectTopMargin;
            }

            return instance.transform.position + Vector3.up * _boundsTopOffset;
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
            _displayName = string.Empty;
            _rawPinName = null;
            _displayDistanceMeters = int.MinValue;
            _nextTargetPinValidationTime = 0f;
            _boundsInstance = null;
            ShowStartMessage(pinData);
        }

        private static void ClearTarget(bool showMessage)
        {
            _targetPin = null;
            RemoveTemporaryMarker();
            _creatureId = ZDOID.None;
            _lastPosition = Vector3.zero;
            _aimedInstance = null;
            _aimedId = ZDOID.None;
            _displayName = string.Empty;
            _rawPinName = null;
            _displayDistanceMeters = int.MinValue;
            _nextTargetPinValidationTime = 0f;
            _boundsInstance = null;
            DestroyOverlay();
            NavigationGuide.Destroy();
            if (showMessage)
                ShowMessage(Automatics.L10N.Localize(
                    "@message_automatic_mapping_navigation_cleared"));
        }

        private static void SetVisible(bool visible)
        {
            if (_overlayObject)
                _overlayObject.SetActive(visible);
        }

        private static bool EnsureOverlay()
        {
            var enemyHud = EnemyHud.instance;
            if (_overlayObject && _overlayHud == enemyHud && enemyHud)
                return true;

            DestroyOverlay();
            if (!enemyHud) return false;

            _overlayHud = enemyHud;
            _overlayObject = Object.Instantiate(enemyHud.m_baseHud, enemyHud.m_hudRoot.transform);
            _overlayObject.name = "AutomaticsNavigationOverlay";
            _overlayObject.SetActive(true);
            var overlay = _overlayObject.transform;
            overlay.Find("level_2").gameObject.SetActive(false);
            overlay.Find("level_3").gameObject.SetActive(false);
            overlay.Find("Alerted").gameObject.SetActive(false);
            overlay.Find("Aware").gameObject.SetActive(false);
            _nameText = overlay.Find("Name").GetComponent(EnemyHudNameType);
            _healthFast = overlay.Find("Health/health_fast").GetComponent<GuiBar>();
            _healthSlow = overlay.Find("Health/health_slow").GetComponent<GuiBar>();
            _healthFast.gameObject.SetActive(true);
            _healthFast.SetValue(1f);
            _healthSlow.SetValue(1f);
            var friendly = overlay.Find("Health/health_fast_friendly");
            if (friendly)
            {
                _healthFastFriendly = friendly.GetComponent<GuiBar>();
                _healthFastFriendly.SetValue(1f);
                friendly.gameObject.SetActive(false);
            }

            SetVisible(false);
            return true;
        }

        private static void UpdateName(Player player, Vector3 targetPos)
        {
            var raw = _targetPin?.m_name;
            var meters = Mathf.RoundToInt(Utils.DistanceXZ(player.transform.position, targetPos));
            if (ReferenceEquals(raw, _rawPinName) && meters == _displayDistanceMeters) return;

            // Localize only when the source pin name changes.
            if (!ReferenceEquals(raw, _rawPinName))
            {
                _rawPinName = raw;
                _displayName = GetDisplayPinName(_targetPin);
            }

            _displayDistanceMeters = meters;
            EnemyHudNameTextProperty.SetValue(_nameText, _displayName + " " + meters + "m");
        }

        private static void UpdateColor()
        {
            var color = Color.white;
            _healthFast.SetColor(color);
            // GuiBar colours each layer independently, including the slow bar behind the fast bar.
            _healthSlow.SetColor(color);
            if (_healthFastFriendly)
                _healthFastFriendly.SetColor(color);
        }

        private static void UpdatePosition(Vector3 screenPoint)
        {
            _overlayObject.transform.position = screenPoint;
        }

        private static bool TryGetScreenPoint(Vector3 targetPos, out Vector3 screenPoint)
        {
            screenPoint = default;
            var camera = GetWorldCamera();
            if (!camera) return false;

            var viewport = camera.WorldToViewportPoint(targetPos);
            if (viewport.z <= 0f) return false;
            if (viewport.x < 0f || viewport.x > 1f) return false;
            if (viewport.y < 0f || viewport.y > 1f) return false;

            screenPoint = camera.WorldToScreenPointScaled(targetPos);
            return true;
        }

        private static Camera GetWorldCamera()
        {
            if (GameCamera.instance)
            {
                var gameCamera = CameraRef(GameCamera.instance);
                if (gameCamera) return gameCamera;
            }

            return Camera.main;
        }

        private static void DestroyOverlay()
        {
            if (_overlayObject)
                Object.Destroy(_overlayObject);

            _overlayObject = null;
            _overlayHud = null;
            _nameText = null;
            _healthFast = null;
            _healthSlow = null;
            _healthFastFriendly = null;
            _displayDistanceMeters = int.MinValue;
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
                if (_temporaryMarker != null) Map.MovePin(_temporaryMarker, _lastPosition);
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
