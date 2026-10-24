using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ScamWYF.Modding.Core.Ui
{
    /// <summary>One mod's own window in the shared overlay.</summary>
    /// <remarks>
    /// Most mods will not need this. The universal menu gives each of them a tab, which is where
    /// settings and status belong, so a separate window is only worth having for something that has to
    /// be visible while the game is being played - an overlay, a debug readout, a cheat panel.
    ///
    /// It exists because the old IMGUI host did, and dropping it would mean every mod that wants a
    /// floating overlay now has to create its own GameObject and OnGUI, which is the exact problem the
    /// library exists to prevent.
    /// </remarks>
    public sealed class UiWindow
    {
        private readonly string _ownerId;
        private VisualElement _titleBar;
        private Label _titleLabel;
        private Button _closeButton;
        private ResizeGrip _resize;
        private bool _dragging;
        private int _dragPointer = -1;
        private Vector2 _grabOffset;

        private float _defaultX;
        private float _defaultY;
        private float _defaultWidth;
        private float _defaultHeight;

        /// <summary>
        /// Smallest the window may be dragged. Below this the title bar and a couple of rows stop fitting,
        /// and a window that can be shrunk to nothing cannot be found again.
        /// </summary>
        private const float MinWidth = 360f;
        private const float MinHeight = 220f;

        /// <summary>The window's root element. Add content to <see cref="Body"/> instead.</summary>
        public VisualElement Element { get; private set; }

        /// <summary>The content area, sized by flex.</summary>
        public VisualElement Body { get; private set; }

        /// <summary>A strip along the bottom, for buttons.</summary>
        public VisualElement Footer { get; private set; }

        /// <summary>Why this window could not be created, or null when it exists.</summary>
        public string UnavailableReason { get; private set; }

        public string Title { get; private set; }

        /// <summary>
        /// The config key this window's geometry is remembered under, or null when it is not remembered.
        /// </summary>
        /// <remarks>
        /// Only the library's own windows persist their geometry. A mod's overlay is positioned by the mod
        /// and may well be full-screen, so remembering where it was would be wrong rather than helpful.
        /// Computed once, because Save is called on every drag release and rebuilding a string each time
        /// would be waste.
        /// </remarks>
        private string PlacementKey { get; set; }

        /// <summary>The mod this window belongs to. Used to hide one mod's windows without touching others.</summary>
        public string OwnerId
        {
            get { return _ownerId; }
        }

        public bool Visible
        {
            get { return Element != null && Element.style.display == DisplayStyle.Flex; }
            set
            {
                if (Element == null) return;
                Element.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
                if (!value) return;

                UiPanel.ShowOverlay();
                Focus();
            }
        }

        /// <summary>Called when the user closes the window with its close button.</summary>
        public Action OnClosed;

        internal static UiWindow Create(string ownerId, string title, float x, float y, float width, float height)
        {
            var window = new UiWindow(ownerId, title, x, y, width, height);
            window.AttachResize();
            return window;
        }

        private UiWindow(string ownerId, string title, float x, float y, float width, float height)
        {
            _ownerId = ownerId;
            Title = title;

            // Remembered geometry wins over the requested one, so a window reopens where it was left.
            // Only for windows the library owns by name: a mod's own overlay is positioned by the mod.
            if (ownerId == "library" || ownerId == LibraryRuntime.LibraryId)
            {
                PlacementKey = "window." + WindowPlacement.SafeKey(title);
                WindowPlacement.Load(PlacementKey, ref x, ref y, ref width, ref height);
            }

            _defaultX = x;
            _defaultY = y;
            _defaultWidth = width;
            _defaultHeight = height;

            var overlay = Widgets.Overlay;
            if (overlay == null)
            {
                UnavailableReason = UiPanel.UnavailableReason ?? "the shared UI could not be created";
                return;
            }

            Element = new VisualElement();
            Element.name = "window:" + ownerId;
            Element.style.position = Position.Absolute;
            Element.style.left = x;
            Element.style.top = y;
            Element.style.width = width;
            Element.style.height = height;
            Element.style.backgroundColor = UiTheme.Window;
            Element.style.borderTopLeftRadius = 6f;
            Element.style.borderTopRightRadius = 6f;
            Element.style.borderBottomLeftRadius = 6f;
            Element.style.borderBottomRightRadius = 6f;
            Element.style.borderLeftWidth = UiTheme.BorderWidth;
            Element.style.borderRightWidth = UiTheme.BorderWidth;
            Element.style.borderTopWidth = UiTheme.BorderWidth;
            Element.style.borderBottomWidth = UiTheme.BorderWidth;
            Element.style.borderLeftColor = UiTheme.WindowBorder;
            Element.style.borderRightColor = UiTheme.WindowBorder;
            Element.style.borderTopColor = UiTheme.WindowBorder;
            Element.style.borderBottomColor = UiTheme.WindowBorder;
            Element.style.overflow = Overflow.Hidden;
            Element.style.flexDirection = FlexDirection.Column;
            Element.style.display = DisplayStyle.None;

            // A click anywhere in the window brings it forward, so overlapping windows behave the way a
            // person expects rather than the way they happened to be created.
            Element.RegisterCallback<PointerDownEvent>(delegate { Focus(); });

            BuildTitleBar();

            Body = new VisualElement();
            Body.style.flexDirection = FlexDirection.Column;
            Body.style.flexGrow = 1f;
            Body.style.flexShrink = 1f;
            Body.style.paddingLeft = 4f;
            Body.style.paddingRight = 4f;
            Body.style.paddingTop = 2f;
            Body.style.paddingBottom = 2f;
            Body.style.overflow = Overflow.Hidden;
            Element.Add(Body);

            Footer = new VisualElement();
            Footer.style.flexDirection = FlexDirection.Row;
            Footer.style.alignItems = Align.Center;
            Footer.style.flexShrink = 0f;
            Footer.style.paddingLeft = 4f;
            Footer.style.paddingRight = 4f;
            Footer.style.paddingTop = 3f;
            Footer.style.paddingBottom = 3f;
            Element.Add(Footer);

            overlay.Add(Element);
        }

        public void SetTitle(string title)
        {
            Title = title;
            if (_titleLabel != null) _titleLabel.text = title ?? "";
        }

        /// <summary>Bring the window to the front of the overlay.</summary>
        public void Focus()
        {
            if (Element != null) Element.BringToFront();
        }

        /// <summary>Empty the body, so a page can be rebuilt without leaving old content behind.</summary>
        public void ClearBody()
        {
            if (Body != null) Body.Clear();
        }

        public void Close()
        {
            Visible = false;
            RememberGeometry();

            // Nothing left on screen means the overlay can go, and with it its full-screen element that
            // would otherwise sit over the game intercepting nothing but costing a repaint.
            if (!UiWindows.AnyVisible) UiPanel.HideOverlay();

            if (OnClosed != null) OnClosed();
        }

        /// <summary>
        /// Attach the resize grip, if it is not already there. Separated from the constructor so the
        /// grip is only built for a window that actually got built.
        /// </summary>
        private void AttachResize()
        {
            if (Element == null || _resize != null) return;

            _resize = new ResizeGrip(Element, MinWidth, MinHeight);
            _resize.Resized = OnResized;

            // The grip sits in the corner, which would otherwise be the last few pixels of the scroll
            // view. Padding the body keeps content clear of it.
            Body.style.paddingRight = ResizeGrip.GripSize;
            Body.style.paddingBottom = 2f;
        }

        private void OnResized(float width, float height)
        {
            // Remembered on resize, not on close: a window that is dragged and then left open should
            // still come back the right size next time.
            RememberGeometry(width, height);
            if (Element != null) Element.MarkDirtyRepaint();
        }

        private void RememberGeometry()
        {
            if (Element == null) return;

            var width = Element.resolvedStyle.width > 0f ? Element.resolvedStyle.width : _defaultWidth;
            var height = Element.resolvedStyle.height > 0f ? Element.resolvedStyle.height : _defaultHeight;
            RememberGeometry(width, height);
        }

        private void RememberGeometry(float width, float height)
        {
            if (_ownerId != "library" && _ownerId != LibraryRuntime.LibraryId) return;
            if (Element == null) return;

            // resolvedStyle is a computed value; the left/top actually in effect are the ones set.
            var x = ReadLength(Element.style.left, _defaultX);
            var y = ReadLength(Element.style.top, _defaultY);
            WindowPlacement.Save(PlacementKey, x, y, width, height);
        }

        /// <summary>The pixel value of a style length, or the fallback when it is unset or in percent.</summary>
        private static float ReadLength(StyleLength value, float fallback)
        {
            if (value.keyword != StyleKeyword.Null && value.keyword != StyleKeyword.Undefined) return fallback;
            try
            {
                var pixels = value.value.value;
                return pixels > 0f ? pixels : fallback;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        /// <summary>Reset the window to the size and position it was created with.</summary>
        public void ResetGeometry()
        {
            if (Element == null) return;

            Element.style.left = _defaultX;
            Element.style.top = _defaultY;
            Element.style.width = _defaultWidth;
            Element.style.height = _defaultHeight;

            // Wipe what was remembered too, or the next open would restore the size just discarded.
            WindowPlacement.Forget(PlacementKey);
            Element.MarkDirtyRepaint();
        }

        /// <summary>Take the window off the overlay. Called for you when a mod unloads.</summary>
        public void Destroy()
        {
            RememberGeometry();
            if (_resize != null) _resize.Cancel();

            if (Element != null) Element.RemoveFromHierarchy();

            Element = null;
            Body = null;
            Footer = null;
            _resize = null;
            _titleBar = null;
            _titleLabel = null;
            _closeButton = null;
        }

        private void BuildTitleBar()
        {
            _titleBar = new VisualElement();
            _titleBar.style.flexDirection = FlexDirection.Row;
            _titleBar.style.alignItems = Align.Center;
            _titleBar.style.flexShrink = 0f;
            _titleBar.style.height = UiTheme.RowHeight + 8f;
            _titleBar.style.paddingLeft = 8f;
            _titleBar.style.paddingRight = 4f;
            _titleBar.style.backgroundColor = UiTheme.Panel;
            // The bar is the drag handle, so it has to take the pointer rather than ignore it.
            _titleBar.pickingMode = PickingMode.Position;
            _titleBar.RegisterCallback<PointerDownEvent>(OnTitleDown);
            _titleBar.RegisterCallback<PointerMoveEvent>(OnTitleMove);
            _titleBar.RegisterCallback<PointerUpEvent>(OnTitleUp);

            _titleLabel = new Label(Title ?? "");
            _titleLabel.style.color = UiTheme.Text;
            _titleLabel.style.fontSize = 13f;
            _titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _titleLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            _titleLabel.style.flexGrow = 1f;
            _titleLabel.pickingMode = PickingMode.Ignore;
            _titleBar.Add(_titleLabel);

            _closeButton = new Button(Close) { text = "X" };
            _closeButton.style.width = UiTheme.RowHeight + 4f;
            _closeButton.style.height = UiTheme.RowHeight;
            _closeButton.style.fontSize = 11f;
            _closeButton.style.color = UiTheme.TextMuted;
            _closeButton.style.marginLeft = 2f;
            _titleBar.Add(_closeButton);

            Element.Add(_titleBar);
        }

        private void OnTitleDown(PointerDownEvent evt)
        {
            // A click on the close button is a click on the button, not the start of a drag.
            if (_closeButton != null && _closeButton.worldBound.Contains(evt.position)) return;

            // The corner belongs to the resize grip, not the drag handle.
            if (_resize != null && _resize.Element.worldBound.Contains(evt.position)) return;

            Focus();

            var origin = Element.worldBound;
            _grabOffset = new Vector2(evt.position.x - origin.x, evt.position.y - origin.y);
            _dragging = true;
            _dragPointer = evt.pointerId;
            _titleBar.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnTitleMove(PointerMoveEvent evt)
        {
            if (!_dragging || evt.pointerId != _dragPointer) return;

            var parent = Element.parent;
            if (parent == null) return;

            var bounds = parent.worldBound;
            var width = Element.resolvedStyle.width;
            var height = Element.resolvedStyle.height;
            if (width <= 0f) width = Element.worldBound.width;
            if (height <= 0f) height = Element.worldBound.height;

            // Keep part of the title bar reachable, so a window cannot be dragged off-screen and lost
            // for the rest of the session.
            var maxX = bounds.width - 60f;
            var maxY = bounds.height - 24f;
            var left = evt.position.x - _grabOffset.x;
            var top = evt.position.y - _grabOffset.y;

            if (left < -width + 60f) left = -width + 60f;
            if (left > maxX) left = maxX;
            if (top < 0f) top = 0f;
            if (top > maxY) top = maxY;

            Element.style.left = left;
            Element.style.top = top;
            evt.StopPropagation();
        }

        private void OnTitleUp(PointerUpEvent evt)
        {
            if (!_dragging || evt.pointerId != _dragPointer) return;

            _dragging = false;
            _dragPointer = -1;
            _titleBar.ReleasePointer(evt.pointerId);
        }
    }
}