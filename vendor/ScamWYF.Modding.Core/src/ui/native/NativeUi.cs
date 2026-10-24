using System;
using UnityEngine;

namespace ScamWYF.Modding.Core.Ui
{
    /// <summary>
    /// The mod menu's entry points inside the game's own menus: the MODS button on the title menu
    /// and the MODS button beside PRESS KIT on the settings page, plus the per-frame heartbeat that
    /// keeps the cloned menu alive.
    /// </summary>
    internal static class NativeUi
    {
        private static bool _installed;

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;

            // One per-frame heartbeat for both entry points and the menu's show/hide + escape
            // handling, running off the shared UI runner.
            UiPanel.AddTicker(Tick);
        }

        private static void Tick()
        {
            try
            {
                MainMenuNative.Tick();
                PauseMenuNative.Tick();
                SettingsMenu.Tick();
            }
            catch (Exception ex)
            {
                if (!_tickFailed)
                {
                    _tickFailed = true;
                    UiLog.Error("The native menu entry points threw: " + ex);
                }
            }
        }

        private static bool _tickFailed;
    }
}