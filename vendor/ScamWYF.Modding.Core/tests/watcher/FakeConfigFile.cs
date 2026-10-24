// A stand-in for BepInEx's ConfigFile with just the surface ConfigWatcher uses: a path, a reload event
// and a Reload that re-reads the file. Real BepInEx cannot be loaded here (it is compiled against the
// game's own mscorlib), and the behaviour under test is ConfigWatcher's, not BepInEx's.

using System;
using System.Collections.Generic;
using System.IO;

namespace BepInEx.Configuration
{
    public class ConfigFile
    {
        private readonly string _path;

        public ConfigFile(string path)
        {
            _path = path;
        }

        public string ConfigFilePath { get { return _path; } }

        /// <summary>Set to make Reload throw, standing in for a file that does not parse.</summary>
        public bool FailNextReload;

        public int ReloadCount;

        /// <summary>What the last Reload saw, so the test can prove the new value arrived.</summary>
        public string LastSeenValue;

        public event EventHandler ConfigReloaded;

        public void Reload()
        {
            if (FailNextReload)
            {
                FailNextReload = false;
                throw new Exception("simulated parse failure");
            }

            ReloadCount++;

            foreach (var line in File.ReadAllLines(_path))
            {
                var equals = line.IndexOf('=');
                if (equals < 0) continue;
                if (line.Substring(0, equals).Trim() != "Timeout") continue;
                LastSeenValue = line.Substring(equals + 1).Trim();
                break;
            }

            var handler = ConfigReloaded;
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }
}