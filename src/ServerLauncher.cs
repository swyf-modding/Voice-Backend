using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;

namespace ScamWYF.VoiceBackend
{
    /// <summary>
    /// Starts the companion server and stops it again when the game exits.
    /// </summary>
    /// <remarks>
    /// The preferred layout is a self-contained folder next to the mod:
    ///
    ///   BepInEx/plugins/VoiceBackend/VoiceBackend.Server.exe
    ///   BepInEx/plugins/VoiceBackend/_internal/...
    ///
    /// The executable is produced by server/build-server.ps1 and needs no Python on the player's
    /// machine. A server that is already listening is left alone, so an external server and the
    /// auto-started one cannot fight over the port, and only a process this mod started is killed on
    /// exit.
    /// </remarks>
    internal static class ServerLauncher
    {
        private static Process _process;
        private static bool _startedByUs;
        private static bool _starting;
        private static StreamWriter _log;
        private static readonly object LogLock = new object();
        private static string _status = "not started";

        public static string Status
        {
            get { return _status; }
        }

        public static bool ManagedRunning
        {
            get { return _process != null && !_process.HasExited; }
        }

        public static IEnumerator EnsureStarted(Action<bool, string> done)
        {
            if (Plugin.Cfg == null)
            {
                done(false, "no config");
                yield break;
            }

            bool auto;
            try { auto = Plugin.Cfg.AutoStartServer.Value; }
            catch (Exception) { auto = false; }

            if (!auto)
            {
                _status = "auto-start off";
                done(true, _status);
                yield break;
            }

            string baseUrl;
            try { baseUrl = Plugin.Cfg.BaseUrl.Value.Trim(); }
            catch (Exception ex)
            {
                _status = "BaseUrl unreadable: " + ex.Message;
                done(false, _status);
                yield break;
            }

            if (!IsLoopback(baseUrl))
            {
                _status = "remote server; auto-start skipped";
                done(false, _status);
                yield break;
            }

            var probe = new Probe();
            yield return ProbeHealth(2, probe);
            if (probe.Ok)
            {
                _status = _startedByUs && ManagedRunning ? "running (auto-started)" : "running";
                done(true, _status);
                yield break;
            }

            if (_starting)
            {
                // Another coroutine is already launching it. Wait for it rather than starting a
                // second process on the same port.
                yield return WaitForHealth(ProbeTimeout(), done);
                yield break;
            }

            string executable = ResolveExecutable();
            if (executable == null)
            {
                _status = "server executable not found";
                done(false, _status);
                yield break;
            }

            _starting = true;
            try
            {
                try
                {
                    StartProcess(executable);
                }
                catch (Exception ex)
                {
                    _status = "could not start: " + ex.Message;
                    Plugin.Log.LogError("Could not start the voice backend server: " + ex);
                    done(false, _status);
                    yield break;
                }

                yield return WaitForHealth(ProbeTimeout(), done);
            }
            finally
            {
                _starting = false;
            }
        }

        private static IEnumerator WaitForHealth(int seconds, Action<bool, string> done)
        {
            var deadline = Time.realtimeSinceStartup + Mathf.Max(5, seconds);
            while (Time.realtimeSinceStartup < deadline)
            {
                if (_process != null && _process.HasExited)
                {
                    _status = "server exited (" + SafeExitCode() + ")";
                    Plugin.Log.LogError("The voice backend server exited during startup. See " +
                                        LogPath() + " for its output.");
                    done(false, _status);
                    yield break;
                }

                var probe = new Probe();
                yield return ProbeHealth(2, probe);
                if (probe.Ok)
                {
                    _status = "running (auto-started)";
                    done(true, _status);
                    yield break;
                }

                yield return new WaitForSecondsRealtime(0.75f);
            }

            _status = "server did not become ready";
            Plugin.Log.LogWarning("The voice backend server did not answer " + HealthUrl() +
                                  " within " + seconds + " seconds. See " + LogPath() + ".");
            done(false, _status);
        }

        public static void Stop(bool explicitStop = false)
        {
            bool shouldStop = explicitStop;
            if (!shouldStop)
            {
                try { shouldStop = Plugin.Cfg == null || Plugin.Cfg.StopServerOnExit.Value; }
                catch (Exception) { shouldStop = true; }
            }

            if (!shouldStop)
            {
                _status = "left running (StopServerOnExit is off)";
                CloseLog();
                _process = null;
                _startedByUs = false;
                return;
            }

            var process = _process;
            _process = null;
            _starting = false;

            if (process != null && _startedByUs)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit(3000);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("Could not stop the voice backend server: " + ex.Message);
                    KillTree(process);
                }
            }

