using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace ScamWYF.Modding.Core.Ui
{
    /// <summary>
    /// Drag-to-resize handling for a window: a grip in the bottom-right corner, plus the arithmetic
    /// that keeps a window inside the screen and above a minimum size.
    /// </summary>
    /// <remarks>
    /// UI Toolkit has no built-in resizable window, and the menu needs one: a settings form with thirty
    /// entries is unreadable in a fixed 920x580, and a mod that opens its own window has no idea how much
    /// room it needs either.
    ///
    /// The corner grip is the least fiddly of the alternatives and is what people expect. Edge grips are
    /// fiddlier to hit and pull in two directions at once; a size field in the title bar is precise but
    /// needs a keyboard.
    ///
    /// The arithmetic lives here rather than in UiWindow so it can be reasoned about - and tested -
    /// without a panel, a theme or a game.
    /// </remarks>
    internal sealed class ResizeGrip
    {
        private readonly VisualElement _window;
        private readonly VisualElement _grip;
        private readonly float _minWidth;
        private readonly float _minHeight;

        private bool _resizing;
        private int _pointer = -1;
        private Vector2 _startWidth;
        private Vector2 _startHeight;
        private Vector2 _grabOffset;

        /// <summary>Called after every accepted resize, so a window can persist its size.</summary>
        public Action<float, float> Resized;

        public ResizeGrip(VisualElement window, float minWidth, float minHeight)
        {
            _window = window;
            _minWidth = minWidth;
            _minHeight = minHeight;

            _grip = new VisualElement();
            _grip.name = "resize-grip";
            _grip.style.position = Position.Absolute;
            _grip.style.right = 0f;
            _grip.style.bottom = 0f;
            _grip.style.width = GripSize;
            _grip.style.height = GripSize;
            _grip.style.backgroundColor = UiTheme.WindowBorder;

            // Three diagonal bars, drawn with borders rather than a texture: there is no sprite to ship
            // with a source-only library, and three thin lines are the conventional grip anyway.
            for (int i = 0; i < 3; i++)
            {
                var bar = new VisualElement();
                bar.style.position = Position.Absolute;
                bar.style.right = 3f + i * 4f;
                bar.style.bottom = 3f + i * 4f;
                bar.style.width = 12f - i * 4f;
                bar.style.height = 1f;
                bar.style.backgroundColor = UiTheme.TextMuted;
                bar.style.opacity = 0.6f;
                bar.pickingMode = PickingMode.Ignore;
                _grip.Add(bar);
            }

            // The grip sits over the window's bottom-right corner, so it has to take the pointer.
            _grip.pickingMode = PickingMode.Position;
            _grip.tooltip = "Drag to resize";

            _grip.RegisterCallback<PointerDownEvent>(OnDown);
            _grip.RegisterCallback<PointerMoveEvent>(OnMove);
            _grip.RegisterCallback<PointerUpEvent>(OnUp);

            window.Add(_grip);
        }

        /// <summary>How large the corner of the window the grip occupies. Big enough to grab.</summary>
        public static readonly float GripSize = 18f;

        public VisualElement Element
        {
            get { return _grip; }
        }

        /// <summary>Release the pointer if a drag is in progress, so a lost pointer cannot stick.</summary>
        public void Cancel()
        {
            _resizing = false;
            _pointer = -1;
        }

        private void OnDown(PointerDownEvent evt)
        {
            _resizing = true;
            _pointer = evt.pointerId;

            _startWidth = new Vector2(_window.resolvedStyle.width, _window.resolvedStyle.height);
            _grabOffset = new Vector2(evt.position.x, evt.position.y);

            _grip.CapturePointer(evt.pointerId);
            evt.StopPropagation();
            evt.StopImmediatePropagation();
        }

        private void OnMove(PointerMoveEvent evt)
        {
            if (!_resizing || evt.pointerId != _pointer) return;

            // Dragging left or up means growing, so the delta is taken from the grab point rather than
            // accumulated frame to frame - no drift, and a stuttering pointer cannot make it walk.
            var width = _startWidth.x + (evt.position.x - _grabOffset.x);
            var height = _startWidth.y + (evt.position.y - _grabOffset.y);

            var room = ScreenRoom();
            var size = WindowGeometry.Clamp(width, height, _minWidth, _minHeight, room.x, room.y);
            _window.style.width = size.Width;
            _window.style.height = size.Height;

            if (Resized != null) Resized(size.Width, size.Height);
            evt.StopPropagation();
        }

        private void OnUp(PointerUpEvent evt)
        {
            if (!_resizing || evt.pointerId != _pointer) return;

            _resizing = false;
            _pointer = -1;
            _grip.ReleasePointer(evt.pointerId);
        }

        /// <summary>How much of the screen is available, so a window cannot be resized off it.</summary>
        private static Vector2 ScreenRoom()
        {
            try
            {
                return new Vector2(Math.Max(320f, Screen.width), Math.Max(240f, Screen.height));
            }
            catch (Exception)
            {
                // Screen can be unavailable in odd contexts; a generous default is better than refusing
                // to resize at all.
                return new Vector2(1920f, 1080f);
            }
        }
    }
}