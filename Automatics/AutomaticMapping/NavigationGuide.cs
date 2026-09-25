using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Automatics.AutomaticMapping
{
    /// <summary>
    ///     The swarm of small glowing lights that leads the local player toward the navigation
    ///     target. It is one local GameObject - no ZNetView, nothing networked and no vanilla prefab
    ///     instantiated - holding a single particle system and two point lights, created when the
    ///     swarm is first shown in a navigation and destroyed when the navigation ends, the module
    ///     is turned off, the local player changes or the option is turned off.
    ///     <para>
    ///         The emitter sweeps out and back along the straight line from the player toward the
    ///         target, a little above the ground, and lingers near the player, so the particles
    ///         form a dense cluster there that thins into a trail ahead. The particles are carried
    ///         along with the player, drift toward the target, and jitter and flicker through the
    ///         noise module and the alpha over their lifetime.
    ///     </para>
    /// </summary>
    internal static class NavigationGuide
    {
        internal const string ObjectName = "AutomaticsNavigationGuide";

        /// <summary>
        ///     Within this horizontal distance of the target no new light appears, and the ones
        ///     already out fade over their remaining lifetime.
        /// </summary>
        internal const float ArrivalDistance = 5f;

        /// <summary>The emitter's height above the sampled ground.</summary>
        internal const float HoverHeight = 1f;

        private const float TrailLength = 18f;
        private const float TrailStart = 0.8f;
        private const float SweepPeriod = 2.6f;
        private const int GroundSegments = 9;
        private const float GroundSampleInterval = 0.2f;

        private const int MaxParticles = 320;
        private const float EmissionRate = 100f;
        private const float MinLifetime = 1.2f;
        private const float MaxLifetime = 2.4f;
        private const float MinSize = 0.05f;
        private const float MaxSize = 0.12f;
        private const float DriftSpeed = 1.2f;
        private const float EmitRadius = 0.35f;
        private const float CarryResponse = 8f;
        private const float EmitterJumpDistance = 5f;

        private const float CoreLightDistance = 2.5f;
        private const float CoreLightIntensity = 0.8f;
        private const float CoreLightRange = 4.5f;
        private const float TrailLightShare = 0.4f;
        private const float TrailLightIntensity = 0.5f;
        private const float TrailLightRange = 3.5f;
        private const float LightFadeSpeed = 1.5f;
        private const float MaterialRetryInterval = 10f;

        // Tried in order; the first that the running game has and supports is used. When none is
        // there, the material of a campfire or torch effect is copied instead.
        private static readonly string[] ShaderNames =
        {
            "Legacy Shaders/Particles/Additive",
            "Mobile/Particles/Additive",
            "Particles/Standard Unlit"
        };

        private static readonly string[] FallbackEffectPrefabs =
        {
            "fire_pit",
            "hearth",
            "piece_groundtorch_wood"
        };

        private static readonly float[] GroundHeights = new float[GroundSegments + 1];

        private static GameObject _root;
        private static ParticleSystem _particles;
        private static Light _coreLight;
        private static Light _trailLight;
        private static Color _appliedColor;
        private static bool _emitting;
        private static bool _placed;
        private static float _sweep;
        private static float _lightLevel;
        private static Vector3 _carryVelocity;
        private static Vector3 _groundOrigin;
        private static Vector3 _groundDirection;
        private static float _groundLength;
        private static float _nextGroundSampleTime;

        private static Material _material;
        private static Texture2D _texture;
        private static float _nextMaterialAttemptTime;
        private static bool _materialWarned;

        /// <summary>
        ///     Where the particle material came from - a built-in shader or a copied vanilla effect
        ///     material - or null before the session's first swarm is created. It is logged once a
        ///     session.
        /// </summary>
        internal static string MaterialSource { get; private set; }

        internal static GameObject Root => _root;
        internal static ParticleSystem Particles => _particles;
        internal static bool Emitting => _root && _emitting;

        /// <summary>
        ///     Shows the swarm for this frame, creating it on the first call of a navigation.
        /// </summary>
        public static void Update(Player player, Vector3 targetPos)
        {
            if (!player) return;
            if (!EnsureCreated()) return;

            if (!_root.activeSelf)
            {
                _root.SetActive(true);
                _nextGroundSampleTime = 0f;
            }

            var playerPos = player.transform.position;
            var offset = targetPos - playerPos;
            offset.y = 0f;
            var distance = offset.magnitude;
            var direction = distance > 0.001f ? offset / distance : player.transform.forward;
            direction.y = 0f;
            direction.Normalize();

            var length = Mathf.Clamp(distance - 1f, TrailStart, TrailLength);
            if (Time.time >= _nextGroundSampleTime)
                SampleGround(player, playerPos, direction, length);

            var deltaTime = Time.deltaTime;
            _sweep = Mathf.Repeat(_sweep + deltaTime / SweepPeriod, 1f);
            var reach = 1f - Mathf.Abs(2f * _sweep - 1f);
            var emitterDistance = TrailStart + (length - TrailStart) * reach * reach;
            // A moving emitter spreads a frame's particles along the way it moved, so after a jump
            // - the first frame, the swarm shown again, a teleport - that frame emits nothing.
            var emitterPos = PointOnTrail(playerPos, direction, emitterDistance);
            var jumped = !_placed ||
                         (emitterPos - _particles.transform.position).sqrMagnitude >
                         EmitterJumpDistance * EmitterJumpDistance;
            _particles.transform.position = emitterPos;
            _placed = true;

            _carryVelocity = Vector3.Lerp(_carryVelocity, player.GetVelocity(),
                1f - Mathf.Exp(-CarryResponse * deltaTime));
            var main = _particles.main;
            main.emitterVelocity = _carryVelocity;

            var rise = length > 0.001f
                ? (HeightOnTrail(playerPos, direction, length) - HeightOnTrail(playerPos, direction, 0f)) / length
                : 0f;
            var velocity = _particles.velocityOverLifetime;
            velocity.x = new ParticleSystem.MinMaxCurve(direction.x * DriftSpeed);
            velocity.y = new ParticleSystem.MinMaxCurve(Mathf.Clamp(rise, -1f, 1f) * DriftSpeed);
            velocity.z = new ParticleSystem.MinMaxCurve(direction.z * DriftSpeed);

            _emitting = distance > ArrivalDistance;
            var emission = _particles.emission;
            emission.rateOverTime = _emitting && !jumped ? EmissionRate : 0f;

            ApplyColor(Config.NavigationGuideColor);
            UpdateLights(playerPos, direction, length, deltaTime);
        }

        /// <summary>
        ///     Hides the swarm while navigation cannot be shown, such as with the large map open or
        ///     the game paused. It comes back with fresh particles.
        /// </summary>
        public static void Hide()
        {
            if (_root && _root.activeSelf)
                _root.SetActive(false);
            _placed = false;
        }

        /// <summary>
        ///     Removes the swarm when a navigation ends or the option is turned off. The material
        ///     and texture stay for the next swarm of the session.
        /// </summary>
        public static void Destroy()
        {
            if (_root)
                Object.Destroy(_root);

            _root = null;
            _particles = null;
            _coreLight = null;
            _trailLight = null;
            _emitting = false;
            _placed = false;
            _sweep = 0f;
            _lightLevel = 0f;
            _carryVelocity = Vector3.zero;
            _groundLength = 0f;
            _nextGroundSampleTime = 0f;
        }

        /// <summary>
        ///     Ends the session: the swarm, and the material and texture this class made for it,
        ///     go. The vanilla material a fallback copied from is never touched. The next swarm
        ///     resolves its material again and logs it.
        /// </summary>
        public static void Dispose()
        {
            Destroy();

            if (_material)
                Object.Destroy(_material);
            if (_texture)
                Object.Destroy(_texture);

            _material = null;
            _texture = null;
            MaterialSource = null;
            _nextMaterialAttemptTime = 0f;
            _materialWarned = false;
        }

        /// <summary>
        ///     The height the swarm follows at a point: the terrain, or the sea surface where that
        ///     is higher, and <paramref name="fallback" /> where no ground height is available.
        /// </summary>
        internal static float SurfaceHeight(Vector3 point, float fallback)
        {
            var zoneSystem = ZoneSystem.instance;
            if (!zoneSystem || !zoneSystem.GetGroundHeight(point, out var height))
                return fallback;

            return Mathf.Max(height, zoneSystem.m_waterLevel);
        }

        private static bool EnsureCreated()
        {
            if (_root && _particles) return true;

            Destroy();
            var material = ResolveMaterial();
            if (!material) return false;

            // Components only on game objects this class creates and destroys itself, like the
            // navigation overlay.
            _root = new GameObject(ObjectName);
            var emitter = new GameObject("Swarm", typeof(ParticleSystem));
            emitter.transform.SetParent(_root.transform, false);
            _particles = emitter.GetComponent<ParticleSystem>();
            ConfigureParticles(_particles, material);

            _coreLight = CreateLight("CoreLight", CoreLightRange);
            _trailLight = CreateLight("TrailLight", TrailLightRange);
            _appliedColor = default;
            return true;
        }

        private static void ConfigureParticles(ParticleSystem particles, Material material)
        {
            // A new particle system starts playing; stop it so the modules can be set up.
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = particles.main;
            main.loop = true;
            main.playOnAwake = true;
            main.duration = 5f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = MaxParticles;
            main.startLifetime = new ParticleSystem.MinMaxCurve(MinLifetime, MaxLifetime);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.1f, 0.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(MinSize, MaxSize);
            main.gravityModifier = 0f;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.emitterVelocityMode = ParticleSystemEmitterVelocityMode.Custom;
            main.emitterVelocity = Vector3.zero;

            var emission = particles.emission;
            emission.enabled = true;
            emission.rateOverTime = EmissionRate;

            var shape = particles.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = EmitRadius;
            shape.randomDirectionAmount = 1f;

            // Carries every particle along with the player, so the cluster stays around a
            // running player instead of falling behind.
            var inherit = particles.inheritVelocity;
            inherit.enabled = true;
            inherit.mode = ParticleSystemInheritVelocityMode.Current;
            inherit.curveMultiplier = 1f;

            var velocity = particles.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            velocity.x = new ParticleSystem.MinMaxCurve(0f);
            velocity.y = new ParticleSystem.MinMaxCurve(0f);
            velocity.z = new ParticleSystem.MinMaxCurve(0f);

            var noise = particles.noise;
            noise.enabled = true;
            noise.separateAxes = true;
            noise.strengthX = new ParticleSystem.MinMaxCurve(0.6f);
            noise.strengthY = new ParticleSystem.MinMaxCurve(0.25f);
            noise.strengthZ = new ParticleSystem.MinMaxCurve(0.6f);
            noise.frequency = 0.9f;
            noise.scrollSpeed = new ParticleSystem.MinMaxCurve(0.7f);
            noise.octaveCount = 2;
            noise.damping = true;
            noise.quality = ParticleSystemNoiseQuality.Medium;
            noise.sizeAmount = new ParticleSystem.MinMaxCurve(0.8f);

            // The alpha rises and falls several times over a lifetime; lifetimes differ, so every
            // light flickers out of step with the others.
            var color = particles.colorOverLifetime;
            color.enabled = true;
            var flicker = new Gradient();
            flicker.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.08f),
                    new GradientAlphaKey(0.45f, 0.22f),
                    new GradientAlphaKey(1f, 0.36f),
                    new GradientAlphaKey(0.55f, 0.5f),
                    new GradientAlphaKey(0.95f, 0.66f),
                    new GradientAlphaKey(0.5f, 0.82f),
                    new GradientAlphaKey(0f, 1f)
                });
            color.color = new ParticleSystem.MinMaxGradient(flicker);

            var size = particles.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(new Keyframe(0f, 0.6f), new Keyframe(0.3f, 1f), new Keyframe(1f, 0.4f)));

            var renderer = particles.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            particles.Play(true);
        }

        private static Light CreateLight(string name, float range)
        {
            var go = new GameObject(name, typeof(Light));
            go.transform.SetParent(_root.transform, false);
            var light = go.GetComponent<Light>();
            light.type = LightType.Point;
            light.range = range;
            light.intensity = 0f;
            light.shadows = LightShadows.None;
            return light;
        }

        private static void ApplyColor(Color color)
        {
            if (color == _appliedColor) return;
            _appliedColor = color;

            var main = _particles.main;
            main.startColor = new ParticleSystem.MinMaxGradient(color, Color.Lerp(color, Color.white, 0.35f));
            if (_coreLight) _coreLight.color = color;
            if (_trailLight) _trailLight.color = color;
        }

        private static void UpdateLights(Vector3 playerPos, Vector3 direction, float length, float deltaTime)
        {
            var target = _emitting ? 1f : 0f;
            _lightLevel = Mathf.MoveTowards(_lightLevel, target, LightFadeSpeed * deltaTime);
            var time = Time.time;

            if (_coreLight)
            {
                _coreLight.transform.position =
                    PointOnTrail(playerPos, direction, Mathf.Min(CoreLightDistance, length));
                _coreLight.intensity = CoreLightIntensity * _lightLevel *
                                       (0.8f + 0.2f * Mathf.PerlinNoise(time * 1.7f, 0.3f));
            }

            if (_trailLight)
            {
                _trailLight.transform.position = PointOnTrail(playerPos, direction, length * TrailLightShare);
                _trailLight.intensity = TrailLightIntensity * _lightLevel *
                                        (0.7f + 0.3f * Mathf.PerlinNoise(time * 2.3f, 0.7f));
            }
        }

        private static Vector3 PointOnTrail(Vector3 playerPos, Vector3 direction, float distance)
        {
            var point = playerPos + direction * distance;
            point.y = HeightOnTrail(playerPos, direction, distance) + HoverHeight;
            return point;
        }

        /// <summary>
        ///     The ground height along the trail, interpolated from the last sampling. The samples
        ///     are taken at most every <see cref="GroundSampleInterval" /> seconds, so between two
        ///     samplings the player's own movement is projected onto the sampled line.
        /// </summary>
        private static float HeightOnTrail(Vector3 playerPos, Vector3 direction, float distance)
        {
            if (_groundLength <= 0.001f) return GroundHeights[0];

            var point = playerPos + direction * distance;
            var along = Vector3.Dot(point - _groundOrigin, _groundDirection);
            var index = Mathf.Clamp(along / _groundLength * GroundSegments, 0f, GroundSegments);
            var lower = Mathf.Min((int)index, GroundSegments - 1);
            return Mathf.Lerp(GroundHeights[lower], GroundHeights[lower + 1], index - lower);
        }

        private static void SampleGround(Player player, Vector3 playerPos, Vector3 direction, float length)
        {
            _nextGroundSampleTime = Time.time + GroundSampleInterval;
            _groundOrigin = playerPos;
            _groundDirection = direction;
            _groundLength = length;

            // Indoors the world terrain lies far below the player, so the swarm flies at the
            // player's height there, as it does wherever no ground height is available.
            var fallback = playerPos.y;
            var interior = player.InInterior();
            for (var i = 0; i <= GroundSegments; i++)
            {
                var point = playerPos + direction * (length * i / GroundSegments);
                GroundHeights[i] = interior ? fallback : SurfaceHeight(point, fallback);
            }
        }

        private static Material ResolveMaterial()
        {
            if (_material) return _material;
            if (Time.unscaledTime < _nextMaterialAttemptTime) return null;

            var texture = GetTexture();
            foreach (var shaderName in ShaderNames)
            {
                var shader = Shader.Find(shaderName);
                if (!shader || !shader.isSupported) continue;

                _material = new Material(shader) { name = ObjectName, mainTexture = texture };
                ConfigureAdditive(_material);
                return Resolved($"shader '{shaderName}'");
            }

            var source = FindVanillaEffectMaterial(out var prefabName, out var alreadyAdditive);
            if (source)
            {
                // Always an owned copy: the vanilla material stays as it is.
                _material = new Material(source) { name = ObjectName };
                if (_material.HasProperty("_MainTex"))
                    _material.mainTexture = texture;
                if (_material.HasProperty("_Color"))
                    _material.SetColor("_Color", Color.white);
                if (_material.HasProperty("_TintColor"))
                    _material.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
                if (!alreadyAdditive)
                    SetAdditiveBlend(_material);
                return Resolved(
                    $"a copy of material '{source.name}' (shader '{source.shader.name}') from {prefabName}{(alreadyAdditive ? "" : ", its blend set to additive")}");
            }

            // Retried a little later: the prefabs may not be loaded yet. Warned once.
            MaterialSource = null;
            _nextMaterialAttemptTime = Time.unscaledTime + MaterialRetryInterval;
            if (!_materialWarned)
                Automatics.Logger.Warning(
                    "Navigation guide: no particle shader or vanilla effect material was found; the swarm is not shown");
            _materialWarned = true;
            return null;
        }

        private static Material Resolved(string source)
        {
            if (MaterialSource != source)
                Automatics.Logger.Info($"Navigation guide particles use {source}");
            MaterialSource = source;
            return _material;
        }

        /// <summary>
        ///     Additive blending for the shaders that choose it through properties; the fixed
        ///     additive shaders ignore properties they do not have.
        /// </summary>
        private static void ConfigureAdditive(Material material)
        {
            if (material.HasProperty("_TintColor"))
                material.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", Color.white);
            if (material.HasProperty("_Mode"))
                material.SetFloat("_Mode", 4f);
            if (material.HasProperty("_BlendOp"))
                material.SetInt("_BlendOp", (int)UnityEngine.Rendering.BlendOp.Add);
            if (CanSetBlend(material))
                SetAdditiveBlend(material);
            material.DisableKeyword("_ALPHATEST_ON");
            material.EnableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.DisableKeyword("_ALPHAMODULATE_ON");
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        /// <summary>
        ///     Whether a material blends additively: a fixed additive shader, or a shader whose
        ///     blend factors are properties set to add onto the background.
        /// </summary>
        internal static bool IsAdditive(Material material)
        {
            if (!material || !material.shader) return false;
            if (material.shader.name.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return material.HasProperty("_DstBlend") &&
                   material.GetInt("_DstBlend") == (int)UnityEngine.Rendering.BlendMode.One;
        }

        private static bool CanSetBlend(Material material)
        {
            return material.HasProperty("_SrcBlend") && material.HasProperty("_DstBlend");
        }

        private static void SetAdditiveBlend(Material material)
        {
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
            if (material.HasProperty("_ZWrite"))
                material.SetInt("_ZWrite", 0);
        }

        /// <summary>
        ///     A particle material of a campfire or torch effect the GPU supports: an additive one
        ///     first, else one whose blend can be set to additive on the copy. Any other is passed
        ///     over, and with none left the caller retries later.
        /// </summary>
        private static Material FindVanillaEffectMaterial(out string prefabName, out bool additive)
        {
            prefabName = null;
            additive = false;
            var scene = ZNetScene.instance;
            if (!scene) return null;

            Material configurable = null;
            string configurablePrefab = null;
            foreach (var name in FallbackEffectPrefabs)
            {
                var prefab = scene.GetPrefab(name);
                if (!prefab) continue;

                foreach (var renderer in prefab.GetComponentsInChildren<ParticleSystemRenderer>(true))
                {
                    var material = renderer.sharedMaterial;
                    if (!material || !material.shader || !material.shader.isSupported) continue;

                    if (IsAdditive(material))
                    {
                        prefabName = name;
                        additive = true;
                        return material;
                    }

                    if (configurable || !CanSetBlend(material)) continue;
                    configurable = material;
                    configurablePrefab = name;
                }
            }

            prefabName = configurablePrefab;
            return configurable;
        }

        /// <summary>A soft round dot, white, fading to transparent at its edge.</summary>
        private static Texture2D GetTexture()
        {
            if (_texture) return _texture;

            const int size = 32;
            _texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = ObjectName,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            var pixels = new Color32[size * size];
            var half = (size - 1) * 0.5f;
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var dx = (x - half) / half;
                var dy = (y - half) / half;
                var falloff = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                var alpha = (byte)Mathf.RoundToInt(255f * falloff * falloff);
                pixels[y * size + x] = new Color32(255, 255, 255, alpha);
            }

            _texture.SetPixels32(pixels);
            _texture.Apply(false, true);
            return _texture;
        }
    }
}
