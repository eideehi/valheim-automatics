using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Automatics.AutomaticMapping
{
    /// <summary>
    ///     A strip at the top centre of the HUD with the cardinal directions and the icons of the
    ///     nearby map pins, each at its bearing from the player. It shows the pins the minimap
    ///     would draw, without the minimap's viewport: the icon filters, the shared-map fade and
    ///     the automatic-pin filters apply, area pins are left out, and so is every pin beyond
    ///     <see cref="Config.CompassPinRange" />.
    ///     <para>
    ///         The pins in range are collected and sorted by distance on a timer. Every frame
    ///         then places the nearest of them that lie within the half view, so turning shows
    ///         the pins ahead at once, and the cap applies to what is in view, not to the pins
    ///         behind the player.
    ///     </para>
    /// </summary>
    internal static class Compass
    {
        internal const string ObjectName = "AutomaticsCompass";

        // Design choices, to be tuned by eye: the angle the strip covers, its size and place in
        // canvas units, the icon and letter size, how many pins it shows and how often it
        // collects them.
        internal const float FieldOfView = 180f;
        internal const float Width = 600f;
        private const float Height = 36f;
        private const float TopMargin = 8f;
        private const float IconSize = 22f;
        private const float LetterSize = 20f;
        internal const int MarkerCount = 24;
        private const float RefreshInterval = 0.25f;
        private static readonly Color BackgroundColor = new Color(0f, 0f, 0f, 0.4f);

        private const float RetryDelay = 5f;

        private static readonly string[] LetterKeys =
        {
            "@text_automatic_mapping_compass_north", "@text_automatic_mapping_compass_east",
            "@text_automatic_mapping_compass_south", "@text_automatic_mapping_compass_west"
        };

        private static readonly Type TextType =
            AccessTools.Field(AccessTools.Inner(typeof(EnemyHud), "HudData"), "m_name").FieldType;
        private static readonly PropertyInfo TextProperty = AccessTools.Property(TextType, "text");
        private static readonly PropertyInfo FontSizeProperty = AccessTools.Property(TextType, "fontSize");
        private static readonly PropertyInfo AutoSizeProperty = AccessTools.Property(TextType, "enableAutoSizing");
        private static readonly PropertyInfo AlignmentProperty = AccessTools.Property(TextType, "alignment");
        private static readonly AccessTools.FieldRef<Minimap, bool[]> VisibleIconTypes =
            AccessTools.FieldRefAccess<Minimap, bool[]>("m_visibleIconTypes");
        private static readonly AccessTools.FieldRef<Minimap, float> SharedMapDataFade =
            AccessTools.FieldRefAccess<Minimap, float>("m_sharedMapDataFade");

        private static readonly List<Candidate> Candidates = new List<Candidate>();
        private static readonly List<Minimap.PinData> Shown = new List<Minimap.PinData>(MarkerCount);
        private static readonly Image[] Markers = new Image[MarkerCount];
        private static readonly RectTransform[] LetterRects = new RectTransform[LetterKeys.Length];
        private static Hud _hud;
        private static float _refreshAt;
        private static float _nextAttemptTime;
        private static bool _errorLogged;

        private struct Candidate
        {
            internal Minimap.PinData Pin;
            internal float DistanceSq;
        }

        internal static GameObject Root { get; private set; }

        /// <summary>The pins shown this frame, nearest first; marker i shows pin i.</summary>
        internal static IReadOnlyList<Minimap.PinData> ShownPins => Shown;

        /// <summary>The north, east, south and west letters, in that order.</summary>
        internal static IReadOnlyList<RectTransform> Letters => LetterRects;

        internal static RectTransform GetMarker(int index)
        {
            return Markers[index] ? Markers[index].rectTransform : null;
        }

        internal static void Update(Player player)
        {
            // Turned off or stopped, the strip goes in the same frame, as the looked-at object
            // bar does.
            if (!Module.Running || !Config.EnableCompass)
            {
                DestroyStrip();
                return;
            }
            if (Time.unscaledTime < _nextAttemptTime) return;

            try
            {
                UpdateStrip(player);
            }
            catch (Exception e)
            {
                _nextAttemptTime = Time.unscaledTime + RetryDelay;
                try
                {
                    DestroyStrip();
                }
                catch (Exception)
                {
                    // The next attempt recreates the strip either way.
                }
                if (_errorLogged) return;
                _errorLogged = true;
                Automatics.Logger.Error($"Compass failed and is retried every {RetryDelay}s: {e}");
            }
        }

        // The small minimap is what is missing while the player is dead or in a world without a
        // map, so its mode covers both.
        private static void UpdateStrip(Player player)
        {
            var map = Minimap.instance;
            if (!Navigation.CanShowHud(player) || map.m_mode != Minimap.MapMode.Small ||
                !GameCamera.instance || !EnemyHud.instance)
            {
                SetVisible(false);
                return;
            }
            if (!Root || _hud != Hud.instance) Create(Hud.instance, EnemyHud.instance);

            var origin = player.transform.position;
            if (Time.unscaledTime >= _refreshAt)
            {
                _refreshAt = Time.unscaledTime + RefreshInterval;
                CollectCandidates(map, origin);
            }

            var heading = GameCamera.instance.transform.eulerAngles.y;
            for (var i = 0; i < LetterRects.Length; i++)
                Place(LetterRects[i], heading, i * 90f);
            PlaceMarkers(origin, heading);
            SetVisible(true);
        }

        // The rule vanilla's UpdatePins draws a pin by, without its viewport test, then the
        // automatic-pin filters and the range. Nothing is capped here.
        private static void CollectCandidates(Minimap map, Vector3 origin)
        {
            Candidates.Clear();
            var pins = Map.GetAllPins();
            var visibleTypes = VisibleIconTypes(map);
            var shared = SharedMapDataFade(map) > 0f;
            var range = Config.CompassPinRange;
            var rangeSq = range * range;
            for (var i = 0; i < pins.Count; i++)
            {
                var pin = pins[i];
                if (pin == null || !pin.m_icon) continue;
                if (pin.m_worldSize > 0f || pin.m_type == Minimap.PinType.EventArea) continue;
                var type = (int)pin.m_type;
                if (visibleTypes == null || type < 0 || type >= visibleTypes.Length || !visibleTypes[type]) continue;
                if (pin.m_ownerID != 0L && !shared) continue;
                var dx = pin.m_pos.x - origin.x;
                var dz = pin.m_pos.z - origin.z;
                var distanceSq = dx * dx + dz * dz;
                if (distanceSq > rangeSq) continue;
                if (Map.ShouldHideAutomaticPin(pin)) continue;
                Candidates.Add(new Candidate { Pin = pin, DistanceSq = distanceSq });
            }
            Candidates.Sort(CandidateOrder.Instance);
        }

        // Marker 0 is the last sibling, so the nearest pin is drawn on top.
        private static void PlaceMarkers(Vector3 origin, float heading)
        {
            Shown.Clear();
            for (var i = 0; i < Candidates.Count && Shown.Count < MarkerCount; i++)
            {
                var pin = Candidates[i].Pin;
                var bearing = Mathf.Atan2(pin.m_pos.x - origin.x, pin.m_pos.z - origin.z) * Mathf.Rad2Deg;
                if (!TryGetOffset(heading, bearing, out var x)) continue;

                var marker = Markers[Shown.Count];
                if (marker.sprite != pin.m_icon) marker.sprite = pin.m_icon;
                marker.rectTransform.anchoredPosition = new Vector2(x, 0f);
                if (!marker.gameObject.activeSelf) marker.gameObject.SetActive(true);
                Shown.Add(pin);
            }
            for (var i = Shown.Count; i < MarkerCount; i++)
                if (Markers[i].gameObject.activeSelf) Markers[i].gameObject.SetActive(false);
        }

        private static void Place(RectTransform rect, float heading, float bearing)
        {
            var shown = TryGetOffset(heading, bearing, out var x);
            if (shown) rect.anchoredPosition = new Vector2(x, 0f);
            if (rect.gameObject.activeSelf != shown) rect.gameObject.SetActive(shown);
        }

        // Heading and bearing in degrees clockwise from north (+Z), so east (+X) is 90.
        private static bool TryGetOffset(float heading, float bearing, out float x)
        {
            var delta = Mathf.DeltaAngle(heading, bearing);
            x = delta / (FieldOfView / 2f) * (Width / 2f);
            return Mathf.Abs(delta) <= FieldOfView / 2f;
        }

        // Under the HUD's root, which the game moves off-screen while the HUD is hidden or a
        // cutscene plays.
        private static void Create(Hud hud, EnemyHud enemyHud)
        {
            DestroyStrip();
            var root = new GameObject(ObjectName, typeof(RectTransform));
            try
            {
                var strip = (RectTransform)root.transform;
                strip.SetParent(hud.m_rootObject.transform, false);
                strip.anchorMin = strip.anchorMax = new Vector2(0.5f, 1f);
                strip.pivot = new Vector2(0.5f, 1f);
                strip.sizeDelta = new Vector2(Width, Height);
                strip.anchoredPosition = new Vector2(0f, -TopMargin);
                var background = root.AddComponent<Image>();
                background.color = BackgroundColor;
                background.raycastTarget = false;

                var source = enemyHud.m_baseHud.transform.Find("Name").gameObject;
                for (var i = 0; i < LetterRects.Length; i++)
                {
                    var letter = Object.Instantiate(source, strip);
                    letter.name = "Letter";
                    letter.SetActive(false);
                    foreach (var localize in letter.GetComponentsInChildren<Localize>(true)) localize.enabled = false;
                    foreach (var graphic in letter.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
                    var text = letter.GetComponent(TextType);
                    AutoSizeProperty.SetValue(text, false);
                    FontSizeProperty.SetValue(text, LetterSize);
                    AlignmentProperty.SetValue(text, Enum.Parse(AlignmentProperty.PropertyType, "Center"));
                    TextProperty.SetValue(text, Automatics.L10N.Translate(LetterKeys[i]));
                    LetterRects[i] = Center((RectTransform)letter.transform, new Vector2(Height, Height));
                }

                for (var i = MarkerCount - 1; i >= 0; i--)
                {
                    var marker = new GameObject("Marker", typeof(RectTransform));
                    Center((RectTransform)marker.transform, new Vector2(IconSize, IconSize)).SetParent(strip, false);
                    var image = marker.AddComponent<Image>();
                    image.raycastTarget = false;
                    image.preserveAspect = true;
                    marker.SetActive(false);
                    Markers[i] = image;
                }

                root.SetActive(false);
                _hud = hud;
                Root = root;
            }
            finally
            {
                if (Root != root) Object.Destroy(root);
            }
        }

        private static RectTransform Center(RectTransform rect, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = Vector2.zero;
            return rect;
        }

        private static void SetVisible(bool visible)
        {
            if (Root && Root.activeSelf != visible) Root.SetActive(visible);
        }

        private static void DestroyStrip()
        {
            var root = Root;
            Root = null;
            _hud = null;
            _refreshAt = 0f;
            Candidates.Clear();
            Shown.Clear();
            Array.Clear(Markers, 0, Markers.Length);
            Array.Clear(LetterRects, 0, LetterRects.Length);
            if (!root) return;
            root.SetActive(false);
            Object.Destroy(root);
        }

        internal static void Cleanup()
        {
            DestroyStrip();
            _nextAttemptTime = 0f;
            _errorLogged = false;
        }

        private sealed class CandidateOrder : IComparer<Candidate>
        {
            internal static readonly CandidateOrder Instance = new CandidateOrder();

            public int Compare(Candidate x, Candidate y)
            {
                return x.DistanceSq.CompareTo(y.DistanceSq);
            }
        }
    }
}
