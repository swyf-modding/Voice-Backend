using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using BepInEx.Logging;
using ScamWYF.Modding.Core.Ui;

namespace ScamWYF.Modding.Core
{
    /// <summary>
    /// Notices when a mod's config file is edited outside the game and reloads it in place.
    /// </summary>
    /// <remarks>
    /// A config file for this game is meant to be edited in a text editor: that is where the
    /// comments and the worked examples live. Reloading means you can change a URL, a model name or
    /// a hotkey while the game is running, press the key again, and see the effect, instead of
    /// editing, relaunching, and finding out you typed it wrong.
    ///
    /// FileSystemWatcher would be the obvious tool, but it is not usable here. Managed stripping has
    /// left System.IO.FileSystemWatcher with only Path and Created in this build - no Changed, no
    /// Renamed, no way to set its filter - so a watcher here would silently never fire. Polling
    /// the file's timestamp costs one stat call a tick and always works.
    ///
    /// The poll also avoids the whole class of "the file changed but has not finished being
    /// written" problems that file-system notifications have: a save is only reported once its
    /// timestamp and length have both been stable for a moment.
    /// </remarks>
    public sealed class ConfigWatcher
    {
        private readonly ConfigFile _file;
        private readonly ManualLogSource _log;
        private readonly string _modId;
        private readonly string _path;
        private readonly double _intervalSeconds;
        private readonly List<Action> _listeners = new List<Action>();

        private DateTime _lastWriteUtc;
        private long _lastLength;
        private double _nextPollAt;
        private double _stableSince;
        private bool _primed;

        /// <summary>How many reloads have happened. Useful in a status page.</summary>
        public int ReloadCount { get; private set; }

        /// <summary>When the file last changed on disk, or null if it has not been seen yet.</summary>
        public DateTime? LastChangeUtc { get; private set; }

        public ConfigWatcher(ConfigFile file, ManualLogSource log, string modId)
            : this(file, log, modId, 0.5d)
        {
        }

        /// <param name="intervalSeconds">How often to look. Half a second is responsive without
        /// being a busy loop, and a caller can ask for something else.</param>
        public ConfigWatcher(ConfigFile file, ManualLogSource log, string modId, double intervalSeconds)
        {
            if (file == null) throw new ArgumentNullException("file");

            _file = file;
            _log = log;
            _modId = modId;
            _intervalSeconds = intervalSeconds < 0.1d ? 0.1d : intervalSeconds;
            _path = SafePath(file);

            // Remember the current state so the first edit is the thing that gets noticed, rather
            // than every existing file being treated as brand new on load.
            _lastWriteUtc = Stamp(_path, out _lastLength);
            _primed = true;

            _file.ConfigReloaded += OnConfigReloaded;
        }

        /// <summary>Called after a reload, with the file already updated.</summary>
        public event Action Reloaded;

        /// <summary>Subscribe without using the event, for callers that would rather not.</summary>
        public void OnReload(Action listener)
        {
            if (listener == null) return;
            lock (_listeners) { _listeners.Add(listener); }
        }

        /// <summary>Stop watching. Safe to call more than once.</summary>
        public void Dispose()
        {
            _file.ConfigReloaded -= OnConfigReloaded;
            lock (_listeners) { _listeners.Clear(); }
        }

        /// <summary>
        /// Check the file. Cheap enough to call every frame; it does nothing until the poll interval
        /// has passed and the file has been stable.
        /// </summary>
        public void Tick()
        {
            if (string.IsNullOrEmpty(_path)) return;

            var now = UtcNow();
            if (now < _nextPollAt) return;
            _nextPollAt = now + _intervalSeconds;

            long length;
            var write = Stamp(_path, out length);

            if (!_primed)
            {
                _primed = true;
                _lastWriteUtc = write;
                _lastLength = length;
                return;
            }

            if (write == _lastWriteUtc && length == _lastLength)
            {
                _stableSince = 0d;
                return;
            }

            // Wait for the file to stop changing. An editor that writes in several steps, or a
            // half-finished copy over the top, would otherwise be read mid-write and produce a
            // config that parses as nonsense.
            if (_stableSince == 0d)
            {
                _stableSince = now;
                return;
            }

            if (now - _stableSince < _intervalSeconds) return;

            _stableSince = 0d;
            _lastWriteUtc = write;
            _lastLength = length;
            LastChangeUtc = write;

            ReloadNow("the file changed on disk");
        }

        /// <summary>
        /// Re-read the file now and tell everyone. Exposed so a mod can force a reload - after it
        /// writes the file itself, for instance.
        /// </summary>
        public void ReloadNow(string why)
        {
            ReloadCount++;

            try
            {
                // Keep the file we are watching consistent, or the next poll sees the write we are
                // about to make and reloads straight back over the top.
                _lastWriteUtc = Stamp(_path, out _lastLength);
            }
            catch (Exception ex)
            {
                UiLog.Warn("Could not re-stamp " + _path + ": " + ex.Message);
            }

            // BepInEx raises ConfigReloaded from inside Reload, and this class is subscribed to it in
            // order to catch reloads it did not initiate - the config editor's "Reload from disk", say.
            // Without this flag the handler would fire for our own reload too, and every listener would
            // be told twice per change.
            _reloading = true;
            try
            {
                _file.Reload();
            }
            catch (Exception ex)
            {
                // A file that does not parse is the user's typo, not a reason to take the mod down.
                _reloading = false;
                if (_log != null)
                {
                    _log.LogWarning(_modId + ": could not reload its config (" + why + "): " + ex.Message +
                                    ". The values in use are the ones from before; fix the file and it will " +
                                    "pick them up.");
                }
                return;
            }
            _reloading = false;

            if (_log != null)
            {
                _log.LogInfo("Reloaded config (" + why + ").");
            }

            Notify();
        }

        /// <summary>True while this watcher is inside its own Reload, so its handler stands down.</summary>
        private bool _reloading;

        private void OnConfigReloaded(object sender, EventArgs args)
        {
            // Our own reload already notifies at the end of ReloadNow.
            if (_reloading) return;

            // Something else reloaded the file - the config editor's button, most likely. The values are
            // already updated; pass the change on so a mod re-applies them.
            Notify();
        }

        private void Notify()
        {
            Action[] snapshot;
            lock (_listeners) { snapshot = _listeners.ToArray(); }
            foreach (var listener in snapshot)
            {
                try
                {
                    listener();
                }
                catch (Exception ex)
                {
                    _log.LogError(_modId + ": a config reload listener threw: " + ex);
                }
            }

            if (Reloaded != null)
            {
                try
                {
                    Reloaded();
                }
                catch (Exception ex)
                {
                    _log.LogError(_modId + ": a config reload handler threw: " + ex);
                }
            }
        }

        private static string SafePath(ConfigFile file)
        {
            try
            {
                var path = file.ConfigFilePath;
                return string.IsNullOrEmpty(path) ? null : path;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static DateTime Stamp(string path, out long length)
        {
            length = 0;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return DateTime.MinValue;
                length = info.Length;
                return info.LastWriteTimeUtc;
            }
            catch (Exception)
            {
                // The file can vanish between the exists check and the read; that is not an error
                // worth surfacing, the next poll will pick it up.
                return DateTime.MinValue;
            }
        }

        private static double UtcNow()
        {
            return (DateTime.UtcNow - Epoch).TotalSeconds;
        }

        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }
}