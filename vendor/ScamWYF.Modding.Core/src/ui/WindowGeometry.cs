using System;

namespace ScamWYF.Modding.Core.Ui
{
    /// <summary>
    /// The arithmetic behind dragging a window: keeping it on screen, above a minimum size, and honest
    /// about nonsense input.
    /// </summary>
    /// <remarks>
    /// Split out from <see cref="ResizeGrip"/> because this is the part that is arithmetic rather than
    /// pointer plumbing, and arithmetic is exactly where an off-by-one hides. Clamped here, a window
    /// cannot be dragged out of reach or shrunk to nothing.
    ///
    /// No Unity types on purpose, so it can be tested without an engine - see tests/window.
    /// </remarks>
    internal static class WindowGeometry
    {
        /// <summary>How much of the window the title bar needs kept reachable, in pixels.</summary>
        public const float TitleBarMargin = 24f;

        /// <summary>A width and a height.</summary>
        public struct Size
        {
            public readonly float Width;
            public readonly float Height;

            public Size(float width, float height)
            {
                Width = width;
                Height = height;
            }

            public override string ToString()
            {
                return Width + " x " + Height;
            }
        }

        /// <summary>
        /// Clamp a proposed size into the range a window can usefully occupy.
        /// </summary>
        /// <param name="width">Requested width.</param>
        /// <param name="height">Requested height.</param>
        /// <param name="minWidth">Narrowest useful width.</param>
        /// <param name="minHeight">Shortest useful height.</param>
        /// <param name="screenWidth">Available screen width.</param>
        /// <param name="screenHeight">Available screen height.</param>
        /// <remarks>
        /// The maximum is the screen less a margin, so a window grown too far still has a title bar to
        /// grab. Where the minimum and that maximum cross - a screen smaller than the minimum - the minimum
        /// wins, because a window smaller than its minimum is less useful than one that overhangs.
        ///
        /// Idempotent by construction: clamping an already-clamped size returns it unchanged, which is what
        /// stops a drag from creeping when the pointer reports the clamped size back on each frame.
        /// </remarks>
        public static Size Clamp(float width, float height, float minWidth, float minHeight,
            float screenWidth, float screenHeight)
        {
            if (minWidth < 0f) minWidth = 0f;
            if (minHeight < 0f) minHeight = 0f;

            // A screen of no size - headless, or a mode query that failed - means "no upper limit known",
            // so only the minimum applies.
            var maxWidth = screenWidth > 0f ? screenWidth - TitleBarMargin : minWidth;
            var maxHeight = screenHeight > 0f ? screenHeight - TitleBarMargin : minHeight;

            if (maxWidth < minWidth) maxWidth = minWidth;
            if (maxHeight < minHeight) maxHeight = minHeight;

            if (width < minWidth) width = minWidth;
            if (height < minHeight) height = minHeight;
            if (width > maxWidth) width = maxWidth;
            if (height > maxHeight) height = maxHeight;

            return new Size(width, height);
        }

        /// <summary>
        /// Clamp a proposed position, so a window can be dragged but not lost.
        /// </summary>
        /// <remarks>
        /// The same margin as the size: the title bar has to stay within reach to drag the window back.
        /// </remarks>
        public static void ClampPosition(ref float x, ref float y, float windowWidth, float windowHeight,
            float screenWidth, float screenHeight)
        {
            if (screenWidth <= 0f || screenHeight <= 0f) return;

            var maxX = screenWidth - TitleBarMargin;
            var maxY = screenHeight - TitleBarMargin;
            var minX = -windowWidth + TitleBarMargin;
            var minY = 0f;

            if (maxX < minX) maxX = minX;
            if (x < minX) x = minX;
            if (x > maxX) x = maxX;
            if (y < minY) y = minY;
            if (y > maxY) y = maxY;
        }
    }
}