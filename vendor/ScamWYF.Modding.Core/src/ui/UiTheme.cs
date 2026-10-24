using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace ScamWYF.Modding.Core.Ui
{
    /// <summary>
    /// The colours the shared UI draws with, read off the base game's own UI Toolkit theme.
    /// </summary>
    /// <remarks>
    /// The game builds its menus with UI Toolkit, which means the theme stylesheet it ships
    /// already defines a coherent palette: window background, button fills, label text, text field
    /// fills, hover states, outline widths. Unity exposes those as USS custom properties, and they
    /// are readable at runtime through ICustomStyle.
    ///
    /// So rather than guessing colours and hoping they sit well against the game's own menus, this
    /// samples the live theme. A mod window then looks like it belongs to the base game instead of
    /// looking like a foreign grey box dropped on top of it.
    ///
    /// If the theme cannot be read - a stripped build, a game update that renames the variables,
    /// or the menu being built before any themed element has resolved - the fallbacks below are
    /// used. They are the website's palette - the same warm browns, creams and golds the
    /// switchboard and the site use - so a failure here degrades to "plain, but ours" rather than
    /// to "unreadable" and a foreign grey box dropped on top of the game's own menus.
    /// </remarks>
    public static class UiTheme
    {
        // Unity's default runtime theme defines these. Read, never assumed.
        private static readonly CustomStyleProperty<Color> WindowBackground =
            new CustomStyleProperty<Color>("--unity-colors-window-background");

        private static readonly CustomStyleProperty<Color> WindowBorderTop =
            new CustomStyleProperty<Color>("--unity-colors-window-border-top");

        private static readonly CustomStyleProperty<Color> LabelText =
            new CustomStyleProperty<Color>("--unity-colors-label-text");

        private static readonly CustomStyleProperty<Color> ButtonBackground =
            new CustomStyleProperty<Color>("--unity-colors-button-background");

        private static readonly CustomStyleProperty<Color> ButtonHovered =
            new CustomStyleProperty<Color>("--unity-colors-button-background-hovered");

        private static readonly CustomStyleProperty<Color> ButtonChecked =
            new CustomStyleProperty<Color>("--unity-colors-button-background-checked");

        private static readonly CustomStyleProperty<Color> TextFieldBackground =
            new CustomStyleProperty<Color>("--unity-colors-textfield-background");

        private static readonly CustomStyleProperty<Color> TextFieldText =
            new CustomStyleProperty<Color>("--unity-colors-textfield-text");

        private static readonly CustomStyleProperty<Color> ToggleBackground =
            new CustomStyleProperty<Color>("--unity-colors-toggle-background");

        private static readonly CustomStyleProperty<Color> HelpBoxBackground =
            new CustomStyleProperty<Color>("--unity-colors-helpbox-background");

        private static readonly CustomStyleProperty<Color> ErrorText =
            new CustomStyleProperty<Color>("--unity-colors-error-text");

        private static readonly CustomStyleProperty<Color> WarningText =
            new CustomStyleProperty<Color>("--unity-colors-warning-text");

        private static readonly CustomStyleProperty<float> OutlineWidth =
            new CustomStyleProperty<float>("--unity-metrics-outline_width");

        private static readonly CustomStyleProperty<float> SingleLineHeight =
            new CustomStyleProperty<float>("--unity-metrics-single_line_height");

        // Fallbacks. The website's palette, so the in-game menu, the switchboard and the site are
        // one visual system; see the remarks above for when these are used at all.
        private static readonly Color FallbackWindow = new Color(0.078f, 0.055f, 0.043f, 0.97f);
        private static readonly Color FallbackBorder = new Color(0.239f, 0.184f, 0.149f, 0.60f);
        private static readonly Color FallbackText = new Color(0.953f, 0.886f, 0.753f);
        private static readonly Color FallbackMuted = new Color(0.804f, 0.725f, 0.561f);
        private static readonly Color FallbackButton = new Color(0.071f, 0.051f, 0.043f, 1f);
        private static readonly Color FallbackButtonHover = new Color(0.153f, 0.110f, 0.086f, 1f);
        private static readonly Color FallbackActive = new Color(0.902f, 0.671f, 0.243f, 1f);
        private static readonly Color FallbackField = new Color(0.071f, 0.051f, 0.043f, 1f);
        private static readonly Color FallbackError = new Color(1f, 0.478f, 0.400f);
        private static readonly Color FallbackWarning = new Color(0.941f, 0.631f, 0.353f);
        private static readonly Color FallbackPanel = new Color(0.114f, 0.082f, 0.067f, 1f);

        private static bool _sampled;
        private static string _sampledFrom;

        /// <summary>Whether the palette came from the game's theme or from the fallbacks.</summary>
        public static bool IsThemed
        {
            get { return _sampled; }
        }

        /// <summary>Which element the palette was read from, for the log and the About page.</summary>
        public static string Source
        {
            get { return _sampledFrom ?? "built-in fallback palette"; }
        }

        public static Color Window { get; private set; }
        public static Color WindowBorder { get; private set; }
        public static Color Text { get; private set; }
        public static Color TextMuted { get; private set; }
        public static Color Button { get; private set; }
        public static Color ButtonHover { get; private set; }
        public static Color Accent { get; private set; }
        public static Color AccentHover { get; private set; }
        public static Color Field { get; private set; }
        public static Color FieldText { get; private set; }
        public static Color Toggle { get; private set; }
        public static Color Panel { get; private set; }
        public static Color Error { get; private set; }
        public static Color Warning { get; private set; }
        public static float BorderWidth { get; private set; }
        public static float RowHeight { get; private set; }

        /// <summary>
        /// Read the palette off a themed element.
        /// </summary>
        /// <remarks>
        /// Safe to call repeatedly. Once the theme has actually been read the result is kept, so the
        /// palette cannot shift underneath a window that is already open; but until then it is retried,
        /// because USS custom properties only resolve once the element has been through a style pass and
        /// the first attempt can easily land before that has happened.
        /// </remarks>
        public static void Sample(VisualElement probe)
        {
            if (probe == null || (_sampled && _themed)) return;

            // Throttled, because this is offered every frame and a palette that genuinely cannot be read
            // would otherwise mean the same dozen lookups, forever.
            var now = Time.frameCount;
            if (_sampled && now < _nextRetryAt) return;
            _nextRetryAt = now + RetryFrames;

            _themed = false;

            Window = Read(probe, WindowBackground, FallbackWindow);
            WindowBorder = Read(probe, WindowBorderTop, FallbackBorder);
            Text = Read(probe, LabelText, FallbackText);
            TextMuted = Shade(Text, FallbackMuted);
            Button = Read(probe, ButtonBackground, FallbackButton);
            ButtonHover = Read(probe, ButtonHovered, FallbackButtonHover);
            Accent = Read(probe, ButtonChecked, FallbackActive);
            AccentHover = Lighten(Accent, 0.10f);
            Field = Read(probe, TextFieldBackground, FallbackField);
            FieldText = Read(probe, TextFieldText, FallbackText);
            Toggle = Read(probe, ToggleBackground, FallbackButton);
            Panel = Read(probe, HelpBoxBackground, FallbackPanel);
            Error = Read(probe, ErrorText, FallbackError);
            Warning = Read(probe, WarningText, FallbackWarning);
            BorderWidth = ReadFloat(probe, OutlineWidth, 1f);
            RowHeight = ReadFloat(probe, SingleLineHeight, 20f);

            _sampled = true;

            // Whether the theme was actually readable is worth distinguishing from "we have a palette
            // now": the About tab says which, and a mod debugging why its window looks wrong needs to
            // know whether to suspect the theme or its own styling.
            _sampledFrom = _themed
                ? "the base game's UI Toolkit theme"
                : "the built-in fallback palette (the game's theme could not be read)";
        }

        private static bool _themed;

        /// <summary>Frames to wait between attempts once a sample has come back unthemed.</summary>
        private const int RetryFrames = 30;

        private static int _nextRetryAt;

        /// <summary>
        /// Reset to the fallback palette. Used when a themed element is not available, so the
        /// properties are never left at Color.clear on a first read.
        /// </summary>
        public static void Reset()
        {
            _sampled = false;
            _themed = false;
            _sampledFrom = null;
            Window = FallbackWindow;
            WindowBorder = FallbackBorder;
            Text = FallbackText;
            TextMuted = FallbackMuted;
            Button = FallbackButton;
            ButtonHover = FallbackButtonHover;
            Accent = FallbackActive;
            AccentHover = FallbackButtonHover;
            Field = FallbackField;
            FieldText = FallbackText;
            Toggle = FallbackButton;
            Panel = FallbackPanel;
            Error = FallbackError;
            Warning = FallbackWarning;
            BorderWidth = 1f;
            RowHeight = 20f;
        }

        /// <summary>Row height plus padding: the standard vertical rhythm for menu content.</summary>
        public static float Row
        {
            get { return RowHeight + 8f; }
        }

        /// <summary>
        /// A colour part way between <paramref name="reference"/> and
        /// <paramref name="target"/>, so a muted variant always exists even when the theme only
        /// gave us one colour.
        /// </summary>
        public static Color Shade(Color reference, Color target)
        {
            return new Color(
                (reference.r + target.r) * 0.5f,
                (reference.g + target.g) * 0.5f,
                (reference.b + target.b) * 0.5f,
                reference.a);
        }

        /// <summary>The same hue, nudged towards white. For hover states on dark fills.</summary>
        public static Color Lighten(Color color, float amount)
        {
            if (amount < 0f) amount = 0f;
            if (amount > 1f) amount = 1f;
            return new Color(
                color.r + (1f - color.r) * amount,
                color.g + (1f - color.g) * amount,
                color.b + (1f - color.b) * amount,
                color.a);
        }

        /// <summary>The same hue, nudged towards black. For pressed states on dark fills.</summary>
        public static Color Darken(Color color, float amount)
        {
            if (amount < 0f) amount = 0f;
            if (amount > 1f) amount = 1f;
            return new Color(
                color.r * (1f - amount),
                color.g * (1f - amount),
                color.b * (1f - amount),
                color.a);
        }

        private static Color Read(VisualElement element, CustomStyleProperty<Color> property, Color fallback)
        {
            Color value;
            if (element == null) return fallback;
            if (element.customStyle.TryGetValue(property, out value))
            {
                _themed = true;
                return value;
            }
            return fallback;
        }

        private static float ReadFloat(VisualElement element, CustomStyleProperty<float> property, float fallback)
        {
            float value;
            if (element != null && element.customStyle.TryGetValue(property, out value))
            {
                _themed = true;
                return value;
            }
            return fallback;
        }
    }
}