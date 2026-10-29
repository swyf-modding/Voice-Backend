using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace ScamWYF.VoiceBackend
{
    /// <summary>
    /// A DontDestroyOnLoad host for the coroutines the HTTP calls need. UnityWebRequest has to run on
    /// the main thread, and both patches are reached from the main thread, so one host serves both.
    /// </summary>
    internal sealed class VoiceRuntime : MonoBehaviour
    {
        private static VoiceRuntime _instance;

        public static VoiceRuntime Instance
        {
            get
            {
                Ensure();
                return _instance;
            }
        }

        public static void Ensure()
        {
            if (_instance != null) return;

            var host = new GameObject("ScamWYF.VoiceBackend.Runtime");
            DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            _instance = host.AddComponent<VoiceRuntime>();
        }

        public static void Shutdown()
        {
            if (_instance == null) return;
            try { Destroy(_instance.gameObject); }
            catch (Exception) { }
            _instance = null;
        }
    }

    internal sealed class HttpResult
    {
        public bool Ok;
        public int Status;
        public byte[] Body;
        public string Text;
        public string Error;

        public string Describe()
        {
            if (Ok) return "HTTP " + Status;
            if (!string.IsNullOrEmpty(Error)) return Error;
            return "HTTP " + Status;
        }
    }

    internal static class VoiceHttp
    {
        private const int MaxText = 1200;

        public static IEnumerator Post(string url, byte[] payload, string contentType, int timeoutSeconds,
            Action<HttpResult> done)
        {
            yield return Send(url, payload, contentType, null, timeoutSeconds, false, done);
        }

        public static IEnumerator Post(string url, byte[] payload, string contentType,
            IDictionary<string, string> headers, int timeoutSeconds, Action<HttpResult> done)
        {
            yield return Send(url, payload, contentType, headers, timeoutSeconds, false, done);
        }

        public static IEnumerator Get(string url, int timeoutSeconds, Action<HttpResult> done)
        {
            yield return Send(url, null, null, null, timeoutSeconds, true, done);
        }

        public static IEnumerator Get(string url, IDictionary<string, string> headers, int timeoutSeconds,
            Action<HttpResult> done)
        {
            yield return Send(url, null, null, headers, timeoutSeconds, true, done);
        }

        private static IEnumerator Send(string url, byte[] payload, string contentType,
            IDictionary<string, string> headers, int timeoutSeconds, bool get, Action<HttpResult> done)
        {
            UnityWebRequest request = null;
            var result = new HttpResult();

            try
            {
                request = new UnityWebRequest(url, get ? UnityWebRequest.kHttpVerbGET : UnityWebRequest.kHttpVerbPOST);
                request.downloadHandler = new DownloadHandlerBuffer();
                if (!get)
                {
                    request.uploadHandler = new UploadHandlerRaw(payload ?? new byte[0]);
                    request.SetRequestHeader("Content-Type", contentType ?? "application/octet-stream");
                }
                request.SetRequestHeader("Accept", "application/json, audio/wav, audio/pcm, */*");
                request.timeout = Math.Max(1, timeoutSeconds);

                // A caller that names its own headers owns its authorization entirely; only the mod's
                // own server falls back to the single shared key.
                if (headers != null && headers.Count > 0)
                {
                    foreach (KeyValuePair<string, string> header in headers)
                    {
                        if (!string.IsNullOrEmpty(header.Value)) request.SetRequestHeader(header.Key, header.Value);
                    }
                }
                else
                {
                    var key = Plugin.Cfg != null ? Plugin.Cfg.ApiKey.Value.Trim() : "";
                    if (key.Length > 0) request.SetRequestHeader("Authorization", "Bearer " + key);
                }

                if (Plugin.Cfg != null && Plugin.Cfg.Debug.Value)
                    Plugin.Log.LogInfo("-> " + (get ? "GET " : "POST ") + Redact(url) + " (" +
                                       (payload != null ? payload.Length : 0) + " bytes)");
            }
            catch (Exception ex)
            {
                result.Error = "Could not build the request: " + ex.Message;
                if (request != null) request.Dispose();
                if (done != null) done(result);
                yield break;
            }

            var operation = request.SendWebRequest();
            while (!operation.isDone) yield return null;

            try
            {
                result.Status = (int)request.responseCode;
                result.Body = request.downloadHandler != null ? request.downloadHandler.data : null;
                result.Text = request.downloadHandler != null ? request.downloadHandler.text : null;
                result.Ok = request.result == UnityWebRequest.Result.Success;

                if (!result.Ok)
                {
                    var body = result.Text;
                    if (!string.IsNullOrEmpty(body) && body.Length > MaxText)
                        body = body.Substring(0, MaxText) + "...";
                    result.Error = string.Format("{0} returned HTTP {1}: {2}{3}",
                        url, result.Status, request.error,
                        string.IsNullOrEmpty(body) ? "" : " - " + body);
                }

                if (Plugin.Cfg != null && Plugin.Cfg.Debug.Value)
                {
                    Plugin.Log.LogInfo("<- " + (result.Ok ? "OK" : "FAIL") + " HTTP " + result.Status +
                                       " (" + (result.Body != null ? result.Body.Length : 0) + " bytes)");
                }
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Error = "Reading the response failed: " + ex.Message;
            }
            finally
            {
                request.Dispose();
            }

            if (done != null) done(result);
        }

        /// <summary>
        /// Strips anything that looks like a credential out of a URL before logging it. Some services
        /// accept the key as a query parameter, and a debug log that leaks one is worse than no log.
        /// </summary>
        internal static string Redact(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            int query = url.IndexOf('?');
            if (query < 0) return url;

            string[] pairs = url.Substring(query + 1).Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                int equals = pairs[i].IndexOf('=');
                if (equals <= 0) continue;
                string name = pairs[i].Substring(0, equals);
                if (name.IndexOf("key", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    pairs[i] = name + "=...";
                }
            }
            return url.Substring(0, query + 1) + string.Join("&", pairs);
        }
    }
}
