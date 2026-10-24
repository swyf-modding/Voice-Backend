// Minimal stand-ins for the three things ConfigWatcher.cs touches, so its polling and reload logic can
// be exercised outside the game. Not part of the shipped library.

using System;
using System.Collections.Generic;
using System.IO;

namespace BepInEx.Logging
{
    public class ManualLogSource
    {
        public string SourceName;
        public List<string> Lines = new List<string>();
        public void LogInfo(string m) { Lines.Add("INFO  " + m); }
        public void LogWarning(string m) { Lines.Add("WARN  " + m); }
        public void LogError(string m) { Lines.Add("ERROR " + m); }
    }

    public static class Logger
    {
        public static ManualLogSource CreateLogSource(string name)
        {
            return new ManualLogSource { SourceName = name };
        }
    }
}

namespace ScamWYF.Modding.Core.Ui
{
    internal static class UiLog
    {
        public static readonly List<string> Lines = new List<string>();
        public static void Info(string m) { Lines.Add("INFO  " + m); }
        public static void Warn(string m) { Lines.Add("WARN  " + m); }
        public static void Error(string m) { Lines.Add("ERROR " + m); }
    }
}