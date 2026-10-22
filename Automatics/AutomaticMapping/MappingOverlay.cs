using System;
using System.Reflection;
using HarmonyLib;
using ModUtils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Automatics.AutomaticMapping
{
    internal sealed class MappingOverlay
    {
        private static readonly AccessTools.FieldRef<GameCamera, Camera> CameraRef =
            AccessTools.FieldRefAccess<GameCamera, Camera>("m_camera");
        private static readonly Type NameType =
            AccessTools.Field(AccessTools.Inner(typeof(EnemyHud), "HudData"), "m_name").FieldType;
        private static readonly PropertyInfo NameTextProperty = AccessTools.Property(NameType, "text");

        private readonly string _objectName;
        private EnemyHud _hud;
        private Component _nameText;
        private GameObject _healthGroup;
        private GuiBar _healthFast;
        private GuiBar _healthSlow;
        private GuiBar _healthFriendly;
        private string _rawName;
        private string _displayName;
        private int _meters = int.MinValue;
        private GameObject _boundsInstance;
        private float _boundsTopOffset;

        internal MappingOverlay(string objectName)
        {
            _objectName = objectName;
        }

        internal GameObject Root { get; private set; }

        internal bool Ensure()
        {
            var hud = EnemyHud.instance;
            if (Root && _hud == hud && hud) return true;
            Destroy();
            if (!hud) return false;

            _hud = hud;
            Root = Object.Instantiate(hud.m_baseHud, hud.m_hudRoot.transform);
            Root.name = _objectName;
            Root.SetActive(true);
            var overlay = Root.transform;
            overlay.Find("level_2").gameObject.SetActive(false);
            overlay.Find("level_3").gameObject.SetActive(false);
            overlay.Find("Alerted").gameObject.SetActive(false);
            overlay.Find("Aware").gameObject.SetActive(false);
            _nameText = overlay.Find("Name").GetComponent(NameType);
            _healthGroup = overlay.Find("Health").gameObject;
            _healthFast = overlay.Find("Health/health_fast").GetComponent<GuiBar>();
            _healthSlow = overlay.Find("Health/health_slow").GetComponent<GuiBar>();
            _healthFast.gameObject.SetActive(true);
            _healthFast.SetColor(Color.white);
            _healthSlow.SetColor(Color.white);
            var friendly = overlay.Find("Health/health_fast_friendly");
            if (friendly)
            {
                _healthFriendly = friendly.GetComponent<GuiBar>();
                _healthFriendly.SetColor(Color.white);
                friendly.gameObject.SetActive(false);
            }
            SetVisible(false);
            return true;
        }

        internal void Show(string rawName, float distance, Vector3 screenPoint, float health,
            bool labelOnly = false)
        {
            var meters = Mathf.RoundToInt(distance);
            if (!ReferenceEquals(rawName, _rawName) || meters != _meters)
            {
                if (!ReferenceEquals(rawName, _rawName))
                {
                    _rawName = rawName;
                    var name = (rawName ?? string.Empty).Replace("\n", " ").Trim();
                    _displayName = Localization.instance != null ? Localization.instance.Localize(name) : name;
                }
                _meters = meters;
                NameTextProperty.SetValue(_nameText, _displayName + " [" + meters + "m]");
            }
            // The label is reused between targets, so the bar goes back for the next one that has it.
            if (_healthGroup.activeSelf == labelOnly) _healthGroup.SetActive(!labelOnly);
            if (!labelOnly)
            {
                _healthFast.SetValue(health);
                _healthSlow.SetValue(health);
                if (_healthFriendly) _healthFriendly.SetValue(health);
            }
            Root.transform.position = screenPoint;
            SetVisible(true);
        }

        internal static float GetHealth(GameObject instance)
        {
            var piece = instance ? instance.GetComponent<Piece>() : null;
            var wear = piece && piece.IsPlacedByPlayer() ? piece.GetComponent<WearNTear>() : null;
            return wear ? wear.GetHealthPercentage() : 1f;
        }

        internal Vector3 GetPosition(GameObject instance)
        {
            if (instance != _boundsInstance)
            {
                _boundsInstance = instance;
                _boundsTopOffset = 2f;
                // Visual bounds cover child meshes even when the label is attached to the root.
                var found = false;
                var bounds = new Bounds();
                foreach (var renderer in instance.GetComponentsInChildren<Renderer>())
                {
                    if (!renderer.enabled || renderer is ParticleSystemRenderer) continue;
                    if (!found)
                    {
                        bounds = renderer.bounds;
                        found = true;
                    }
                    else bounds.Encapsulate(renderer.bounds);
                }
                if (found) _boundsTopOffset = bounds.max.y - instance.transform.position.y + 0.4f;
            }
            return instance.transform.position + Vector3.up * _boundsTopOffset;
        }

        internal static bool TryGetScreenPoint(Vector3 position, out Vector3 screenPoint)
        {
            screenPoint = default;
            var camera = GameCamera.instance ? CameraRef(GameCamera.instance) : Camera.main;
            if (!camera) camera = Camera.main;
            if (!camera) return false;
            var viewport = camera.WorldToViewportPoint(position);
            if (viewport.z <= 0f || viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f)
                return false;
            screenPoint = camera.WorldToScreenPointScaled(position);
            return true;
        }

        internal void SetVisible(bool visible)
        {
            if (Root) Root.SetActive(visible);
        }

        internal void Destroy()
        {
            SetVisible(false);
            if (Root) Object.Destroy(Root);
            Root = null;
            _hud = null;
            _nameText = null;
            _healthGroup = null;
            _healthFast = null;
            _healthSlow = null;
            _healthFriendly = null;
            _rawName = null;
            _displayName = string.Empty;
            _meters = int.MinValue;
            _boundsInstance = null;
        }
    }
}
