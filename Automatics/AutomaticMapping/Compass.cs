using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Automatics.AutomaticMapping
{
    /// <summary>
    ///     A strip at the top or bottom centre of the HUD with the cardinal directions and the
    ///     icons of the nearby map pins, each at its bearing from the player. It shows the pins
    ///     the minimap would draw, without the minimap's viewport: the icon filters, the
    ///     shared-map fade and the automatic-pin filters apply, area pins are left out, and so is
    ///     every pin beyond <see cref="Config.CompassPinRange" />. Nearer icons are larger and
    ///     brighter, and the nearest are taller than the strip, so they overflow it above and
    ///     below.
    ///     <para>
    ///         The pins in range are collected and sorted by distance on a timer, and again in the
    ///         frame the navigation target changes. Every frame then places the nearest of them
    ///         that lie within the half view, so turning shows the pins ahead at once, and the cap
    ///         applies to what is in view, not to the pins behind the player.
    ///     </para>
    ///     <para>
    ///         The navigation target has a marker of its own, shown at any distance over the
    ///         others, and is never one of the ordinary markers. The pin faced and the target are
    ///         named beside the strip. At the top, the strip moves down out of the way of the boss
    ///         bar and the event bar.
    ///     </para>
    /// </summary>
    internal static class Compass
    {
        internal const string ObjectName = "AutomaticsCompass";

        // Design choices, to be tuned by eye: the angle the strip covers, its size and place in
        // canvas units, the letter size, how many pins it shows and how often it collects them.
        // The margins are measured to the content, not to the strip's edge: the nearest icons
        // overflow the strip by IconOverflow above and below, so the strip is set in by that
        // much more.
        internal const float FieldOfView = 180f;
        internal const float Width = 600f;
        private const float Height = 36f;
        private const float TopMargin = 8f;
        private const float BottomMargin = 8f;
        private const float LetterSize = 20f;
        internal const int MarkerCount = 24;
        private const float RefreshInterval = 0.25f;
        private static readonly Color BackgroundColor = new Color(0f, 0f, 0f, 0.6f);

        // Also by eye: an icon's size at the player and at the range, and its grey tint and
        // opacity at the range, eased so that most of the change comes near the player; how
        // far from the centre a pin counts as faced; the name labels; and the gap left below a
        // bar the strip moves out of the way of. The near size is larger than the strip is
        // tall, and the icons are centred on it, so each overflows by IconOverflow above and
        // below; the labels and the avoidance band allow for that. The distance is carried
        // mainly by the tint, which reads against both a bright sky and a dark ground, and
        // the opacity only falls to a floor, so far icons stay legible.
        internal const float NearIconSize = 52f;
        internal const float FarIconSize = 12f;
        internal const float FarIconAlpha = 0.6f;
        internal const float FarIconTint = 0.55f;
        internal const float IconOverflow = (NearIconSize - Height) / 2f;
        internal const float FocusAngle = 10f;
        private const float FocusOffset = FocusAngle / (FieldOfView / 2f) * (Width / 2f);
        private const float LabelFontSize = 16f;
        private const float LabelWidth = 300f;
        internal const float LabelHeight = 24f;
        internal const float AvoidanceGap = 8f;

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
        private static readonly FieldInfo HudsField = AccessTools.Field(typeof(EnemyHud), "m_huds");
        private static readonly FieldInfo HudGuiField =
            AccessTools.Field(AccessTools.Inner(typeof(EnemyHud), "HudData"), "m_gui");
        private static readonly AccessTools.FieldRef<Minimap, bool[]> VisibleIconTypes =
            AccessTools.FieldRefAccess<Minimap, bool[]>("m_visibleIconTypes");
        private static readonly AccessTools.FieldRef<Minimap, float> SharedMapDataFade =
            AccessTools.FieldRefAccess<Minimap, float>("m_sharedMapDataFade");

        private static readonly List<Candidate> Candidates = new List<Candidate>();
        private static readonly List<Minimap.PinData> Shown = new List<Minimap.PinData>(MarkerCount);
        private static readonly Image[] Markers = new Image[MarkerCount];
        private static readonly float[] MarkerFades = new float[MarkerCount];
        private static readonly RectTransform[] LetterRects = new RectTransform[LetterKeys.Length];
        private static readonly PinLabel FacedLabel = new PinLabel();
        private static readonly PinLabel TargetLabel = new PinLabel();
        private static readonly Vector3[] Corners = new Vector3[4];
        private static Image _targetMarker;
        private static Hud _hud;
        private static CompassPosition _position;
        private static Minimap.PinData _target;
        private static EnemyHud _bossHudOwner;
        private static Character _boss;
        private static RectTransform _bossHud;
        private static float _refreshAt;
        private static float _nextAttemptTime;
        private static bool _errorLogged;

        private struct Candidate
        {
            internal Minimap.PinData Pin;
            internal float DistanceSq;
        }

        internal static GameObject Root { get; private set; }

        /// <summary>
        ///     The pins shown this frame, nearest first; marker i shows pin i. The navigation
        ///     target is never among them.
        /// </summary>
        internal static IReadOnlyList<Minimap.PinData> ShownPins => Shown;

        /// <summary>The north, east, south and west letters, in that order.</summary>
        internal static IReadOnlyList<RectTransform> Letters => LetterRects;

        /// <summary>The navigation target's marker.</summary>
        internal static RectTransform TargetMarker => _targetMarker ? _targetMarker.rectTransform : null;

        /// <summary>The label of the pin faced.</summary>
        internal static RectTransform FacedLabelRect => FacedLabel.Rect;

        /// <summary>The label of the navigation target.</summary>
        internal static RectTransform TargetLabelRect => TargetLabel.Rect;

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
            var position = Config.CompassPosition;
            if (!Root || _hud != Hud.instance || _position != position)
                Create(Hud.instance, EnemyHud.instance, position);

            // A new target is collected again at once, so a temporary marker removed with the old
            // one never stays behind as an ordinary pin until the timer.
            var origin = player.transform.position;
            var target = Navigation.TargetPin;
            if (Time.unscaledTime >= _refreshAt || !ReferenceEquals(target, _target))
            {
                _refreshAt = Time.unscaledTime + RefreshInterval;
                _target = target;
                CollectCandidates(map, origin);
            }

            var heading = GameCamera.instance.transform.eulerAngles.y;
            for (var i = 0; i < LetterRects.Length; i++)
                Place(LetterRects[i], heading, i * 90f);
            var faced = PlaceMarkers(origin, heading, target, out var facedX);
            var targetShown = PlaceTarget(origin, heading, target, out var targetX, out var targetDistance);
            PlaceLabels(origin, faced, facedX, target, targetShown, targetX, targetDistance);
            PlaceStrip(Hud.instance, EnemyHud.instance);
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

        // Marker 0 is drawn over the other ordinary markers, so the nearest pin is on top. The
        // navigation target has its own marker and is skipped here. Returns the marker faced,
        // the one nearest the centre within the focus angle, or -1.
        private static int PlaceMarkers(Vector3 origin, float heading, Minimap.PinData target, out float facedX)
        {
            Shown.Clear();
            var faced = -1;
            facedX = 0f;
            var range = Config.CompassPinRange;
            for (var i = 0; i < Candidates.Count && Shown.Count < MarkerCount; i++)
            {
                var pin = Candidates[i].Pin;
                if (ReferenceEquals(pin, target)) continue;
                var dx = pin.m_pos.x - origin.x;
                var dz = pin.m_pos.z - origin.z;
                if (!TryGetOffset(heading, Mathf.Atan2(dx, dz) * Mathf.Rad2Deg, out var x)) continue;

                var index = Shown.Count;
                var marker = Markers[index];
                if (marker.sprite != pin.m_icon) marker.sprite = pin.m_icon;
                marker.rectTransform.anchoredPosition = new Vector2(x, 0f);
                SetDistanceStyle(index, Mathf.Sqrt(dx * dx + dz * dz) / range);
                if (!marker.gameObject.activeSelf) marker.gameObject.SetActive(true);
                if (Mathf.Abs(x) <= FocusOffset && (faced < 0 || Mathf.Abs(x) < Mathf.Abs(facedX)))
                {
                    faced = index;
                    facedX = x;
                }
                Shown.Add(pin);
            }
            for (var i = Shown.Count; i < MarkerCount; i++)
                if (Markers[i].gameObject.activeSelf) Markers[i].gameObject.SetActive(false);
            return faced;
        }

        // White, full size and opaque at the player; at the range and beyond, the far size,
        // a grey tint and the far opacity. In between, the change follows an ease-out curve,
        // f = 1 - (1 - t)^2 of the share of the range, so it is fastest near the player and
        // nearby icons differ clearly. The size, the grey and the opacity all follow f, so they
        // are written only when f changes.
        private static void SetDistanceStyle(int index, float rangeFraction)
        {
            var t = Mathf.Clamp01(rangeFraction);
            var f = 1f - (1f - t) * (1f - t);
            if (MarkerFades[index] == f) return;
            MarkerFades[index] = f;
            var scale = Mathf.Lerp(1f, FarIconSize / NearIconSize, f);
            var grey = Mathf.Lerp(1f, FarIconTint, f);
            var alpha = Mathf.Lerp(1f, FarIconAlpha, f);
            var marker = Markers[index];
            marker.rectTransform.localScale = new Vector3(scale, scale, 1f);
            marker.color = new Color(grey, grey, grey, alpha);
        }

        // The navigation target at any distance, in the guide's colour at full size and
        // opacity, without the grey tint of the distance, and hidden outside the half view like
        // any other pin.
        private static bool PlaceTarget(Vector3 origin, float heading, Minimap.PinData target, out float x,
            out float distance)
        {
            x = 0f;
            distance = 0f;
            var shown = false;
            if (target != null && target.m_icon)
            {
                var position = Navigation.TargetPosition;
                var dx = position.x - origin.x;
                var dz = position.z - origin.z;
                shown = TryGetOffset(heading, Mathf.Atan2(dx, dz) * Mathf.Rad2Deg, out x);
                if (shown)
                {
                    distance = Mathf.Sqrt(dx * dx + dz * dz);
                    if (_targetMarker.sprite != target.m_icon) _targetMarker.sprite = target.m_icon;
                    var color = Config.NavigationGuideColor;
                    color.a = 1f;
                    if (_targetMarker.color != color) _targetMarker.color = color;
                    _targetMarker.rectTransform.anchoredPosition = new Vector2(x, 0f);
                }
            }
            if (_targetMarker.gameObject.activeSelf != shown) _targetMarker.gameObject.SetActive(shown);
            return shown;
        }

        // The pin faced and the target are named at their markers. While the target is within
        // the focus angle it is the only one named, so the two labels never overlap there.
        private static void PlaceLabels(Vector3 origin, int faced, float facedX, Minimap.PinData target,
            bool targetShown, float targetX, float targetDistance)
        {
            if (!Config.EnableCompassLabels)
            {
                FacedLabel.Hide();
                TargetLabel.Hide();
                return;
            }
            if (targetShown && Mathf.Abs(targetX) <= FocusOffset) faced = -1;
            if (faced >= 0) FacedLabel.Show(Shown[faced], Utils.DistanceXZ(origin, Shown[faced].m_pos), facedX);
            else FacedLabel.Hide();
            if (targetShown) TargetLabel.Show(target, targetDistance, targetX);
            else TargetLabel.Hide();
        }

        // At the top, while the boss bar or the event bar is shown over the band the strip, its
        // overflowing icons and its labels take by default, the strip moves down until its
        // icons' top edge is a gap below the bar, and it goes back once neither is. At the
        // bottom it stays. The strip's default top is the top margin plus the overflow below
        // the screen's top edge, so the icons, not the strip, are the margin from it. Where the
        // bars are is vanilla's prefab data, so their rects are read as they are.
        private static void PlaceStrip(Hud hud, EnemyHud enemyHud)
        {
            var strip = (RectTransform)Root.transform;
            var y = BottomMargin + IconOverflow;
            if (_position == CompassPosition.Top)
            {
                var parent = (RectTransform)strip.parent;
                var area = parent.rect;
                var top = area.yMax - TopMargin - IconOverflow;
                var band = Rect.MinMaxRect(area.center.x - Width / 2f,
                    top - Height - IconOverflow - LabelHeight, area.center.x + Width / 2f,
                    top + IconOverflow);
                var below = Below(parent, band, GetBossHud(enemyHud), top);
                below = Below(parent, band, hud.m_eventBar.transform as RectTransform, below);
                y = below - area.yMax;
            }
            if (strip.anchoredPosition.y != y) strip.anchoredPosition = new Vector2(0f, y);
        }

        // The highest the strip's top may be with the bar: while the bar is shown over the
        // band, the gap below it plus the overflow, so the overflowing icons keep the gap, else
        // the given top. The rect is read in the strip parent's space.
        private static float Below(RectTransform parent, Rect band, RectTransform bar, float top)
        {
            if (!bar || !bar.gameObject.activeInHierarchy) return top;
            bar.GetWorldCorners(Corners);
            var min = parent.InverseTransformPoint(Corners[0]);
            var max = parent.InverseTransformPoint(Corners[2]);
            var rect = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            return rect.Overlaps(band) ? Mathf.Min(top, rect.yMin - AvoidanceGap - IconOverflow) : top;
        }

        // The boss bar is found in the enemy HUD's table only when the boss or the enemy HUD
        // changes.
        private static RectTransform GetBossHud(EnemyHud enemyHud)
        {
            var boss = enemyHud.GetActiveBoss();
            if (ReferenceEquals(boss, _boss) && ReferenceEquals(enemyHud, _bossHudOwner)) return _bossHud;
            _boss = boss;
            _bossHudOwner = enemyHud;
            _bossHud = null;
            var huds = HudsField.GetValue(enemyHud) as IDictionary;
            var data = boss && huds != null && huds.Contains(boss) ? huds[boss] : null;
            var gui = data == null ? null : HudGuiField.GetValue(data) as GameObject;
            if (gui) _bossHud = gui.transform as RectTransform;
            return _bossHud;
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
        // cutscene plays. The bottom placement mirrors the top one.
        private static void Create(Hud hud, EnemyHud enemyHud, CompassPosition position)
        {
            DestroyStrip();
            var root = new GameObject(ObjectName, typeof(RectTransform));
            try
            {
                var edge = position == CompassPosition.Top ? 1f : 0f;
                var strip = (RectTransform)root.transform;
                strip.SetParent(hud.m_rootObject.transform, false);
                strip.anchorMin = strip.anchorMax = new Vector2(0.5f, edge);
                strip.pivot = new Vector2(0.5f, edge);
                strip.sizeDelta = new Vector2(Width, Height);
                strip.anchoredPosition = new Vector2(0f, position == CompassPosition.Top ? -TopMargin - IconOverflow : BottomMargin + IconOverflow);
                var background = root.AddComponent<Image>();
                background.color = BackgroundColor;
                background.raycastTarget = false;

                var source = enemyHud.m_baseHud.transform.Find("Name").gameObject;
                for (var i = 0; i < LetterRects.Length; i++)
                {
                    var letter = CreateText(source, strip, "Letter", LetterSize);
                    TextProperty.SetValue(letter.GetComponent(TextType), Automatics.L10N.Translate(LetterKeys[i]));
                    LetterRects[i] = Center((RectTransform)letter.transform, new Vector2(Height, Height));
                }

                for (var i = MarkerCount - 1; i >= 0; i--)
                    Markers[i] = CreateMarker(strip, "Marker");
                _targetMarker = CreateMarker(strip, "TargetMarker");

                // Below the strip at the top, above it at the bottom, clear of the icons'
                // overflow.
                CreateLabel(FacedLabel, source, strip, edge);
                CreateLabel(TargetLabel, source, strip, edge);

                root.SetActive(false);
                _hud = hud;
                _position = position;
                Root = root;
            }
            finally
            {
                if (Root != root) Object.Destroy(root);
            }
        }

        private static GameObject CreateText(GameObject source, RectTransform strip, string name, float fontSize)
        {
            var text = Object.Instantiate(source, strip);
            text.name = name;
            text.SetActive(false);
            foreach (var localize in text.GetComponentsInChildren<Localize>(true)) localize.enabled = false;
            foreach (var graphic in text.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
            var component = text.GetComponent(TextType);
            AutoSizeProperty.SetValue(component, false);
            FontSizeProperty.SetValue(component, fontSize);
            AlignmentProperty.SetValue(component, Enum.Parse(AlignmentProperty.PropertyType, "Center"));
            return text;
        }

        private static Image CreateMarker(RectTransform strip, string name)
        {
            var marker = new GameObject(name, typeof(RectTransform));
            Center((RectTransform)marker.transform, new Vector2(NearIconSize, NearIconSize)).SetParent(strip, false);
            var image = marker.AddComponent<Image>();
            image.raycastTarget = false;
            image.preserveAspect = true;
            marker.SetActive(false);
            return image;
        }

        private static void CreateLabel(PinLabel label, GameObject source, RectTransform strip, float edge)
        {
            var text = CreateText(source, strip, "Label", LabelFontSize);
            var rect = (RectTransform)text.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f - edge);
            rect.pivot = new Vector2(0.5f, edge);
            rect.sizeDelta = new Vector2(LabelWidth, LabelHeight);
            rect.anchoredPosition = new Vector2(0f, edge == 1f ? -IconOverflow : IconOverflow);
            label.Attach(rect, text.GetComponent(TextType));
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
            _target = null;
            _refreshAt = 0f;
            Candidates.Clear();
            Shown.Clear();
            Array.Clear(Markers, 0, Markers.Length);
            // Below zero is no fade a marker is given, so a new strip writes each of them.
            for (var i = 0; i < MarkerFades.Length; i++) MarkerFades[i] = -1f;
            Array.Clear(LetterRects, 0, LetterRects.Length);
            _targetMarker = null;
            FacedLabel.Reset();
            TargetLabel.Reset();
            _bossHudOwner = null;
            _boss = null;
            _bossHud = null;
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

        // A pin's name and distance in the navigation label's format, rewritten only when the
        // pin, its name or the whole metre changes.
        private sealed class PinLabel
        {
            private Component _text;
            private Minimap.PinData _pin;
            private string _rawName;
            private string _displayName;
            private int _meters = int.MinValue;

            internal RectTransform Rect { get; private set; }

            internal void Attach(RectTransform rect, Component text)
            {
                Rect = rect;
                _text = text;
            }

            internal void Show(Minimap.PinData pin, float distance, float x)
            {
                if (!ReferenceEquals(pin, _pin) || !ReferenceEquals(pin.m_name, _rawName))
                {
                    _pin = pin;
                    _rawName = pin.m_name;
                    var name = (_rawName ?? string.Empty).Replace("\n", " ").Trim();
                    _displayName = Localization.instance != null ? Localization.instance.Localize(name) : name;
                    _meters = int.MinValue;
                }
                var meters = Mathf.RoundToInt(distance);
                if (meters != _meters)
                {
                    _meters = meters;
                    TextProperty.SetValue(_text, _displayName + " [" + meters + "m]");
                }
                // The height is the offset the label was created with.
                Rect.anchoredPosition = new Vector2(x, Rect.anchoredPosition.y);
                if (!Rect.gameObject.activeSelf) Rect.gameObject.SetActive(true);
            }

            internal void Hide()
            {
                if (Rect.gameObject.activeSelf) Rect.gameObject.SetActive(false);
            }

            internal void Reset()
            {
                Rect = null;
                _text = null;
                _pin = null;
                _rawName = null;
                _displayName = null;
                _meters = int.MinValue;
            }
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
