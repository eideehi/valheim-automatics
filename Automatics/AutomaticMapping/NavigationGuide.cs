using System;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace Automatics.AutomaticMapping
{
    /// <summary>
    ///     The swarm of small glowing lights that leads the local player toward the navigation
    ///     target. It is one local GameObject - no ZNetView, nothing networked and no vanilla
    ///     prefab instantiated - holding two particle systems, the stream and the column, and two
    ///     point lights, one near the player and one at the column. It is created when the swarm is
    ///     first shown in a navigation and destroyed when a navigation starts or ends, the module
    ///     is turned off, the local player changes or the option is turned off.
    ///     <para>
    ///         When a navigation starts or switches target, a burst of the stream's lights comes out
    ///         of the player's body and spreads out around the player in every direction - up, down,
    ///         sideways and behind, the lowest staying above the player's feet - then gathers into a
    ///         stream that flows from just ahead of the player along the straight line toward the
    ///         target, a little above the ground, and fades where it ends or flows into the column at
    ///         the target. Every later light, also once the swarm is shown again after being hidden,
    ///         leaves the player's body and joins the stream directly, without spreading out. The
    ///         column's lights hover over the target like a column of midges, each circling the
    ///         column's axis, bobbing, jittering and flickering. Each light keeps its own direction,
    ///         speed and phase from its random seed; the code places it and sets its colour and alpha
    ///         every frame, and the particle systems only age and draw the lights.
    ///     </para>
    ///     <para>
    ///         When navigation ends on arrival, a shown swarm is not removed at once: it is handed
    ///         to <see cref="NavigationGuideScatter" />, and its lights fly off from the column and
    ///         fade out before the object goes. Every other end removes the swarm at once.
    ///     </para>
    /// </summary>
    internal static class NavigationGuide
    {
        internal const string ObjectName = "AutomaticsNavigationGuide";

        /// <summary>The stream's height above the sampled ground.</summary>
        internal const float HoverHeight = 1.5f;

        /// <summary>How far ahead of the player the stream reaches when the target is farther.</summary>
        internal const float TrailLength = 18f;

        /// <summary>How long a light of the burst takes to spread out from the player's body.</summary>
        internal const float RadiateTime = 0.5f;

        /// <summary>How long a spread light of the burst takes to join the stream.</summary>
        internal const float GatherTime = 0.4f;

        /// <summary>How far a stream light flies to either side of the line toward the target.</summary>
        internal const float StreamHalfWidth = 0.5f;

        /// <summary>How far a stream light flies above or below the stream's height.</summary>
        internal const float StreamHalfHeight = 0.3f;

        /// <summary>How far a stream light wanders sideways and up or down from its place.</summary>
        internal const float WobbleAmplitude = 0.2f;

        /// <summary>The farthest a column light circles from the vertical axis through the target.</summary>
        internal const float ColumnRadius = 0.8f;

        /// <summary>The height of the column's centre above the target.</summary>
        internal const float ColumnCentreHeight = 1.5f;

        /// <summary>How far a column light flies above or below the column's centre.</summary>
        internal const float ColumnHalfHeight = 1f;

        /// <summary>How far a column light jitters along each axis from its path.</summary>
        internal const float JitterAmplitude = 0.15f;

        /// <summary>How long a swarm scatters after an arrival before it is removed.</summary>
        internal const float ScatterTime = 1.2f;

        /// <summary>The longest a light waits before it flies off in a scatter.</summary>
        internal const float ScatterDelay = 0.25f;

        /// <summary>The slowest a scattering light flies off.</summary>
        internal const float MinScatterSpeed = 1.5f;

        /// <summary>The fastest a scattering light flies off.</summary>
        internal const float MaxScatterSpeed = 3.5f;

        /// <summary>How fast a scattering light slows: its speed falls as exp(-decay * t).</summary>
        internal const float ScatterDecay = 2f;

        /// <summary>
        ///     How far a scattering light's direction turns from straight out from the column's
        ///     axis. Below 1, so every light still moves outward.
        /// </summary>
        internal const float ScatterSpread = 0.6f;

        /// <summary>How much a scattering light's direction leans upward.</summary>
        internal const float ScatterLift = 0.3f;

        /// <summary>The height above the player's feet at which the stream's lights are born.</summary>
        internal const float BodyHeight = 1.1f;

        /// <summary>How much white a light's colour is mixed with at most.</summary>
        internal const float MaxWhiteMix = 0.35f;

        /// <summary>
        ///     The lifetime a light of the burst is born with: long enough that it always reaches the
        ///     end of the stream, where it is removed, before its lifetime runs out.
        /// </summary>
        internal const float BurstLifetime = RadiateTime + GatherTime + TrailLength / FlowSpeed + 0.5f;

        /// <summary>
        ///     The lifetime every other stream light is born with: long enough that it too always
        ///     reaches the end of the stream first, and shorter than <see cref="BurstLifetime" />,
        ///     which tells the two kinds apart.
        /// </summary>
        internal const float FlowLifetime = JoinTime + TrailLength / FlowSpeed + 0.5f;

        private const float MinRadiateRadius = 1.2f;
        private const float MaxRadiateRadius = 2f;
        private const float BurstFloor = 0.2f;

        // A burst light heading down sinks only this share of its reach, so even the farthest one
        // stays BurstFloor above the player's feet.
        private const float BurstDownScale = (BodyHeight - BurstFloor) / MaxRadiateRadius;

        private const float JoinTime = 0.4f;
        private const float TrailEntry = 1f;
        private const float FlowSpeed = 6f;
        private const float FadeLength = 2f;
        private const float StreamFadeInTime = 0.15f;
        private const float WobbleFrequency = 0.6f;
        private const float FollowRate = 8f;
        private const int BurstCount = 60;
        private const float StreamRate = 70f;
        private const int MaxStreamLights = 320;
        private const float MinSize = 0.05f;
        private const float MaxSize = 0.12f;

        private const int ColumnCount = 100;
        private const float ColumnFollowRate = 12f;
        private const float MinColumnLifetime = 3f;
        private const float MaxColumnLifetime = 6f;
        private const float ColumnFadeTime = 0.6f;
        private const float MinColumnSize = 0.06f;
        private const float MaxColumnSize = 0.14f;
        private const float MinOrbitSpeed = 0.6f;
        private const float MaxOrbitSpeed = 2f;
        private const float MinBobSpeed = 1.5f;
        private const float MaxBobSpeed = 3.5f;
        private const float BobShare = 0.3f;
        private const float JitterFrequency = 3f;

        private const float ScatterJitter = 0.15f;
        private const float ScatterJitterFrequency = 4f;

        private const float MinFlicker = 0.4f;
        private const float MinFlickerSpeed = 1.5f;
        private const float MaxFlickerSpeed = 4f;
        private const float TwoPi = Mathf.PI * 2f;

        private const int GroundSegments = 9;
        private const float GroundSampleInterval = 0.2f;

        private const float CoreLightDistance = 2.5f;
        private const float LightIntensity = 0.8f;
        private const float LightRange = 4.5f;
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

        // One buffer for the lights of every particle system in turn: the live swarm's two and a
        // scattering swarm's.
        private static readonly ParticleSystem.Particle[] LightBuffer =
            new ParticleSystem.Particle[Math.Max(MaxStreamLights, ColumnCount)];

        private static GameObject _root;
        private static ParticleSystem _stream;
        private static ParticleSystem _column;
        private static Light _coreLight;
        private static Light _targetLight;
        private static Color _appliedColor;
        private static bool _started;
        private static bool _burstPending;
        private static float _pendingStreamLights;
        private static float _lightLevel;
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
        internal static ParticleSystem Stream => _stream;
        internal static ParticleSystem Column => _column;

        /// <summary>
        ///     Shows the swarm for this frame, creating it on the first call of a navigation. A new
        ///     swarm begins with a burst of lights that spread out from the player's body and a full
        ///     column; one shown again after <see cref="Hide" /> begins with a full column only. Either
        ///     way the stream's later lights leave the body at a steady rate and join the stream
        ///     directly.
        /// </summary>
        public static void Update(Player player, Vector3 targetPos)
        {
            if (!player) return;
            if (!EnsureCreated()) return;

            if (!_root.activeSelf)
                _root.SetActive(true);

            var playerPos = player.transform.position;
            var offset = targetPos - playerPos;
            offset.y = 0f;
            var distance = offset.magnitude;
            var direction = distance > 0.001f ? offset / distance : player.transform.forward;
            direction.y = 0f;
            direction.Normalize();

            // The stream reaches the target when it is near enough, and flows into the column there.
            var end = Mathf.Min(distance, TrailLength);
            var endsAtTarget = distance <= TrailLength;
            var centre = targetPos + Vector3.up * ColumnCentreHeight;
            var deltaTime = Time.deltaTime;

            var burstLights = 0;
            if (!_started)
            {
                // Lights left from before the swarm was hidden would hang where it last was.
                _stream.Clear();
                _column.Clear();
                _started = true;
                _pendingStreamLights = 0f;
                _lightLevel = 0f;
                _nextGroundSampleTime = 0f;

                // Only a swarm made for this navigation bursts out; one shown again after it was
                // hidden goes on with lights that join the stream directly.
                if (_burstPending)
                    burstLights = BurstCount;
                _burstPending = false;
            }

            _pendingStreamLights += StreamRate * deltaTime;
            var flowLights = (int)_pendingStreamLights;
            _pendingStreamLights -= flowLights;

            if (Time.time >= _nextGroundSampleTime)
                SampleGround(player, playerPos, direction, end);

            // Emitted before the lights are moved, so a new light is placed and coloured before it
            // is first drawn.
            var bodyPoint = playerPos + Vector3.up * BodyHeight;
            EmitStream(bodyPoint, burstLights, BurstLifetime);
            EmitStream(bodyPoint, flowLights, FlowLifetime);
            EmitColumn(centre, ColumnCount - _column.particleCount);

            var color = Config.NavigationGuideColor;
            var time = Time.time;
            MoveStream(playerPos, direction, end, endsAtTarget, centre.y, color, time, deltaTime);
            MoveColumn(centre, color, time, deltaTime);

            ApplyColor(color);
            UpdateLights(playerPos, direction, end, centre, deltaTime);
        }

        /// <summary>
        ///     Hides the swarm while navigation cannot be shown, such as with the large map open or
        ///     the game paused. It comes back with a full column and the stream flowing from the
        ///     player again, without a burst.
        /// </summary>
        public static void Hide()
        {
            if (_root && _root.activeSelf)
                _root.SetActive(false);
            _started = false;
        }

        /// <summary>
        ///     Removes the swarm at once when a navigation starts or ends or the option is turned
        ///     off. The material and texture stay for the next swarm of the session.
        /// </summary>
        public static void Destroy()
        {
            if (_root)
                Object.Destroy(_root);
            Forget();
        }

        /// <summary>
        ///     Lets a shown swarm go when navigation ends on arrival: it is handed to
        ///     <see cref="NavigationGuideScatter" />, which scatters and fades its lights and removes
        ///     it after <see cref="ScatterTime" />, and the next navigation starts a swarm of its own.
        ///     A hidden or absent swarm is left as it is, for <see cref="Destroy" /> to remove.
        /// </summary>
        public static void Scatter(Vector3 targetPos)
        {
            if (!_root || !_root.activeSelf) return;

            _root.AddComponent<NavigationGuideScatter>().Initialize(_stream, _column, _coreLight, _targetLight,
                targetPos + Vector3.up * ColumnCentreHeight);
            Forget();
        }

        /// <summary>
        ///     Ends the session: the swarm, and the material and texture this class made for it,
        ///     go. The vanilla material a fallback copied from is never touched. The next swarm
        ///     resolves its material again and logs it. A swarm still scattering after an arrival
        ///     is not tracked here; it removes itself within <see cref="ScatterTime" />.
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
        ///     The height the stream follows at a point: the terrain, or the sea surface where that
        ///     is higher, and <paramref name="fallback" /> where no ground height is available.
        /// </summary>
        internal static float SurfaceHeight(Vector3 point, float fallback)
        {
            var zoneSystem = ZoneSystem.instance;
            if (!zoneSystem || !zoneSystem.GetGroundHeight(point, out var height))
                return fallback;

            return Mathf.Max(height, zoneSystem.m_waterLevel);
        }

        /// <summary>
        ///     Moves the lights of a scattering swarm from one moment of its scatter to the next,
        ///     both counted from the arrival. Each light waits its own delay, then flies outward from
        ///     the column's axis, slowing and zigzagging, and its alpha is scaled by
        ///     <paramref name="fadeRatio" />. Each is kept alive until the scatter ends, so none goes
        ///     out early at full alpha. No light is added.
        /// </summary>
        internal static void MoveScatteringLights(ParticleSystem system, Vector3 centre, float from, float to,
            float fadeRatio)
        {
            var remaining = ScatterTime - to;
            var count = system.GetParticles(LightBuffer);
            for (var i = 0; i < count; i++)
            {
                var light = LightBuffer[i];
                var seed = light.randomSeed;
                var delay = Hash(seed, 24) * ScatterDelay;
                var start = Mathf.Max(0f, from - delay);
                var now = Mathf.Max(0f, to - delay);
                if (now > start)
                {
                    // Straight out from the axis, turned by the light's own spread and leaning up;
                    // a light on the axis takes a direction of its own.
                    var outward = light.position - centre;
                    outward.y = 0f;
                    if (outward.sqrMagnitude > 0.0001f)
                        outward.Normalize();
                    else
                    {
                        var angle = Hash(seed, 25) * TwoPi;
                        outward = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                    }

                    var rise = Hash(seed, 26) * 2f - 1f;
                    var turn = Hash(seed, 27) * TwoPi;
                    var flat = Mathf.Sqrt(1f - rise * rise);
                    var spread = new Vector3(flat * Mathf.Cos(turn), rise, flat * Mathf.Sin(turn));
                    var direction = (outward + ScatterSpread * spread + ScatterLift * Vector3.up).normalized;

                    // The distance covered between the two moments at a speed of
                    // speed * exp(-decay * t), t counted from when the light left.
                    var speed = Mathf.Lerp(MinScatterSpeed, MaxScatterSpeed, Hash(seed, 28));
                    var travel = speed / ScatterDecay *
                                 (Mathf.Exp(-ScatterDecay * start) - Mathf.Exp(-ScatterDecay * now));
                    light.position += direction * travel + Zigzag(seed, now) - Zigzag(seed, start);
                }

                // Rounded at random, so a faint light fades as a bright one does on average instead
                // of keeping its byte value while each frame's step is under a half.
                var tint = light.startColor;
                tint.a = (byte)Mathf.Min(tint.a, Mathf.FloorToInt(tint.a * fadeRatio + Random.value));
                light.startColor = tint;
                if (light.remainingLifetime < remaining)
                    light.remainingLifetime = remaining;
                LightBuffer[i] = light;
            }

            system.SetParticles(LightBuffer, count);
        }

        private static bool EnsureCreated()
        {
            if (_root && _stream && _column) return true;

            Destroy();
            var material = ResolveMaterial();
            if (!material) return false;

            // Components only on game objects this class creates and destroys itself, like the
            // navigation overlay.
            _root = new GameObject(ObjectName);
            _stream = CreateParticles("Stream", MaxStreamLights, material);
            _column = CreateParticles("Column", ColumnCount, material);
            _coreLight = CreateLight("CoreLight");
            _targetLight = CreateLight("TargetLight");
            _appliedColor = default;
            _burstPending = true;
            return true;
        }

        /// <summary>Drops the references to the swarm and leaves the object itself as it is.</summary>
        private static void Forget()
        {
            _root = null;
            _stream = null;
            _column = null;
            _coreLight = null;
            _targetLight = null;
            _started = false;
        }

        private static ParticleSystem CreateParticles(string name, int maxParticles, Material material)
        {
            var go = new GameObject(name, typeof(ParticleSystem));
            go.transform.SetParent(_root.transform, false);
            var particles = go.GetComponent<ParticleSystem>();

            // A new particle system starts playing; stop it so the modules can be set up.
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            // The code emits, places and colours every light itself, so the system only ages and
            // draws them: it emits nothing on its own and no module moves, sizes or colours a
            // light. It keeps ageing the lights off screen too, as where a light flies follows its
            // age.
            var main = particles.main;
            main.loop = true;
            main.playOnAwake = true;
            main.duration = 5f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = maxParticles;
            main.startSpeed = 0f;
            main.gravityModifier = 0f;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;

            var emission = particles.emission;
            emission.rateOverTime = 0f;

            var shape = particles.shape;
            shape.enabled = false;

            var velocity = particles.velocityOverLifetime;
            velocity.enabled = false;
            var noise = particles.noise;
            noise.enabled = false;
            var inherit = particles.inheritVelocity;
            inherit.enabled = false;
            var color = particles.colorOverLifetime;
            color.enabled = false;
            var size = particles.sizeOverLifetime;
            size.enabled = false;

            var renderer = particles.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            particles.Play(true);
            return particles;
        }

        private static Light CreateLight(string name)
        {
            var go = new GameObject(name, typeof(Light));
            go.transform.SetParent(_root.transform, false);
            var light = go.GetComponent<Light>();
            light.type = LightType.Point;
            light.range = LightRange;
            light.intensity = 0f;
            light.shadows = LightShadows.None;
            return light;
        }

        /// <summary>
        ///     Colours both point lights. The lights of the particle systems take the colour as they
        ///     are moved each frame.
        /// </summary>
        private static void ApplyColor(Color color)
        {
            if (color == _appliedColor) return;
            _appliedColor = color;

            _coreLight.color = color;
            _targetLight.color = color;
        }

        private static void EmitStream(Vector3 bodyPoint, int count, float lifetime)
        {
            var emit = new ParticleSystem.EmitParams
            {
                position = bodyPoint,
                velocity = Vector3.zero,
                startLifetime = lifetime
            };
            for (var i = 0; i < count; i++)
            {
                emit.startSize = Random.Range(MinSize, MaxSize);
                _stream.Emit(emit, 1);
            }
        }

        private static void EmitColumn(Vector3 centre, int count)
        {
            var emit = new ParticleSystem.EmitParams { velocity = Vector3.zero };
            for (var i = 0; i < count; i++)
            {
                var point = Random.insideUnitCircle * ColumnRadius;
                emit.position = centre + new Vector3(point.x, Random.Range(-ColumnHalfHeight, ColumnHalfHeight),
                    point.y);
                emit.startLifetime = Random.Range(MinColumnLifetime, MaxColumnLifetime);
                emit.startSize = Random.Range(MinColumnSize, MaxColumnSize);
                _column.Emit(emit, 1);
            }
        }

        /// <summary>
        ///     Moves each stream light toward its place for its age. A light of the burst flies out
        ///     from the player's body along its own direction to its own distance, then into the
        ///     stream; any other light leaves the body straight along the line, taking its place
        ///     across the stream and its height as it goes. Then each flows along the line toward
        ///     the target at the stream's height, at its own place across the stream. The places
        ///     move with the player, so the stream stays ahead of a running player. A light fades
        ///     over the stream's last metres and is removed once its place is past the end.
        /// </summary>
        private static void MoveStream(Vector3 playerPos, Vector3 direction, float end, bool endsAtTarget,
            float centreHeight, Color color, float time, float deltaTime)
        {
            var bodyPoint = playerPos + Vector3.up * BodyHeight;
            var side = new Vector3(direction.z, 0f, -direction.x);
            var follow = 1f - Mathf.Exp(-FollowRate * deltaTime);
            var count = _stream.GetParticles(LightBuffer);
            var kept = 0;
            for (var i = 0; i < count; i++)
            {
                var light = LightBuffer[i];
                var seed = light.randomSeed;
                var age = light.startLifetime - light.remainingLifetime;
                var burst = IsBurstLight(light.startLifetime);
                var along = burst ? TrailEntry + FlowSpeed * Mathf.Max(0f, age - RadiateTime) : FlowSpeed * age;
                if (along > end) continue;

                var wobble = Hash(seed, 4) * 64f + time * WobbleFrequency;
                var across = (Hash(seed, 2) * 2f - 1f) * StreamHalfWidth +
                             Noise(wobble, 0.5f) * WobbleAmplitude;
                var height = HeightOnTrail(playerPos, direction, along) + HoverHeight;
                // Where the stream reaches the target, it rises or sinks into the column.
                if (endsAtTarget)
                    height = Mathf.Lerp(height, centreHeight, 1f - (end - along) / FadeLength);
                var goal = playerPos + direction * along + side * across;
                goal.y = height + (Hash(seed, 3) * 2f - 1f) * StreamHalfHeight +
                         Noise(wobble, 3.5f) * WobbleAmplitude;

                if (burst && age < RadiateTime + GatherTime)
                {
                    // A direction of its own, alike in every direction around the body; one heading
                    // down reaches less far, so it stays above the player's feet.
                    var rise = Hash(seed, 5) * 2f - 1f;
                    var angle = Hash(seed, 0) * TwoPi;
                    var flat = Mathf.Sqrt(1f - rise * rise);
                    var outward = new Vector3(flat * Mathf.Cos(angle), rise < 0f ? rise * BurstDownScale : rise,
                        flat * Mathf.Sin(angle));
                    var radius = Mathf.Lerp(MinRadiateRadius, MaxRadiateRadius, Hash(seed, 1));
                    // Out fast at first, slowing as the light reaches its own distance.
                    var spread = Mathf.Clamp01(age / RadiateTime);
                    var reach = radius * spread * (2f - spread);
                    var radiated = bodyPoint + outward * reach;
                    var gather = Mathf.SmoothStep(0f, 1f, (age - RadiateTime) / GatherTime);
                    goal = Vector3.Lerp(radiated, goal, gather);
                }
                else if (!burst && age < JoinTime)
                {
                    // Straight out of the body along the line, its place across the stream and its
                    // height growing in until it flows with the stream.
                    var join = Mathf.SmoothStep(0f, 1f, age / JoinTime);
                    goal = Vector3.Lerp(bodyPoint + direction * along, goal, join);
                }

                light.position = Vector3.Lerp(light.position, goal, follow);
                var fade = Mathf.Clamp01(age / StreamFadeInTime) * Mathf.Clamp01((end - along) / FadeLength);
                light.startColor = Tint(color, seed, fade * Flicker(seed, time));
                LightBuffer[kept++] = light;
            }

            // Only the lights kept are written back; the rest leave the system.
            _stream.SetParticles(LightBuffer, kept);
        }

        /// <summary>
        ///     Moves each column light along its own path around the column's axis - circling at
        ///     its own radius, speed and direction, bobbing at its own height and jittering - and
        ///     fades it in and out over its lifetime. The column follows the target as it moves.
        /// </summary>
        private static void MoveColumn(Vector3 centre, Color color, float time, float deltaTime)
        {
            var follow = 1f - Mathf.Exp(-ColumnFollowRate * deltaTime);
            var count = _column.GetParticles(LightBuffer);
            for (var i = 0; i < count; i++)
            {
                var light = LightBuffer[i];
                var seed = light.randomSeed;
                var age = light.startLifetime - light.remainingLifetime;

                var radius = ColumnRadius * Mathf.Sqrt(Hash(seed, 0));
                var orbitSpeed = Mathf.Lerp(MinOrbitSpeed, MaxOrbitSpeed, Hash(seed, 1));
                if (Hash(seed, 2) < 0.5f) orbitSpeed = -orbitSpeed;
                var angle = Hash(seed, 3) * TwoPi + orbitSpeed * age;
                var bob = Mathf.Sin(Hash(seed, 4) * TwoPi +
                                    Mathf.Lerp(MinBobSpeed, MaxBobSpeed, Hash(seed, 5)) * age);
                var level = (Hash(seed, 6) * 2f - 1f) * (1f - BobShare) + bob * BobShare;
                var jitter = Hash(seed, 7) * 64f + time * JitterFrequency;
                var goal = centre + new Vector3(
                    Mathf.Cos(angle) * radius + Noise(jitter, 0.5f) * JitterAmplitude,
                    level * ColumnHalfHeight + Noise(jitter, 3.5f) * JitterAmplitude,
                    Mathf.Sin(angle) * radius + Noise(jitter, 6.5f) * JitterAmplitude);

                light.position = Vector3.Lerp(light.position, goal, follow);
                var fade = Mathf.Clamp01(age / ColumnFadeTime) *
                           Mathf.Clamp01(light.remainingLifetime / ColumnFadeTime);
                light.startColor = Tint(color, seed, fade * Flicker(seed, time));
                LightBuffer[i] = light;
            }

            _column.SetParticles(LightBuffer, count);
        }

        private static void UpdateLights(Vector3 playerPos, Vector3 direction, float end, Vector3 centre,
            float deltaTime)
        {
            _lightLevel = Mathf.MoveTowards(_lightLevel, 1f, LightFadeSpeed * deltaTime);
            var time = Time.time;

            _coreLight.transform.position = PointOnTrail(playerPos, direction, Mathf.Min(CoreLightDistance, end));
            _coreLight.intensity =
                LightIntensity * _lightLevel * (0.8f + 0.2f * Mathf.PerlinNoise(time * 1.7f, 0.3f));

            _targetLight.transform.position = centre;
            _targetLight.intensity =
                LightIntensity * _lightLevel * (0.8f + 0.2f * Mathf.PerlinNoise(time * 2.3f, 0.7f));
        }

        /// <summary>
        ///     Whether a stream light came with the burst of a new swarm: told by the lifetime it was
        ///     born with, so nothing about it is kept beside the particle.
        /// </summary>
        private static bool IsBurstLight(float startLifetime)
        {
            return startLifetime > (BurstLifetime + FlowLifetime) * 0.5f;
        }

        /// <summary>A light's own slow flicker, between dim and full.</summary>
        private static float Flicker(uint seed, float time)
        {
            var speed = Mathf.Lerp(MinFlickerSpeed, MaxFlickerSpeed, Hash(seed, 16));
            return Mathf.Lerp(MinFlicker, 1f, Mathf.PerlinNoise(Hash(seed, 17) * 64f + time * speed, 2.5f));
        }

        /// <summary>
        ///     The configured colour mixed with a light's own share of white, at the given alpha.
        /// </summary>
        private static Color32 Tint(Color color, uint seed, float alpha)
        {
            var tint = Color.Lerp(color, Color.white, MaxWhiteMix * Hash(seed, 18));
            tint.a *= alpha;
            return tint;
        }

        /// <summary>A scattering light's zigzag offset at a moment, around zero on each axis.</summary>
        private static Vector3 Zigzag(uint seed, float time)
        {
            var x = Hash(seed, 29) * 64f + time * ScatterJitterFrequency;
            return new Vector3(Noise(x, 0.5f), Noise(x, 3.5f), Noise(x, 6.5f)) * ScatterJitter;
        }

        /// <summary>Smooth noise from about -1 to 1, read along one row of Perlin noise.</summary>
        private static float Noise(float x, float row)
        {
            return Mathf.PerlinNoise(x, row) * 2f - 1f;
        }

        /// <summary>
        ///     A value from 0 to just below 1 that a light keeps for as long as it lives: a hash of
        ///     its random seed, one channel for each of its own values, such as its direction, speed
        ///     or phase. Nothing about a light is kept beside the particle itself.
        /// </summary>
        private static float Hash(uint seed, int channel)
        {
            unchecked
            {
                var hash = seed ^ ((uint)channel * 0x9E3779B9u);
                hash ^= hash >> 16;
                hash *= 0x7FEB352Du;
                hash ^= hash >> 15;
                hash *= 0x846CA68Bu;
                hash ^= hash >> 16;
                return (hash >> 8) * (1f / 16777216f);
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

    /// <summary>
    ///     Drives a guide swarm that navigation let go on arrival. Every light out at that moment,
    ///     in the stream and the column, flies off from the column's axis like dispersing insects -
    ///     each after its own short delay, slowing and zigzagging - and fades out together with the
    ///     two point lights; no new light appears. The object destroys itself after
    ///     <see cref="NavigationGuide.ScatterTime" />, whatever navigation does meanwhile.
    /// </summary>
    internal sealed class NavigationGuideScatter : MonoBehaviour
    {
        private ParticleSystem _stream;
        private ParticleSystem _column;
        private Light _coreLight;
        private Light _targetLight;
        private float _coreIntensity;
        private float _targetIntensity;
        private Vector3 _centre;
        private float _startTime;
        private float _elapsed;

        internal void Initialize(ParticleSystem stream, ParticleSystem column, Light coreLight, Light targetLight,
            Vector3 centre)
        {
            _stream = stream;
            _column = column;
            _coreLight = coreLight;
            _targetLight = targetLight;
            _coreIntensity = coreLight.intensity;
            _targetIntensity = targetLight.intensity;
            _centre = centre;
            _startTime = Time.time;
        }

        private void Update()
        {
            // Checked before any light is touched, so a failing frame cannot keep the object.
            var elapsed = Time.time - _startTime;
            if (elapsed >= NavigationGuide.ScatterTime)
            {
                Destroy(gameObject);
                return;
            }

            // Each light's alpha at the arrival falls linearly to zero over the scatter: scaled by
            // how far the fade fell since the last frame, so no light's own alpha has to be kept.
            var fade = 1f - elapsed / NavigationGuide.ScatterTime;
            var fadeRatio = fade / (1f - _elapsed / NavigationGuide.ScatterTime);
            NavigationGuide.MoveScatteringLights(_stream, _centre, _elapsed, elapsed, fadeRatio);
            NavigationGuide.MoveScatteringLights(_column, _centre, _elapsed, elapsed, fadeRatio);
            _elapsed = elapsed;

            _coreLight.intensity = _coreIntensity * fade;
            _targetLight.intensity = _targetIntensity * fade;
        }
    }
}
