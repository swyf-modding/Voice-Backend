using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ScamWYF.Modding.Core.Ui
{
    /// <summary>
    /// Factory methods for the shared UI's widgets, styled to match the base game.
    /// </summary>
    /// <remarks>
    /// Mods should build their interfaces out of these rather than dropping raw VisualElements, for
    /// the same reason the rest of the library exists: two mods styling the same control
    /// differently is the thing that makes a pile of overlays look like a pile of overlays.
    ///
    /// Everything here is code rather than USS on purpose. A stylesheet would have to ship as an
    /// asset next to the dll, and the library is source-only - one file compiled into each mod - so
    /// there is nowhere for a .uss to live. Inline styles cost a few more bytes and keep the whole
    /// thing in one place.
    /// </remarks>
    public static class Widgets
    {
        /// <summary>
        /// When true, widgets are being rendered into the game's own settings tree, and adopt the
        /// game's own class names so its stylesheet draws them. Set by the menu host around each
        /// page's render; false everywhere else.
        /// </summary>
        internal static bool GameTree;

        /// <summary>The overlay every shared window is added to, creating the panel if needed.</summary>
        public static VisualElement Overlay
        {
            get { return UiPanel.Ensure(); }
        }

        /// <summary>True when the shared UI can actually be drawn.</summary>
        public static bool Available
        {
            get { return UiPanel.Available; }
        }

        // ---------------------------------------------------------------- layout

        /// <summary>A full-width vertical stack.</summary>
        public static VisualElement Column(VisualElement parent)
        {
            var element = new VisualElement();
            element.style.flexDirection = FlexDirection.Column;
            element.style.alignItems = Align.Stretch;
            element.style.flexShrink = 0f;
            Touch(parent, element);
            return element;
        }

        /// <summary>A horizontal row whose children share the height.</summary>
        public static VisualElement Row(VisualElement parent)
        {
            var element = new VisualElement();
            element.style.flexDirection = FlexDirection.Row;
            element.style.alignItems = Align.Center;
            element.style.flexShrink = 0f;
            Touch(parent, element);
            return element;
        }

        /// <summary>A row that wraps, for a grid of buttons.</summary>
        public static VisualElement WrapRow(VisualElement parent)
        {
            var element = Row(parent);
            element.style.flexWrap = Wrap.Wrap;
            return element;
        }

        /// <summary>A scrollable column, which is what a menu page almost always wants to be.</summary>
        public static ScrollView Scroll(VisualElement parent)
        {
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1f;
            scroll.style.flexShrink = 1f;
            scroll.verticalScrollerVisibility = ScrollerVisibility.Auto;
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            Touch(parent, scroll);
            return scroll;
        }

        /// <summary>Fixed-height empty space.</summary>
        public static VisualElement Spacer(VisualElement parent, float height)
        {
            var element = new VisualElement();
            element.style.height = height;
            element.style.flexShrink = 0f;
            element.pickingMode = PickingMode.Ignore;
            Touch(parent, element);
            return element;
        }

        /// <summary>Empty space that eats whatever horizontal room is left over.</summary>
        public static VisualElement Filler(VisualElement parent)
        {
            var element = new VisualElement();
            element.style.flexGrow = 1f;
            element.pickingMode = PickingMode.Ignore;
            Touch(parent, element);
            return element;
        }

        // ---------------------------------------------------------------- text

        public static Label Label(VisualElement parent, string text, bool muted)
        {
            var label = new Label(text ?? "");
            if (GameTree)
            {
                if (muted) label.AddToClassList("setting-help");
                else label.AddToClassList("setting-label");
            }
            else
            {
                label.style.color = muted ? UiTheme.TextMuted : UiTheme.Text;
                label.style.fontSize = 13f;
            }
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.flexShrink = 1f;
            label.pickingMode = PickingMode.Ignore;
            Touch(parent, label);
            return label;
        }

        /// <summary>
        /// A section that can be collapsed. For the long tail of a mod's settings, where most people
        /// want two of the five groups and have no interest in the rest.
        /// </summary>
        public static Foldout Section(VisualElement parent, string text, bool open)
        {
            var fold = Foldout(parent, text, open);

            // The default foldout triangle is a text glyph in the theme's font at a size that varies
            // between themes; a drawn chevron of our own is predictable and matches the rest of the menu.
            fold.text = "";
            fold.style.paddingLeft = 2f;

            var header = Row(fold);
            header.style.height = UiTheme.Row;
            header.style.flexShrink = 0f;

            var marker = new Label(open ? "v" : ">");
            if (!GameTree)
            {
                marker.style.color = UiTheme.TextMuted;
                marker.style.fontSize = 11f;
            }
            marker.style.width = 14f;
            marker.style.unityTextAlign = TextAnchor.MiddleCenter;
            marker.pickingMode = PickingMode.Ignore;
            header.Add(marker);

            var label = new Label(text ?? "");
            if (!GameTree)
            {
                label.style.color = UiTheme.Text;
                label.style.fontSize = 13f;
            }
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            label.pickingMode = PickingMode.Ignore;
            header.Add(label);

            // Whole header is the toggle, not just the arrow, and the marker follows the state so it does
            // not need a callback per foldout.
            header.RegisterCallback<PointerDownEvent>(delegate (PointerDownEvent evt)
            {
                if (evt.button != 0) return;
                var next = !fold.value;
                fold.value = next;
                marker.text = next ? "v" : ">";
                evt.StopPropagation();
            });

            var content = Column(fold);
            content.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            content.style.flexShrink = 0f;

            // Keep the content in step if anything else changes the value.
            fold.RegisterCallback<ChangeEvent<bool>>(delegate (ChangeEvent<bool> evt)
            {
                marker.text = evt.newValue ? "v" : ">";
                content.style.display = evt.newValue ? DisplayStyle.Flex : DisplayStyle.None;
            });

            SectionContent[fold] = content;
            return fold;
        }

        /// <summary>
        /// The inner column of each collapsible section, so a caller can keep adding to it after the
        /// section itself has been returned.
        /// </summary>
        private static readonly Dictionary<Foldout, VisualElement> SectionContent =
            new Dictionary<Foldout, VisualElement>();

        /// <summary>
        /// Add to a collapsible section's content. Use this instead of adding to the Foldout directly,
        /// or the content will appear beside the header rather than under it.
        /// </summary>
        public static VisualElement SectionBody(Foldout section)
        {
            if (section == null) return null;

            VisualElement content;
            return SectionContent.TryGetValue(section, out content) ? content : null;
        }

        /// <summary>A heading with a rule under it. Used to break a page into sections.</summary>
        public static VisualElement Heading(VisualElement parent, string text)
        {
            var column = Column(parent);
            if (!GameTree)
            {
                column.style.marginTop = 6f;
                column.style.marginBottom = 2f;
            }

            var label = new Label(text ?? "");
            if (GameTree)
            {
                label.AddToClassList("section-label");
            }
            else
            {
                label.style.color = UiTheme.Text;
                label.style.fontSize = 15f;
                label.style.unityFontStyleAndWeight = FontStyle.Bold;
                label.style.unityTextAlign = TextAnchor.LowerLeft;
                label.style.marginLeft = 4f;
                label.style.marginBottom = 3f;
            }
            column.Add(label);

            if (!GameTree)
            {
                var rule = new VisualElement();
                rule.style.height = UiTheme.BorderWidth;
                rule.style.backgroundColor = UiTheme.WindowBorder;
                rule.style.marginBottom = 4f;
                rule.pickingMode = PickingMode.Ignore;
                column.Add(rule);
            }

            return column;
        }

        /// <summary>Body copy for explanations, wrapping freely.</summary>
        public static Label Paragraph(VisualElement parent, string text)
        {
            var label = new Label(text ?? "");
            if (GameTree)
            {
                label.AddToClassList("long-copy");
            }
            else
            {
                label.style.color = UiTheme.TextMuted;
                label.style.fontSize = 12f;
            }
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.marginLeft = 4f;
            label.style.marginRight = 4f;
            label.style.marginBottom = 4f;
            Touch(parent, label);
            return label;
        }

        // ---------------------------------------------------------------- controls

        public static Button Button(VisualElement parent, string text, Action onClick)
        {
            var button = new Button(() =>
            {
                if (onClick == null) return;
                try
                {
                    onClick();
                }
                catch (Exception ex)
                {
                    UiLog.Error("A UI action threw: " + ex);
                }
            })
            {
                text = text ?? ""
            };

            if (GameTree)
            {
                button.AddToClassList("reset-button");
            }
            else
            {
                button.style.height = UiTheme.RowHeight + 4f;
                button.style.minWidth = 88f;
                button.style.marginLeft = 2f;
                button.style.marginRight = 2f;
                button.style.marginTop = 2f;
                button.style.marginBottom = 2f;
                button.style.paddingLeft = 10f;
                button.style.paddingRight = 10f;
                button.style.color = UiTheme.Text;
                button.style.fontSize = 13f;
                button.style.unityTextAlign = TextAnchor.MiddleCenter;
            }
            Touch(parent, button);
            return button;
        }

        /// <summary>
        /// A button that stays down while a page is selected. The universal menu's tab strip is
        /// built from these.
        /// </summary>
        public static Button TabButton(VisualElement parent, string text, Action onClick)
        {
            var button = Button(parent, text, onClick);
            button.style.minWidth = 0f;
            button.style.flexGrow = 1f;
            button.style.unityTextAlign = TextAnchor.MiddleLeft;
            button.style.marginLeft = 3f;
            button.style.marginRight = 3f;
            return button;
        }

        /// <summary>
        /// A toggle shown as a button with ON/OFF text, using the game's reset-button class.
        /// For use inside the game's settings tree where raw Toggle elements don't match the style.
        /// </summary>
        public static Button ToggleButton(VisualElement parent, string text, bool value, Action<bool> onChanged)
        {
            var btn = new Button { text = value ? "ON" : "OFF" };
            if (GameTree)
            {
                btn.AddToClassList("reset-button");
            }
            else
            {
                btn.style.height = UiTheme.RowHeight + 4f;
                btn.style.minWidth = 60f;
                btn.style.marginLeft = 2f;
                btn.style.marginRight = 2f;
                btn.style.marginTop = 2f;
                btn.style.marginBottom = 2f;
                btn.style.paddingLeft = 10f;
                btn.style.paddingRight = 10f;
                btn.style.color = UiTheme.Text;
                btn.style.fontSize = 13f;
                btn.style.unityTextAlign = TextAnchor.MiddleCenter;
            }
            btn.clicked += delegate
            {
                var next = btn.text != "ON";
                btn.text = next ? "ON" : "OFF";
                if (onChanged != null)
                {
                    try { onChanged(next); } catch (Exception ex) { UiLog.Error("A toggle handler threw: " + ex); }
                }
            };
            Touch(parent, btn);
            return btn;
        }

        public static Toggle Toggle(VisualElement parent, string text, bool value, Action<bool> onChanged)
        {
            var toggle = new Toggle(text ?? "") { value = value };
            if (!GameTree)
            {
                toggle.style.color = UiTheme.Text;
                toggle.style.fontSize = 13f;
                toggle.style.marginLeft = 4f;
                toggle.style.marginTop = 3f;
                toggle.style.marginBottom = 3f;
                toggle.style.minHeight = UiTheme.Row;
                if (toggle.labelElement != null)
                {
                    toggle.labelElement.style.unityTextAlign = TextAnchor.MiddleLeft;
                    toggle.labelElement.style.whiteSpace = WhiteSpace.Normal;
                }
            }
            // In GameTree we leave Toggle unstyled - the game's stylesheet may style it, or use ToggleButton instead.

            if (onChanged != null)
            {
                toggle.RegisterValueChangedCallback(evt =>
                {
                    try
                    {
                        onChanged(evt.newValue);
                    }
                    catch (Exception ex)
                    {
                        UiLog.Error("A toggle handler threw: " + ex);
                    }
                });
            }

            Touch(parent, toggle);
            return toggle;
        }

        /// <summary>
        /// A text field that reports every keystroke. For values a mod wants to act on straight
        /// away, such as an endpoint being typed into.
        /// </summary>
        public static TextField TextField(VisualElement parent, string label, string value,
            Action<string> onChanged, bool multiline)
        {
            var field = new TextField(label ?? "") { value = value ?? "" };
            field.multiline = multiline;
            if (GameTree)
            {
                field.AddToClassList("byok-field");
            }
            else
            {
                field.style.color = UiTheme.FieldText;
                field.style.fontSize = 13f;
            }
            if (!GameTree)
            {
                field.style.marginLeft = 4f;
                field.style.marginRight = 4f;
                field.style.marginTop = 2f;
                field.style.marginBottom = 2f;
                field.style.flexGrow = 1f;
                if (!multiline) field.style.height = UiTheme.Row;
            }
            else
            {
                field.style.marginLeft = 4f;
                field.style.marginRight = 4f;
                field.style.marginTop = 2f;
                field.style.marginBottom = 2f;
                field.style.flexGrow = 1f;
            }
            if (field.labelElement != null)
            {
                if (!GameTree)
                {
                    field.labelElement.style.color = UiTheme.TextMuted;
                    field.labelElement.style.fontSize = 12f;
                }
                field.labelElement.style.unityTextAlign = TextAnchor.MiddleLeft;
                if (!GameTree)
                {
                    field.labelElement.style.minWidth = 150f;
                }
            }

            if (onChanged != null)
            {
                field.RegisterValueChangedCallback(evt =>
                {
                    try
                    {
                        onChanged(evt.newValue);
                    }
                    catch (Exception ex)
                    {
                        UiLog.Error("A text field handler threw: " + ex);
                    }
                });
            }

            Touch(parent, field);
            return field;
        }

        /// <summary>A text field that hides what is typed. Used for API keys.</summary>
        public static TextField SecretField(VisualElement parent, string label, string value,
            Action<string> onChanged)
        {
            var field = TextField(parent, label, value, onChanged, false);
            field.isPasswordField = true;
            return field;
        }

        /// <summary>A labelled slider for a bounded number.</summary>
        public static SliderInt IntSlider(VisualElement parent, string label, int value, int min, int max,
            Action<int> onChanged)
        {
            var slider = new SliderInt(min, max) { value = value, showInputField = true };
            slider.label = label ?? "";
            slider.style.color = UiTheme.Text;
            if (!GameTree)
            {
                slider.style.fontSize = 12f;
            }
            slider.style.marginLeft = 4f;
            slider.style.marginRight = 4f;
            slider.style.marginTop = 2f;
            slider.style.marginBottom = 2f;
            slider.style.flexGrow = 1f;
            if (slider.labelElement != null)
            {
                slider.labelElement.style.color = UiTheme.TextMuted;
                slider.labelElement.style.unityTextAlign = TextAnchor.MiddleRight;
                slider.labelElement.style.minWidth = 150f;
            }

            if (onChanged != null)
            {
                slider.RegisterValueChangedCallback(evt =>
                {
                    try
                    {
                        onChanged(evt.newValue);
                    }
                    catch (Exception ex)
                    {
                        UiLog.Error("A slider handler threw: " + ex);
                    }
                });
            }

            Touch(parent, slider);
            return slider;
        }

        /// <summary>
        /// A dropdown built from a list of display names. The caller maps between the name and the
        /// underlying value, because the shared UI has no opinion about anyone's enum.
        /// </summary>
        public static DropdownField Dropdown(VisualElement parent, string label, List<string> choices,
            int selectedIndex, Action<int> onChanged)
        {
            var drop = new DropdownField(label ?? "", choices ?? new List<string>(), selectedIndex,
                Format, Format);
            if (GameTree)
            {
                drop.AddToClassList("setting-dropdown");
            }
            else
            {
                drop.style.color = UiTheme.Text;
                drop.style.fontSize = 13f;
            }
            drop.style.marginLeft = 4f;
            drop.style.marginRight = 4f;
            drop.style.marginTop = 2f;
            drop.style.marginBottom = 2f;
            drop.style.flexGrow = 1f;
            if (!GameTree)
            {
                drop.style.height = UiTheme.Row;
            }
            if (drop.labelElement != null)
            {
                if (!GameTree)
                {
                    drop.labelElement.style.color = UiTheme.TextMuted;
                    drop.labelElement.style.fontSize = 12f;
                }
                drop.labelElement.style.unityTextAlign = TextAnchor.MiddleLeft;
                if (!GameTree)
                {
                    drop.labelElement.style.minWidth = 150f;
                }
            }

            if (onChanged != null)
            {
                drop.RegisterValueChangedCallback(evt =>
                {
                    try
                    {
                        onChanged(drop.index);
                    }
                    catch (Exception ex)
                    {
                        UiLog.Error("A dropdown handler threw: " + ex);
                    }
                });
            }

            Touch(parent, drop);
            return drop;
        }

        private static string Format(string value)
        {
            return value ?? "";
        }

        /// <summary>A collapsible section, for the long tail of a mod's settings.</summary>
        public static Foldout Foldout(VisualElement parent, string text, bool open)
        {
            var fold = new Foldout { text = text ?? "", value = open };
            if (!GameTree)
            {
                fold.style.color = UiTheme.Text;
                fold.style.fontSize = 13f;
            }
            fold.style.marginLeft = 2f;
            fold.style.marginTop = 3f;
            fold.style.marginBottom = 2f;
            Touch(parent, fold);
            return fold;
        }

        /// <summary>An informational note.</summary>
        public static VisualElement Note(VisualElement parent, string text)
        {
            return Note(parent, text, false, false);
        }

        /// <summary>A note in the warning colour: worth reading, not broken.</summary>
        public static VisualElement Warning(VisualElement parent, string text)
        {
            return Note(parent, text, true, false);
        }

        /// <summary>A note in the error colour: something did not work.</summary>
        public static VisualElement Error(VisualElement parent, string text)
        {
            return Note(parent, text, false, true);
        }

        /// <summary>A coloured note. Warning wins over error if both are somehow set.</summary>
        public static VisualElement Note(VisualElement parent, string text, bool warning, bool error)
        {
            var box = new VisualElement();
            box.style.flexDirection = FlexDirection.Row;
            box.style.backgroundColor = UiTheme.Panel;
            box.style.borderTopLeftRadius = 4f;
            box.style.borderTopRightRadius = 4f;
            box.style.borderBottomLeftRadius = 4f;
            box.style.borderBottomRightRadius = 4f;
            box.style.paddingLeft = 8f;
            box.style.paddingRight = 8f;
            box.style.paddingTop = 5f;
            box.style.paddingBottom = 5f;
            box.style.marginLeft = 4f;
            box.style.marginRight = 4f;
            box.style.marginTop = 3f;
            box.style.marginBottom = 3f;

            var label = new Label(text ?? "");
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.flexGrow = 1f;
            label.style.unityTextAlign = TextAnchor.UpperLeft;
            if (!GameTree)
            {
                label.style.fontSize = 12f;
            }
            if (error) label.style.color = UiTheme.Error;
            else if (warning) label.style.color = UiTheme.Warning;
            else label.style.color = UiTheme.TextMuted;
            box.Add(label);

            Touch(parent, box);
            return box;
        }

        /// <summary>A label and a value on one line, the workhorse of a read-only info page.</summary>
        public static VisualElement FieldRow(VisualElement parent, string label, string value)
        {
            var row = Row(parent);
            if (GameTree)
            {
                row.AddToClassList("setting-row");
                row.AddToClassList("setting-row--compact");
            }
            else
            {
                row.style.minHeight = UiTheme.Row;
            }
            row.style.marginLeft = 4f;
            row.style.marginRight = 4f;
            row.style.overflow = Overflow.Hidden;

            var name = new Label(label ?? "");
            if (GameTree)
            {
                name.AddToClassList("setting-label");
            }
            else
            {
                name.style.color = UiTheme.TextMuted;
                name.style.fontSize = 12f;
            }
            name.style.unityTextAlign = TextAnchor.MiddleLeft;
            if (!GameTree)
            {
                name.style.minWidth = 150f;
            }
            name.style.flexShrink = 0f;
            row.Add(name);

            var content = new Label(value ?? "");
            if (GameTree)
            {
                content.AddToClassList("setting-value");
            }
            else
            {
                content.style.color = UiTheme.Text;
                content.style.fontSize = 12f;
            }
            content.style.whiteSpace = WhiteSpace.Normal;
            content.style.unityTextAlign = TextAnchor.MiddleLeft;
            content.style.flexGrow = 1f;
            content.style.flexShrink = 1f;
            content.style.flexBasis = 0f;
            row.Add(content);

            return row;
        }

        /// <summary>
        /// A button that shows a tooltip explaining what it will do. Used where a short label is
        /// ambiguous, which in a menu full of "Apply" buttons it usually is.
        /// </summary>
        public static Button DescribedButton(VisualElement parent, string text, string tooltip, Action onClick)
        {
            var button = Button(parent, text, onClick);
            button.tooltip = tooltip;
            return button;
        }

        /// <summary>A button styled as the primary action in a row of them.</summary>
        public static Button PrimaryButton(VisualElement parent, string text, Action onClick)
        {
            var button = Button(parent, text, onClick);
            button.style.backgroundColor = UiTheme.Accent;
            button.style.color = new Color(1f, 1f, 1f);
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            return button;
        }

        /// <summary>Style a button as currently selected. Used by the tab strip.</summary>
        public static void SetSelected(VisualElement button, bool selected)
        {
            if (button == null) return;
            button.style.backgroundColor = selected ? UiTheme.Accent : UiTheme.Button;
            button.style.color = selected ? new Color(1f, 1f, 1f) : UiTheme.Text;
            button.style.unityFontStyleAndWeight = selected ? FontStyle.Bold : FontStyle.Normal;
        }

        /// <summary>Add to a parent without caring whether the parent exists yet.</summary>
        private static void Touch(VisualElement parent, VisualElement child)
        {
            if (parent != null) parent.Add(child);
        }
    }
}