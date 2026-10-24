// Runtime checks for the window geometry rules.
//
// The clamping that keeps a window on screen and above a minimum size is the one part of the resize
// handling that is arithmetic rather than pointer plumbing, and arithmetic is where an off-by-one hides: a
// window that can be dragged out of reach or shrunk to nothing is not obvious from reading the drag code.
//
// These compile the real WindowGeometry.cs, not a copy, so they cannot drift from what ships.

using System;
using ScamWYF.Modding.Core.Ui;

internal static class WindowGeometryTests
{
    private const float MinWidth = 360f;
    private const float MinHeight = 220f;

    private static int _passed;
    private static int _failed;

    private static void Check(string name, bool condition, string detail)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine("  PASS  " + name);
            return;
        }

        _failed++;
        Console.WriteLine("  FAIL  " + name + (detail.Length > 0 ? " - " + detail : ""));
    }

    public static int Main()
    {
        // A size inside the limits is left exactly as asked.
        var normal = WindowGeometry.Clamp(800f, 500f, MinWidth, MinHeight, 1920f, 1080f);
        Check("a size inside the limits is kept",
            Approx(normal.Width, 800f) && Approx(normal.Height, 500f), normal.ToString());

        // Below the minimum it grows back rather than shrinking to nothing.
        var tiny = WindowGeometry.Clamp(100f, 50f, MinWidth, MinHeight, 1920f, 1080f);
        Check("too small clamps up to the minimum",
            Approx(tiny.Width, MinWidth) && Approx(tiny.Height, MinHeight), tiny.ToString());

        // Only the offending axis moves.
        var squat = WindowGeometry.Clamp(800f, 10f, MinWidth, MinHeight, 1920f, 1080f);
        Check("only the offending axis is clamped",
            Approx(squat.Width, 800f) && Approx(squat.Height, MinHeight), squat.ToString());

        // Beyond the screen it stops short, leaving the title bar reachable.
        var huge = WindowGeometry.Clamp(5000f, 4000f, MinWidth, MinHeight, 1920f, 1080f);
        Check("too large clamps to the screen minus the title bar margin",
            Approx(huge.Width, 1920f - WindowGeometry.TitleBarMargin) &&
            Approx(huge.Height, 1080f - WindowGeometry.TitleBarMargin), huge.ToString());

        // A screen smaller than the minimum must not produce something smaller than the minimum.
        var cramped = WindowGeometry.Clamp(900f, 700f, MinWidth, MinHeight, 320f, 240f);
        Check("a screen smaller than the minimum still yields the minimum",
            Approx(cramped.Width, MinWidth) && Approx(cramped.Height, MinHeight), cramped.ToString());

        // Nonsense input does not produce a nonsense size.
        var noScreen = WindowGeometry.Clamp(900f, 700f, MinWidth, MinHeight, 0f, 0f);
        Check("an unknown screen size falls back to the minimum",
            Approx(noScreen.Width, MinWidth) && Approx(noScreen.Height, MinHeight), noScreen.ToString());

        var negative = WindowGeometry.Clamp(-50f, -50f, MinWidth, MinHeight, 1920f, 1080f);
        Check("a negative drag clamps to the minimum",
            Approx(negative.Width, MinWidth) && Approx(negative.Height, MinHeight), negative.ToString());

        // Idempotence: clamping an already-clamped size changes nothing. Without this a drag that reports
        // the clamped size back would creep on every frame.
        var once = WindowGeometry.Clamp(5000f, 4000f, MinWidth, MinHeight, 1920f, 1080f);
        var twice = WindowGeometry.Clamp(once.Width, once.Height, MinWidth, MinHeight, 1920f, 1080f);
        Check("clamping twice changes nothing",
            Approx(twice.Width, once.Width) && Approx(twice.Height, once.Height),
            once + " then " + twice);

        // Positions: a window can be moved, but its title bar can never leave the screen entirely.
        // Off the right or below, the whole title bar comes back; off the left, only a strip does, which
        // is deliberate: a window wider than the screen still has to be grabbable.
        float x = 5000f, y = 5000f;
        WindowGeometry.ClampPosition(ref x, ref y, 920f, 580f, 1920f, 1080f);
        Check("dragging off the right or bottom is pulled back on screen",
            Approx(x, 1920f - WindowGeometry.TitleBarMargin) &&
            Approx(y, 1080f - WindowGeometry.TitleBarMargin),
            "(" + x + ", " + y + ")");

        float leftX = -5000f, leftY = 100f;
        WindowGeometry.ClampPosition(ref leftX, ref leftY, 920f, 580f, 1920f, 1080f);
        Check("dragging left leaves a grabbable strip on screen",
            Approx(leftX + 920f, WindowGeometry.TitleBarMargin), "x=" + leftX);

        float saneX = 100f, saneY = 100f;
        WindowGeometry.ClampPosition(ref saneX, ref saneY, 920f, 580f, 1920f, 1080f);
        Check("a position already on screen is untouched", Approx(saneX, 100f) && Approx(saneY, 100f),
            "(" + saneX + ", " + saneY + ")");

        // An unknown screen size must not throw or move anything.
        float unknownX = 50f, unknownY = 60f;
        WindowGeometry.ClampPosition(ref unknownX, ref unknownY, 920f, 580f, 0f, 0f);
        Check("an unknown screen size leaves the position alone",
            Approx(unknownX, 50f) && Approx(unknownY, 60f), "(" + unknownX + ", " + unknownY + ")");

        Console.WriteLine();
        Console.WriteLine(_passed + " passed, " + _failed + " failed");
        return _failed == 0 ? 0 : 1;
    }

    private static bool Approx(float actual, float expected)
    {
        return Math.Abs(actual - expected) < 0.01f;
    }
}