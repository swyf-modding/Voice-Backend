using System;
using HarmonyLib;

namespace ScamWYF.Modding.Core.Ui
{
    /// <summary>
    /// Plays the game's own click sound for actions the native menu surfaces take.
    /// </summary>
    /// <remarks>
    /// Every button the game binds goes through <c>AudioSingleton.Instance.PlayClickSfx()</c>, and a
    /// modded button that silently clicks where everything else clicks is one more small thing that
    /// says "I do not belong here". Reached by reflection for the same reason as everywhere else in
    /// this folder: the library builds without game assemblies.
    /// </remarks>
    internal static class GameSounds
    {
        private static bool _reported;

        public static void Click()
        {
            try
            {
                var audioType = AccessTools.TypeByName("AudioSingleton");
                if (audioType == null) return;

                var instanceProperty = AccessTools.Property(audioType, "Instance");
                var instance = instanceProperty != null
                    ? instanceProperty.GetValue(null)
                    : AccessTools.Field(audioType, "Instance")?.GetValue(null);
                if (instance == null) return;

                var method = AccessTools.Method(audioType, "PlayClickSfx");
                if (method == null) return;
                method.Invoke(instance, null);
            }
            catch (Exception ex)
            {
                // A sound failing must never fail a menu action; say it once and move on.
                if (!_reported)
                {
                    _reported = true;
                    UiLog.Warn("Could not play the game's click sound: " + ex.Message);
                }
            }
        }
    }
}
