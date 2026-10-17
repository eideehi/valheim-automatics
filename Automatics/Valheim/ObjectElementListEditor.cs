using System.Collections.Generic;
using System.Linq;
using ModUtils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Automatics.Valheim
{
    /// <summary>
    /// Edits a custom object list on the in-game config screen as its ConfigurationManager drawer
    /// does: identifier, label and pattern fields with an Add button, then one line per element with
    /// a Remove button. A pattern that starts with <c>r/</c> adds a regex matcher.
    /// </summary>
    internal sealed class ObjectElementListEditor : ConfigurationGuiCustomEditor
    {
        // Every caption gets the same fixed width, so the three fields start at the same place. A
        // caption longer than that is cut off instead of moving its field.
        private const float CaptionWidth = 120f;
        private const float ButtonWidth = 120f;

        // The spacing that the slot and the column put between lines.
        private const float LineSpacing = 4f;

        private ConfigurationGuiInputField _identifier;
        private ConfigurationGuiInputField _label;
        private RectTransform _lines;
        private ConfigurationGuiInputField _pattern;
        private ConfigurationGuiWidgets _widgets;

        private List<ObjectElement> Elements => (List<ObjectElement>)Value;

        protected override float Build(RectTransform slot, ConfigurationGuiWidgets widgets)
        {
            _widgets = widgets;
            _identifier = CreateInput(slot, "@config_object_element_identifier");
            _label = CreateInput(slot, "@config_object_element_label");
            _pattern = CreateInput(slot, "@config_object_element_value");
            widgets.CreateButton(slot, Automatics.L10N.Translate("mod_utils_config_button_add"), Add);
            _lines = widgets.CreateColumn(slot);

            LineHeight = widgets.ControlHeight;
            Refresh();
            return Height();
        }

        protected override void Refresh()
        {
            // Lines are hidden at once, because Destroy removes them only at the end of the frame.
            for (var i = _lines.childCount - 1; i >= 0; i--)
            {
                var line = _lines.GetChild(i).gameObject;
                if (!line.activeSelf) continue;

                line.SetActive(false);
                Object.Destroy(line);
            }

            var height = _widgets.ControlHeight;
            var removeText = Automatics.L10N.Translate("mod_utils_config_button_remove");
            foreach (var element in Elements.Where(x => x != null))
            {
                var line = _widgets.CreateRow(_lines);
                var text = _widgets.CreateLabel(line, Describe(element));
                _widgets.SetLayout(text.gameObject, preferredWidth: 0f, flexibleWidth: 1f, height: height);
                var remove = _widgets.CreateButton(line, removeText,
                    () => Commit(ObjectElementList.CopyWithoutElement(Elements, element)));
                _widgets.SetLayout(remove.gameObject, ButtonWidth, ButtonWidth, height: height);
            }

            SetHeight(Height());
        }

        private ConfigurationGuiInputField CreateInput(Transform parent, string captionKey)
        {
            var height = _widgets.ControlHeight;
            var row = _widgets.CreateRow(parent);
            var caption = _widgets.CreateLabel(row, Automatics.L10N.Translate(captionKey));
            _widgets.SetLayout(caption.gameObject, CaptionWidth, CaptionWidth, height: height);
            var field = _widgets.CreateTextField(row, _ => { });
            _widgets.SetLayout(field.Input.gameObject, preferredWidth: 0f, flexibleWidth: 1f, height: height);
            return field;
        }

        // The three input lines, the Add button and one line per shown element.
        private float Height()
        {
            var lines = 4 + Elements.Count(x => x != null);
            return lines * _widgets.ControlHeight + (lines - 1) * LineSpacing;
        }

        private void Add()
        {
            if (!ObjectElementList.TryCopyWithElement(Elements, _identifier.Input.text, _label.Input.text,
                    _pattern.Input.text, out var elements))
                return;

            Commit(elements);
            _identifier.Input.SetTextWithoutNotify("");
            _label.Input.SetTextWithoutNotify("");
            _pattern.Input.SetTextWithoutNotify("");
        }

        // The translated label, then the identifier and every matcher, a regex one with its r/
        // prefix, so elements with the same label can be told apart.
        private static string Describe(ObjectElement element)
        {
            var matchers = element.matches == null
                ? ""
                : string.Join(", ", element.matches.Select(x => x.regex ? "r/" + x.value : x.value));
            return $"{Automatics.L10N.TranslateInternalName(element.label)} ({element.identifier}: {matchers})";
        }
    }
}
