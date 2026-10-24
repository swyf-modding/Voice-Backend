using System;
using System.Reflection;
using HarmonyLib;

namespace ScamWYF.Modding.Core
{
    /// <summary>
    /// Targeted workarounds for defects in the shipped game build that would otherwise flood the
    /// log or break every session.
    /// </summary>
    /// <remarks>
    /// Each workaround proves the defect at runtime before changing anything, touches only the
    /// one broken method, and does nothing on a build where the defect is gone. A workaround that
    /// is hiding something shows up in the log saying exactly what it suppressed and why, so a
    /// future game update that fixes the underlying defect is visible as the workaround going
    /// quiet rather than as mysterious behaviour.
    ///
    /// These are installed by the library itself rather than by any mod, because the defect hits
    /// every session no matter which mods are installed. They use their own Harmony instance
    /// under the library's id for the same reason.
    /// </remarks>
    public static class GameWorkarounds
    {
        private static readonly object Gate = new object();
        private static Harmony _harmony;
        private static bool _hudGuardInstalled;
        private static volatile bool _hudHintBroken;
        private static bool _hudHintReported;

        /// <summary>
        /// True once the HUD menu-hint bind has proven itself broken this session and is being
        /// skipped. False on a healthy build, where nothing is patched around.
        /// </summary>
        public static bool HudHintBindingSuppressed
        {
            get { return _hudHintBroken; }
        }

        /// <summary>
        /// Stops ScammerGameHudController.BindPlayerMenuHint from throwing a TypeLoadException on
        /// every call on builds whose Unity.InputSystem surface cannot resolve DisplayStringOptions.
        /// </summary>
        /// <remarks>
        /// The bind builds the small "TAB / FOR PLAYERS" hint. On the affected builds the method
        /// cannot JIT at all - the missing type is referenced before its first instruction - so it
        /// can never accomplish anything, and its callers retry it every frame. Skipping it is
        /// therefore strictly equivalent to letting it throw, minus one exception per frame.
        ///
        /// Nothing is assumed up front: the first call still runs the original, and only a
        /// TypeLoadException arms the skip. Any other exception propagates untouched, and a build
        /// where the bind succeeds never arms anything.
        /// </remarks>
        public static void EnsureHudHintBindingGuard()
        {
            lock (Gate)
            {
                if (_hudGuardInstalled) return;
                _hudGuardInstalled = true;
            }

            MethodBase target = null;
            try
            {
                var controllerType = AccessTools.TypeByName("ScammerGameHudController");
                if (controllerType == null) return;
                target = FindPrivateInstance(controllerType, "BindPlayerMenuHint", 0);
            }
            catch (Exception)
            {
                return;
            }

            if (target == null) return;

            try
            {
                if (_harmony == null) _harmony = new Harmony(LibraryRuntime.LibraryId);
                _harmony.Patch(target,
                    new HarmonyMethod(AccessTools.Method(typeof(GameWorkarounds), "HudHintGuardPrefix")),
                    null, null,
                    new HarmonyMethod(AccessTools.Method(typeof(GameWorkarounds), "HudHintGuardFinalizer")),
                    null);
            }
            catch (Exception ex)
            {
                LibraryRuntime.Log.LogWarning("Could not guard the HUD menu-hint bind: " + ex.Message +
                                              ". The per-frame TypeLoadException stays.");
            }
        }

        /// <summary>Skips the bind once it has proven itself broken. Cheap: one volatile read.</summary>
        public static bool HudHintGuardPrefix()
        {
            return !_hudHintBroken;
        }

        /// <summary>
        /// Arms the skip on the first TypeLoadException and swallows it; anything else passes
        /// through so real bugs stay visible.
        /// </summary>
        public static Exception HudHintGuardFinalizer(Exception __exception)
        {
            if (!(__exception is TypeLoadException)) return __exception;

            _hudHintBroken = true;
            if (!_hudHintReported)
            {
                _hudHintReported = true;
                LibraryRuntime.Log.LogWarning(
                    "ScammerGameHudController.BindPlayerMenuHint cannot resolve DisplayStringOptions " +
                    "in this build (" + GameBuild.Describe() + "), so the player-menu hint bind can " +
                    "never succeed. It is now skipped instead of throwing every frame. If a game " +
                    "update fixes the InputSystem surface this guard will simply never arm.");
            }
            return null;
        }

        private static MethodBase FindPrivateInstance(Type type, string methodName, int paramCount)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.Static;
            foreach (var candidate in type.GetMethods(flags))
            {
                if (candidate.Name != methodName) continue;
                if (candidate.IsStatic) continue;
                if (candidate.GetParameters().Length != paramCount) continue;
                return candidate;
            }
            return null;
        }
    }
}
