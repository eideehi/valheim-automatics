using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ModUtils;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Automatics.AutomaticMapping
{
    internal static class MapFilter
    {
        internal const string ObjectName = "AutomaticsMapFilter";
        private static readonly Type TextType = AccessTools.Property(typeof(Minimap.PinNameData), "PinNameText").PropertyType;
        private static readonly PropertyInfo TextProperty = AccessTools.Property(TextType, "text");
        private static readonly PropertyInfo FontSizeProperty = AccessTools.Property(TextType, "fontSize");
        private static readonly PropertyInfo PreferredWidthProperty = AccessTools.Property(TextType, "preferredWidth");
        private static readonly FieldInfo NameInputField = AccessTools.Field(typeof(Minimap), "m_nameInput");
        private static readonly Type InputType = NameInputField.FieldType;
        private static readonly PropertyInfo InputTextProperty = AccessTools.Property(InputType, "text");
        private static readonly PropertyInfo InputFocusProperty = AccessTools.Property(InputType, "isFocused");
        private static readonly PropertyInfo InputCompositionProperty = AccessTools.Property(InputType, "compositionLength");
        private static readonly FieldInfo InputCompositionActiveField = AccessTools.Field(InputType, "m_IsCompositionActive");
        private static readonly PropertyInfo InputPlaceholderProperty = AccessTools.Property(InputType, "placeholder");
        private static readonly MethodInfo ForceInputLabelUpdate = AccessTools.Method(InputType, "ForceLabelUpdate");
        private static readonly List<FilterControl> FilterControls = new List<FilterControl>();
        private static readonly Dictionary<Minimap.PinData, SearchLabel> SearchLabels =
            new Dictionary<Minimap.PinData, SearchLabel>();
        private static Minimap _map;
        private static string _searchText = "";
        private static string _loweredSearch = "";
        private static int _blockedInputFrame = -1;
        private static Vector2 _layoutSize;

        private struct SearchLabel
        {
            internal string Name;
            internal string Lowered;
        }

        internal static GameObject Root { get; private set; }
        internal static IReadOnlyList<FilterControl> Controls => FilterControls;
        internal static Component SearchInput { get; private set; }
        internal static FilterControl SearchToggle { get; private set; }
        internal static string SearchText => _searchText;
        internal static bool SearchPinned { get; private set; }
        internal static bool HasSearch => _loweredSearch.Length != 0;
        internal static bool SearchFocused => SearchInput && SearchInput.gameObject.activeInHierarchy &&
            (bool)InputFocusProperty.GetValue(SearchInput);
        internal static bool InTextInput => Module.Running && Config.EnableMapEnhancements &&
            _map && _map.m_mode == Minimap.MapMode.Large &&
            (SearchFocused || _blockedInputFrame == Time.frameCount);
        internal static bool SearchActive => HasSearch && Minimap.instance &&
            (Minimap.instance.m_mode == Minimap.MapMode.Large || SearchPinned);

        internal static bool ShouldHideBySearch(Minimap.PinData pin)
        {
            if (ReferenceEquals(pin, Navigation.TargetPin)) return false;
            if (!SearchLabels.TryGetValue(pin, out var label) || !ReferenceEquals(label.Name, pin.m_name))
            {
                label = new SearchLabel
                {
                    Name = pin.m_name,
                    Lowered = Localization.instance.Localize(pin.m_name ?? "").ToLowerInvariant()
                };
                SearchLabels[pin] = label;
            }
            return !label.Lowered.Contains(_loweredSearch);
        }

        internal static void OnRemovePin(Minimap.PinData pin)
        {
            if (pin != null) SearchLabels.Remove(pin);
        }

        internal static void RefreshSearchPins()
        {
            if (Module.Running && Config.EnableMapEnhancements && HasSearch) Map.RefreshPins();
        }

        internal static void Update(Minimap map)
        {
            if (!Module.Running || !Config.EnableMapEnhancements)
            {
                Cleanup();
                return;
            }
            if (_map && _map != map) Cleanup();
            if (!Root && map.m_mode == Minimap.MapMode.Large) CreateControls(map);
            if (!Root) return;
            if (_layoutSize != ((RectTransform)Root.transform).rect.size) LayoutControls();
            foreach (var control in FilterControls) control.Refresh();
            SearchToggle.Refresh();
        }

        internal static void UpdateInput()
        {
            if (!SearchFocused) return;
            // TMP's submit path distinguishes an IME confirmation from an Enter that ends editing.
            if (ZInput.GetKeyDown(KeyCode.Escape)) DefocusSearch();
        }

        internal static void DefocusSearch()
        {
            _blockedInputFrame = Time.frameCount;
            AccessTools.Method(InputType, "DeactivateInputField").Invoke(SearchInput, new object[] { false });
            if (EventSystem.current && EventSystem.current.currentSelectedGameObject == SearchInput.gameObject)
                EventSystem.current.SetSelectedGameObject(null);
            ForceInputLabelUpdate.Invoke(SearchInput, null);
            OnSearchChanged((string)InputTextProperty.GetValue(SearchInput));
        }

        private static void OnSearchSubmitted(string text)
        {
            if (Input.compositionString.Length != 0 || (int)InputCompositionProperty.GetValue(SearchInput) != 0 ||
                (bool)InputCompositionActiveField.GetValue(SearchInput)) return;
            DefocusSearch();
        }

        internal static void SetSearchText(string text)
        {
            InputTextProperty.SetValue(SearchInput, text);
        }

        private static void OnSearchChanged(string text)
        {
            var placeholder = (Behaviour)InputPlaceholderProperty.GetValue(SearchInput);
            placeholder.enabled = string.IsNullOrEmpty(text) && Input.compositionString.Length == 0 &&
                (int)InputCompositionProperty.GetValue(SearchInput) == 0;
            _searchText = text;
            var lowered = text.Trim().ToLowerInvariant();
            if (_loweredSearch == lowered) return;
            _loweredSearch = lowered;
            if (Module.Running) Map.RefreshPins();
        }

        internal static void OnLanguageChange()
        {
            SearchLabels.Clear();
            if (!Root) return;
            foreach (var control in FilterControls) control.LocalizeLabel();
            SearchToggle.LocalizeLabel();
            SetPlaceholder();
            LayoutControls();
            RefreshSearchPins();
        }

        internal static void Cleanup()
        {
            _searchText = "";
            _loweredSearch = "";
            SearchPinned = false;
            _blockedInputFrame = -1;
            SearchLabels.Clear();
            if (Root)
            {
                Root.SetActive(false);
                Object.Destroy(Root);
            }
            Root = null;
            SearchInput = null;
            SearchToggle = null;
            _map = null;
            FilterControls.Clear();
        }

        private static void CreateControls(Minimap map)
        {
            var iconParent = map.m_selectedIcon0.transform.parent;
            var button = iconParent.GetComponent<Button>();
            var source = button && iconParent.GetComponentInChildren(TextType, true)
                ? iconParent.gameObject : map.m_publicPosition.gameObject;
            _map = map;
            Root = new GameObject(ObjectName, typeof(RectTransform));
            var row = (RectTransform)Root.transform;
            row.SetParent(map.m_largeRoot.transform, false);
            row.anchorMin = Vector2.zero;
            row.anchorMax = Vector2.one;
            row.offsetMin = row.offsetMax = Vector2.zero;
            foreach (var category in PinCategories.Categories)
            {
                var go = Object.Instantiate(source, row);
                go.name = "AutomaticsMapFilter_" + category;
                FilterControls.Add(new FilterControl(go, category));
                go.SetActive(true);
            }
            var toggleRoot = Object.Instantiate(source, row);
            toggleRoot.name = "AutomaticsMapFilter_PinSearch";
            SearchToggle = new FilterControl(toggleRoot, null);
            toggleRoot.SetActive(true);

            var originalInput = (Component)NameInputField.GetValue(map);
            var inputRoot = Object.Instantiate(originalInput.gameObject, row);
            inputRoot.name = "AutomaticsMapFilter_Search";
            SearchInput = inputRoot.GetComponent(InputType);
            DisableInheritedInput(inputRoot);
            ((Selectable)SearchInput).navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.None };
            foreach (var eventName in new[] { "onValueChanged", "onEndEdit", "onSubmit", "onSelect", "onDeselect" })
            {
                var property = AccessTools.Property(InputType, eventName);
                property.SetValue(SearchInput, Activator.CreateInstance(property.PropertyType));
            }
            var submit = AccessTools.Field(InputType, "OnInputSubmit");
            submit.SetValue(SearchInput, Activator.CreateInstance(submit.FieldType));
            AccessTools.Field(InputType, "VirtualKeyboardStateChange").SetValue(SearchInput, null);
            AccessTools.Property(InputType, "restoreOriginalTextOnEscape").SetValue(SearchInput, false);
            AccessTools.Property(InputType, "lineType").SetValue(SearchInput,
                Enum.Parse(AccessTools.Property(InputType, "lineType").PropertyType, "SingleLine"));
            InputTextProperty.SetValue(SearchInput, "");
            ((UnityEvent<string>)AccessTools.Property(InputType, "onValueChanged").GetValue(SearchInput))
                .AddListener(OnSearchChanged);
            ((UnityEvent<string>)AccessTools.Property(InputType, "onEndEdit").GetValue(SearchInput))
                .AddListener(_ => DefocusSearch());
            ((UnityEvent<string>)AccessTools.Property(InputType, "onSubmit").GetValue(SearchInput))
                .AddListener(OnSearchSubmitted);
            SetPlaceholder();
            inputRoot.SetActive(true);
            Canvas.ForceUpdateCanvases();
            LayoutControls();
        }

        private static void SetPlaceholder()
        {
            var placeholder = (Component)AccessTools.Property(InputType, "placeholder").GetValue(SearchInput);
            TextProperty.SetValue(placeholder, Automatics.L10N.Translate("@text_automatic_mapping_search_placeholder"));
        }

        private static void DisableInheritedInput(GameObject root)
        {
            foreach (var localize in root.GetComponentsInChildren<Localize>(true)) localize.enabled = false;
            foreach (var hint in root.GetComponentsInChildren<UIInputHint>(true)) hint.gameObject.SetActive(false);
            foreach (var tooltip in root.GetComponentsInChildren<UITooltip>(true)) tooltip.enabled = false;
            foreach (var gamepad in root.GetComponentsInChildren<UIGamePad>(true)) gamepad.enabled = false;
        }

        // Measure the translated text and flow around the vanilla filter column and public toggle.
        private static void LayoutControls()
        {
            var row = (RectTransform)Root.transform;
            _layoutSize = row.rect.size;
            var occupied = new List<Rect>();
            var column = LocalRect(row, (RectTransform)_map.m_selectedIcon0.transform.parent);
            foreach (var field in typeof(Minimap).GetFields())
                if (field.Name.StartsWith("m_selectedIcon") && field.GetValue(_map) is Image icon && icon)
                {
                    var iconRect = LocalRect(row, (RectTransform)icon.transform.parent);
                    column = Rect.MinMaxRect(Mathf.Min(column.xMin, iconRect.xMin), Mathf.Min(column.yMin, iconRect.yMin),
                        Mathf.Max(column.xMax, iconRect.xMax), Mathf.Max(column.yMax, iconRect.yMax));
                }
            occupied.Add(column);
            occupied.Add(LocalRect(row, (RectTransform)_map.m_publicPosition.transform));
            var left = row.rect.xMin + 24f;
            var right = row.rect.xMax - 24f;
            var x = left;
            var y = row.rect.yMin + 80f;
            foreach (var control in FilterControls)
                Place((RectTransform)control.Root.transform, control.Width, row, occupied, left, right, ref x, ref y);
            x = left;
            y += 40f;
            var placeholder = (Component)AccessTools.Property(InputType, "placeholder").GetValue(SearchInput);
            var inputWidth = Mathf.Max(260f, Mathf.Ceil((float)PreferredWidthProperty.GetValue(placeholder)) + 32f);
            Place((RectTransform)SearchInput.transform, inputWidth, row, occupied, left, right, ref x, ref y);
            Place((RectTransform)SearchToggle.Root.transform, SearchToggle.Width, row, occupied, left, right, ref x, ref y);
        }

        private static void Place(RectTransform rect, float width, RectTransform row, List<Rect> occupied,
            float left, float right, ref float x, ref float y)
        {
            while (true)
            {
                if (x + width > right) { x = left; y += 40f; }
                var candidate = new Rect(x, y, width, 32f);
                var collision = occupied.FindIndex(other => candidate.Overlaps(other));
                if (collision >= 0) { x = occupied[collision].xMax + 8f; continue; }
                rect.anchorMin = rect.anchorMax = Vector2.zero;
                rect.pivot = Vector2.zero;
                rect.sizeDelta = candidate.size;
                rect.anchoredPosition = candidate.position - row.rect.min;
                occupied.Add(new Rect(x - 4f, y - 4f, width + 8f, 40f));
                x += width + 8f;
                return;
            }
        }

        internal static Rect ScreenRect(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            var canvas = rect.GetComponentInParent<Canvas>().rootCanvas;
            var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var min = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            var max = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        internal static Rect LocalRect(RectTransform relativeTo, RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            var min = relativeTo.InverseTransformPoint(corners[0]);
            var max = relativeTo.InverseTransformPoint(corners[2]);
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        internal sealed class FilterControl
        {
            private readonly Toggle _toggle;
            private readonly Button _button;
            private readonly Graphic _tint;
            private readonly Component _label;

            internal FilterControl(GameObject root, PinCategory? category)
            {
                Root = root;
                Category = category;
                _label = root.GetComponentInChildren(TextType, true);
                FontSizeProperty.SetValue(_label, 14f);
                AccessTools.Property(TextType, "enableAutoSizing").SetValue(_label, false);
                LocalizeLabel();
                DisableInheritedInput(root);
                var labelRect = (RectTransform)_label.transform;
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = new Vector2(28f, 2f);
                labelRect.offsetMax = new Vector2(-8f, -2f);
                _toggle = root.GetComponent<Toggle>();
                _button = root.GetComponent<Button>();
                var selectable = _toggle ? (Selectable)_toggle : _button;
                selectable.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.None };
                _tint = selectable.targetGraphic;
                if (_toggle)
                {
                    _toggle.group = null;
                    _toggle.onValueChanged = new Toggle.ToggleEvent();
                    _toggle.onValueChanged.AddListener(_ => Click());
                    var box = (RectTransform)_tint.transform;
                    if (box != root.transform)
                    {
                        box.anchorMin = box.anchorMax = new Vector2(0f, 0.5f);
                        box.pivot = new Vector2(0f, 0.5f);
                        box.anchoredPosition = new Vector2(2f, 0f);
                        box.sizeDelta = new Vector2(20f, 20f);
                    }
                }
                else
                {
                    _button.onClick = new Button.ButtonClickedEvent();
                    _button.onClick.AddListener(Click);
                }
                Refresh();
            }

            internal GameObject Root { get; }
            internal PinCategory? Category { get; }
            internal Color Tint => _tint ? _tint.color : Color.clear;
            internal bool Shown => Category.HasValue ? !Config.IsMapCategoryHidden(Category.Value) : SearchPinned;
            internal float Width => Mathf.Ceil((float)PreferredWidthProperty.GetValue(_label)) + 44f;

            internal void LocalizeLabel()
            {
                TextProperty.SetValue(_label, Automatics.L10N.Translate(Category.HasValue
                    ? "@text_automatic_mapping_filter_" + Category.Value.ToString().ToLowerInvariant()
                    : "@text_automatic_mapping_search_pin"));
            }

            internal void Refresh()
            {
                var shown = Shown;
                if (_toggle) _toggle.SetIsOnWithoutNotify(shown);
                if (_tint) _tint.color = shown ? Color.white : Color.gray;
            }

            private void Click()
            {
                if (!Module.Running || !Config.EnableMapEnhancements) return;
                if (Category.HasValue)
                {
                    var hidden = new StringList(Config.MapFilterHiddenCategories);
                    var name = Category.Value.ToString();
                    if (!hidden.Remove(name)) hidden.Add(name);
                    Config.MapFilterHiddenCategoriesEntry.Value = hidden;
                }
                else
                {
                    SearchPinned = !SearchPinned;
                    Refresh();
                    Map.RefreshPins();
                }
            }

            internal void InvokeClick()
            {
                if (_toggle) _toggle.onValueChanged.Invoke(!Shown);
                else _button.onClick.Invoke();
            }
        }
    }
}
