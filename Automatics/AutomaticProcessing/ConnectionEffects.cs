using System.Collections.Generic;
using System.Linq;
using ModUtils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Automatics.AutomaticProcessing
{
    internal static class ConnectionEffects
    {
        private static readonly int ColorPropertyId = Shader.PropertyToID("_Color");
        private static readonly int TintColorPropertyId = Shader.PropertyToID("_TintColor");
        private static readonly int EmissionColorPropertyId =
            Shader.PropertyToID("_EmissionColor");

        private const float RefreshInterval = 0.75f;
        private const float FireplaceEffectHeight = 1.2f;
        private const float TurretEffectHeight = 1.4f;
        private const float ContainerTopInset = 0.05f;

        private static readonly Dictionary<int, ConnectionHelper> ActiveHelpers;

        private static int _currentProcessorId;
        private static float _nextRefreshTime;
        private static ConnectionTemplate _template;

        static ConnectionEffects()
        {
            ActiveHelpers = new Dictionary<int, ConnectionHelper>();
        }

        public static void Cleanup()
        {
            foreach (var id in ActiveHelpers.Keys.ToList())
                RemoveHelper(id);

            ActiveHelpers.Clear();
            _currentProcessorId = 0;
            _nextRefreshTime = 0f;
        }

        public static void Update(Player player)
        {
            if (!Config.EnableAutomaticProcessing || Game.IsPaused())
            {
                Cleanup();
                return;
            }

            var hovering = Reflections.GetField<GameObject>(player, "m_hovering");
            if (!hovering)
            {
                Cleanup();
                return;
            }

            if (!TryGetProcessorContext(hovering, out var context))
            {
                Cleanup();
                return;
            }

            if (Config.ContainerSearchRange(context.Name) <= 0f || !HasSourceProcess(context.Name))
            {
                Cleanup();
                return;
            }

            if (_currentProcessorId != context.Id || Time.time >= _nextRefreshTime)
                Refresh(context);
        }

        private static bool HasSourceProcess(string processorName)
        {
            return (Config.AllowProcessing(processorName) &
                    (Process.Craft | Process.Refuel | Process.Charge)) != 0;
        }

        private static void Refresh(ProcessorContext context)
        {
            var template = GetTemplate();
            if (!template)
            {
                Cleanup();
                return;
            }

            var activeIds = new HashSet<int>();
            foreach (var (container, _) in Logics.GetNearbyContainers(context.Name, context.SearchOrigin))
            {
                if (!container) continue;

                var containerId = container.GetInstanceID();
                activeIds.Add(containerId);

                if (!ActiveHelpers.TryGetValue(containerId, out var helper) ||
                    helper.Root == null || !helper.ConnectionPrefab)
                {
                    helper = CreateHelper(template, container);
                    if (helper == null) continue;
                    ActiveHelpers[containerId] = helper;
                }

                PositionHelper(helper, container);
                UpdateConnectionEffect(helper, context.EffectPoint);
            }

            foreach (var id in ActiveHelpers.Keys.Where(x => !activeIds.Contains(x)).ToList())
                RemoveHelper(id);

            _currentProcessorId = context.Id;
            _nextRefreshTime = Time.time + RefreshInterval;
        }

        private static void RemoveHelper(int containerId)
        {
            if (!ActiveHelpers.TryGetValue(containerId, out var helper))
                return;

            if (helper.Connection)
                Object.Destroy(helper.Connection);
            if (helper.Root)
                Object.Destroy(helper.Root);

            ActiveHelpers.Remove(containerId);
        }

        private static ConnectionHelper CreateHelper(ConnectionTemplate template, Container container)
        {
            var root = new GameObject("Automatics_StorageConnectionHelper");
            if (!root || !template.ConnectionPrefab)
            {
                if (root)
                    Object.Destroy(root);
                return null;
            }

            root.hideFlags = HideFlags.HideAndDontSave;
            var helper = new ConnectionHelper(root, template.ConnectionPrefab,
                template.ConnectionOffset);
            PositionHelper(helper, container);
            return helper;
        }

        private static void PositionHelper(ConnectionHelper helper, Container container)
        {
            var connectionPoint = GetContainerConnectionPoint(container);
            helper.Root.transform.SetPositionAndRotation(connectionPoint - helper.ConnectionOffset,
                Quaternion.identity);
        }

        private static Vector3 GetContainerConnectionPoint(Container container)
        {
            if (TryGetContainerBounds(container, out var bounds))
            {
                var topY = Mathf.Max(bounds.min.y, bounds.max.y - ContainerTopInset);
                return new Vector3(bounds.center.x, topY, bounds.center.z);
            }

            return container.transform.position + Vector3.up * 0.5f;
        }

        private static bool TryGetContainerBounds(Container container, out Bounds bounds)
        {
            var initialized = false;
            bounds = default;

            foreach (var renderer in container.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled) continue;

                if (!initialized)
                {
                    bounds = renderer.bounds;
                    initialized = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            foreach (var collider in container.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled) continue;

                if (!initialized)
                {
                    bounds = collider.bounds;
                    initialized = true;
                }
                else
                {
                    bounds.Encapsulate(collider.bounds);
                }
            }

            return initialized;
        }

        private static void UpdateConnectionEffect(ConnectionHelper helper, Vector3 effectPoint)
        {
            var connectionPoint = helper.Root.transform.TransformPoint(helper.ConnectionOffset);
            var direction = effectPoint - connectionPoint;
            var distance = direction.magnitude;
            if (distance <= Mathf.Epsilon)
                return;

            if (!helper.Connection)
                helper.Connection = Object.Instantiate(helper.ConnectionPrefab, connectionPoint,
                    Quaternion.identity);

            var connectionTransform = helper.Connection.transform;
            connectionTransform.SetPositionAndRotation(connectionPoint,
                Quaternion.LookRotation(direction.normalized));
            connectionTransform.localScale = new Vector3(1f, 1f, distance);

            ApplyConnectionColor(helper, Config.StorageConnectionEffectColor);
        }

        private static void ApplyConnectionColor(ConnectionHelper helper, Color color)
        {
            if (!helper.Connection)
                return;
            if (helper.ColorApplied && helper.LastAppliedColor == color)
                return;

            foreach (var lineRenderer in helper.Connection.GetComponentsInChildren<LineRenderer>(true))
            {
                lineRenderer.startColor = color;
                lineRenderer.endColor = color;
            }

            foreach (var trailRenderer in helper.Connection.GetComponentsInChildren<TrailRenderer>(true))
            {
                trailRenderer.startColor = color;
                trailRenderer.endColor = color;
            }

            foreach (var particleSystem in helper.Connection.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = particleSystem.main;
                main.startColor = color;
            }

            foreach (var renderer in helper.Connection.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var material in renderer.materials)
                {
                    if (material.HasProperty(ColorPropertyId))
                        material.SetColor(ColorPropertyId, color);
                    if (material.HasProperty(TintColorPropertyId))
                        material.SetColor(TintColorPropertyId, color);
                    if (material.HasProperty(EmissionColorPropertyId))
                        material.SetColor(EmissionColorPropertyId, color);
                }
            }

            helper.LastAppliedColor = color;
            helper.ColorApplied = true;
        }

        private static ConnectionTemplate GetTemplate()
        {
            if (_template)
                return _template;
            if (!ZNetScene.instance)
                return null;

            ConnectionTemplate fallback = null;
            foreach (var prefab in ZNetScene.instance.m_prefabs)
            {
                if (!prefab) continue;

                var extension = prefab
                    .GetComponentsInChildren<StationExtension>(true)
                    .FirstOrDefault(x => x && x.m_connectionPrefab && x.GetComponent<ZNetView>());
                if (!extension) continue;

                if (!fallback)
                    fallback = new ConnectionTemplate(extension.m_connectionPrefab,
                        extension.m_connectionOffset);

                if (extension.m_craftingStation &&
                    extension.m_craftingStation.m_name == "$piece_forge")
                {
                    _template = new ConnectionTemplate(extension.m_connectionPrefab,
                        extension.m_connectionOffset);
                    return _template;
                }
            }

            _template = fallback;
            return _template;
        }

        private static bool TryGetProcessorContext(GameObject hovering,
            out ProcessorContext context)
        {
            var smelter = hovering.GetComponentInParent<Smelter>();
            if (smelter)
            {
                var origin = smelter.transform.position;
                var effectPoint = smelter.m_outputPoint
                    ? smelter.m_outputPoint.position
                    : origin + Vector3.up;
                context = new ProcessorContext(smelter.GetInstanceID(), smelter.m_name, origin,
                    effectPoint);
                return true;
            }

            var cookingStation = hovering.GetComponentInParent<CookingStation>();
            if (cookingStation)
            {
                var origin = cookingStation.transform.position;
                var effectPoint = cookingStation.m_spawnPoint
                    ? cookingStation.m_spawnPoint.position
                    : origin + Vector3.up;
                context = new ProcessorContext(cookingStation.GetInstanceID(),
                    cookingStation.m_name, origin, effectPoint);
                return true;
            }

            var fermenter = hovering.GetComponentInParent<Fermenter>();
            if (fermenter)
            {
                var origin = fermenter.transform.position;
                var effectPoint = fermenter.m_outputPoint
                    ? fermenter.m_outputPoint.position
                    : origin + Vector3.up;
                context = new ProcessorContext(fermenter.GetInstanceID(), fermenter.m_name, origin,
                    effectPoint);
                return true;
            }

            var fireplace = hovering.GetComponentInParent<Fireplace>();
            if (fireplace)
            {
                var origin = fireplace.transform.position;
                var piece = fireplace.GetComponent<Piece>();
                context = new ProcessorContext(fireplace.GetInstanceID(),
                    piece ? piece.m_name : fireplace.m_name, origin,
                    origin + Vector3.up * FireplaceEffectHeight);
                return true;
            }

            var turret = hovering.GetComponentInParent<Turret>();
            if (turret)
            {
                var origin = turret.transform.position;
                context = new ProcessorContext(turret.GetInstanceID(), turret.m_name, origin,
                    origin + Vector3.up * TurretEffectHeight);
                return true;
            }

            context = default;
            return false;
        }

        private readonly struct ProcessorContext
        {
            public ProcessorContext(int id, string name, Vector3 searchOrigin, Vector3 effectPoint)
            {
                Id = id;
                Name = name;
                SearchOrigin = searchOrigin;
                EffectPoint = effectPoint;
            }

            public int Id { get; }
            public string Name { get; }
            public Vector3 SearchOrigin { get; }
            public Vector3 EffectPoint { get; }
        }

        private sealed class ConnectionHelper
        {
            public ConnectionHelper(GameObject root, GameObject connectionPrefab,
                Vector3 connectionOffset)
            {
                Root = root;
                ConnectionPrefab = connectionPrefab;
                ConnectionOffset = connectionOffset;
            }

            public GameObject Root { get; }
            public GameObject Connection { get; set; }
            public GameObject ConnectionPrefab { get; }
            public Vector3 ConnectionOffset { get; }
            public bool ColorApplied { get; set; }
            public Color LastAppliedColor { get; set; }
        }

        private sealed class ConnectionTemplate
        {
            public ConnectionTemplate(GameObject connectionPrefab, Vector3 connectionOffset)
            {
                ConnectionPrefab = connectionPrefab;
                ConnectionOffset = connectionOffset;
            }

            public GameObject ConnectionPrefab { get; }
            public Vector3 ConnectionOffset { get; }

            public static implicit operator bool(ConnectionTemplate template)
            {
                return template != null && template.ConnectionPrefab;
            }
        }
    }
}
