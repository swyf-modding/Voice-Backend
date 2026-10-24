using System.Threading;

namespace ScamWYF.VoiceBackend
{
    /// <summary>Cheap counters for the menu tab. Written from the main thread, read from the menu.</summary>
    internal static class Stats
    {
        private static int _asrRequests;
        private static int _asrFallbacks;
        private static int _asrFailures;
        private static int _ttsRequests;
        private static int _ttsFailures;
        private static string _lastAsrOutcome;
        private static string _lastAsrError;
        private static string _lastTtsOutcome;
        private static string _lastTtsError;

        public static int AsrRequests { get { return Volatile.Read(ref _asrRequests); } }
        public static int AsrFallbacks { get { return Volatile.Read(ref _asrFallbacks); } }
        public static int AsrFailures { get { return Volatile.Read(ref _asrFailures); } }
        public static int TtsRequests { get { return Volatile.Read(ref _ttsRequests); } }
        public static int TtsFailures { get { return Volatile.Read(ref _ttsFailures); } }
        public static string LastAsrOutcome { get { return _lastAsrOutcome; } }
        public static string LastAsrError { get { return _lastAsrError; } }
        public static string LastTtsOutcome { get { return _lastTtsOutcome; } }
        public static string LastTtsError { get { return _lastTtsError; } }

        public static void AsrRequest() { Interlocked.Increment(ref _asrRequests); }

        public static void AsrSuccess(string outcome)
        {
            _lastAsrOutcome = outcome;
            _lastAsrError = null;
        }

        public static void AsrFallback(string reason)
        {
            Interlocked.Increment(ref _asrFallbacks);
            _lastAsrOutcome = "Whisper (fallback)";
            _lastAsrError = reason;
        }

        public static void AsrFailure(string reason)
        {
            Interlocked.Increment(ref _asrFailures);
            _lastAsrError = reason;
        }

        public static void TtsRequest() { Interlocked.Increment(ref _ttsRequests); }

        public static void TtsSuccess(string outcome)
        {
            _lastTtsOutcome = outcome;
            _lastTtsError = null;
        }

        public static void TtsFailure(string reason)
        {
            Interlocked.Increment(ref _ttsFailures);
            _lastTtsError = reason;
        }
    }
}