            _startedByUs = false;
            _status = "stopped";
            CloseLog();
        }

        private static void CloseLog()
        {
            lock (LogLock)
            {
                try { if (_log != null) _log.Dispose(); }
                catch (Exception) { }
                _log = null;
            }
        }

        // ---------------------------------------------------------------- starting

        private static void StartProcess(string executable)
        {
            var directory = Path.GetDirectoryName(executable);
            var info = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var uri = new Uri(Plugin.Cfg.BaseUrl.Value.Trim());
            info.EnvironmentVariables["VOICE_HOST"] = "127.0.0.1";
            info.EnvironmentVariables["VOICE_PORT"] = uri.Port.ToString();
            info.EnvironmentVariables["PYTHONUTF8"] = "1";
            info.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            info.EnvironmentVariables["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1";
            info.EnvironmentVariables["TOKENIZERS_PARALLELISM"] = "false";
            info.EnvironmentVariables["TQDM_DISABLE"] = "1";

            OpenLog();

            var process = new Process { StartInfo = info };
            process.OutputDataReceived += delegate (object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null) WriteLog("out", e.Data);
            };
            process.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null) WriteLog("err", e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _process = process;
            _startedByUs = true;
            _status = "starting";

            Plugin.Log.LogInfo("Started the bundled voice backend: " + executable);
        }

        private static string ResolveExecutable()
        {
            string configured;
            try { configured = (Plugin.Cfg.ServerExecutable.Value ?? "").Trim(); }
            catch (Exception) { configured = ""; }

            if (configured.Length > 0)
            {
                var explicitPath = configured;
                if (!Path.IsPathRooted(explicitPath))
                {
                    var besidePlugins = Path.Combine(Paths.PluginPath, explicitPath);
                    if (File.Exists(besidePlugins)) return besidePlugins;
                    explicitPath = Path.Combine(Paths.GameRootPath, explicitPath);
                }
                if (File.Exists(explicitPath)) return explicitPath;
                Plugin.Log.LogWarning("ServerExecutable points at a file that does not exist: " + configured);
            }

            var names = new[]
            {
                "VoiceBackend.Server.exe",
                Path.Combine("server", "VoiceBackend.Server.exe"),
                Path.Combine("VoiceBackend", "VoiceBackend.Server.exe")
            };
            var roots = new[]
            {
                Path.Combine(Paths.PluginPath, "VoiceBackend"),
                Paths.PluginPath,
                Path.Combine(Paths.GameRootPath, "VoiceBackend"),
                Path.Combine(Paths.GameRootPath, "server"),
                Paths.GameRootPath
            };

            foreach (var root in roots)
            {
                foreach (var name in names)
                {
                    var candidate = Path.Combine(root, name);
                    if (File.Exists(candidate)) return candidate;
                }
            }

            return null;
        }

        // ---------------------------------------------------------------- health

        private sealed class Probe
        {
            public bool Ok;
            public string Text;
        }

        private static IEnumerator ProbeHealth(int timeoutSeconds, Probe probe)
        {
            HttpResult result = null;
            yield return VoiceHttp.Get(HealthUrl(), timeoutSeconds,
                delegate (HttpResult r) { result = r; });

            probe.Ok = result != null && result.Ok;
            probe.Text = result != null ? result.Describe() : "no response";
        }

        private static string HealthUrl()
        {
            return Plugin.Cfg.BaseUrl.Value.Trim().TrimEnd('/') + "/health";
        }

        private static int ProbeTimeout()
        {
            try { return Plugin.Cfg.ServerStartTimeoutSeconds.Value; }
            catch (Exception) { return 120; }
        }

        private static bool IsLoopback(string url)
        {
            try
            {
                var uri = new Uri(url);
                return uri.IsLoopback ||
                       string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- log and shutdown

        private static string LogPath()
        {
            return Path.Combine(Paths.BepInExRootPath, "VoiceBackend.Server.log");
        }

        private static void OpenLog()
        {
            lock (LogLock)
            {
                try
                {
                    _log = new StreamWriter(LogPath(), false, new UTF8Encoding(false));
                    _log.AutoFlush = true;
                    _log.WriteLine("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                                   "] voice backend server starting");
                }
                catch (Exception ex)
                {
                    _log = null;
                    Plugin.Log.LogWarning("Could not write " + LogPath() + ": " + ex.Message);
                }
            }
        }

        private static void WriteLog(string stream, string line)
        {
            lock (LogLock)
            {
                if (_log == null) return;
                try { _log.WriteLine("[" + stream + "] " + line); }
                catch (Exception) { }
            }
        }

        private static string SafeExitCode()
        {
            try { return _process != null && _process.HasExited ? _process.ExitCode.ToString() : "unknown"; }
            catch (Exception) { return "unknown"; }
        }

        private static void KillTree(Process process)
        {
            try
            {
                Process.Start(new ProcessStartInfo("taskkill", "/PID " + process.Id + " /T /F")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
            catch (Exception) { }
        }
    }
}
