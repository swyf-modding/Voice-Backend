using System;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ScamWYF.Modding.Core
{
    /// <summary>
    /// What game build this session is running, and a way to fail politely when the thing a mod
    /// hooks has moved.
    /// </summary>
    /// <remarks>
    /// A mod that patches a game method by name breaks the first time the game updates. That is
    /// expected, not exceptional. Resolving through here means the log says "expected
    /// KolkataApi.CompleteOpenRouterAsync(JObject, CancellationToken, Boolean), the game build is
    /// now X" instead of an AccessTools stack trace.
    /// </remarks>
    public static class GameBuild
    {
        private static string _unityVersion;
        private static string _gameVersion;

        /// <summary>Unity version, e.g. 6000.3.10f1.</summary>
        public static string UnityVersion
        {
            get
            {
                if (_unityVersion == null) _unityVersion = SafeRead(delegate { return Application.unityVersion; }, "unknown");
                return _unityVersion;
            }
        }

        /// <summary>The game's own version string, e.g. 1.4.2.</summary>
        public static string GameVersion
        {
            get
            {
                if (_gameVersion == null) _gameVersion = SafeRead(delegate { return Application.version; }, "unknown");
                return _gameVersion;
            }
        }

        public static string ProductName
        {
            get { return SafeRead(delegate { return Application.productName; }, "Scam With Your Friends"); }
        }

        /// <summary>One line naming the build, for a mod's startup log.</summary>
        public static string Describe()
        {
            return ProductName + " " + GameVersion + " (Unity " + UnityVersion + ")";
        }

        /// <summary>
        /// Warn when the running Unity version is not the one a mod was written against. Cheap
        /// insurance: a mismatch is the first thing to check when a mod misbehaves after an update.
        /// </summary>
        public static bool CheckUnityVersion(ManualLogSource log, string modId, string expected)
        {
            if (string.IsNullOrEmpty(expected)) return true;
            if (string.Equals(expected, UnityVersion, StringComparison.Ordinal)) return true;

            if (log != null)
            {
                log.LogWarning(modId + " was written against Unity " + expected +
                               " but this is Unity " + UnityVersion +
                               ". It may still work; if anything misbehaves, rebuild against this build.");
            }
            return false;
        }

        /// <summary>
        /// Find a method on a game type, reporting a not-found as a likely game update rather than
        /// letting it throw.
        /// </summary>
        public static bool TryResolveMethod(ScamMod owner, Type declaringType, string methodName,
            Type[] parameterTypes, out MethodBase method)
        {
            method = null;

            var expected = new StringBuilder(declaringType != null ? declaringType.FullName : "?");
            expected.Append(".").Append(methodName).Append("(");
            if (parameterTypes != null)
            {
                for (int i = 0; i < parameterTypes.Length; i++)
                {
                    if (i > 0) expected.Append(", ");
                    expected.Append(parameterTypes[i] != null ? parameterTypes[i].Name : "?");
                }
            }
            expected.Append(")");

            MethodBase found;
            try
            {
                found = AccessTools.Method(declaringType, methodName, parameterTypes);
            }
            catch (Exception ex)
            {
                owner.ModLog.LogError("Could not look up " + expected + ": " + ex.Message);
                return false;
            }

            if (found != null)
            {
                method = found;
                return true;
            }

            owner.ModLog.LogError(
                expected + " does not exist in this build (" + Describe() + "). " +
                "The game was most likely updated and this mod needs rebuilding against it. " +
                "Nothing was patched; the game keeps running as it shipped.");

            // Worth saying out loud: a renamed or re-signatured method is the usual cause, and
            // AccessTools finds methods by exact parameter types only.
            var candidates = FindSimilar(declaringType, methodName);
            if (candidates.Count > 0)
            {
                owner.ModLog.LogError("Closest matches in this build: " +
                                      string.Join(", ", candidates.ToArray()) + ".");
            }

            return false;
        }

        /// <summary>
        /// Find a type in the game's own assemblies by name, reporting a miss the same way a method
        /// miss is reported.
        /// </summary>
        public static bool TryResolveType(ScamMod owner, string typeName, out Type type)
        {
            type = null;
            try
            {
                type = AccessTools.TypeByName(typeName);
            }
            catch (Exception ex)
            {
                owner.ModLog.LogError("Could not look up the type " + typeName + ": " + ex.Message);
                return false;
            }

            if (type != null) return true;

            owner.ModLog.LogWarning(
                "This build has no " + typeName + " (" + Describe() + "). " +
                "The game was most likely updated and this mod needs rebuilding against it. " +
                "The affected feature is off; everything else keeps working.");

            return false;
        }

        /// <summary>
        /// Find a field on a game type, reporting a miss the same way. Used by mods that read the
        /// game's own UI state rather than patching it.
        /// </summary>
        public static bool TryResolveField(ScamMod owner, Type declaringType, string fieldName,
            out FieldInfo field)
        {
            field = null;
            try
            {
                field = AccessTools.Field(declaringType, fieldName);
            }
            catch (Exception ex)
            {
                owner.ModLog.LogError("Could not look up " + declaringType + "." + fieldName + ": " + ex.Message);
                return false;
            }

            if (field != null) return true;

            owner.ModLog.LogWarning(
                declaringType + " has no " + fieldName + " field in this build (" + Describe() + "). " +
                "The game was most likely updated and this mod needs rebuilding against it.");

            return false;
        }

        /// <summary>Methods with the right name but a different shape, to make the log actionable.</summary>
        private static System.Collections.Generic.List<string> FindSimilar(Type declaringType, string methodName)
        {
            var found = new System.Collections.Generic.List<string>();
            if (declaringType == null) return found;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (var candidate in declaringType.GetMethods(flags))
            {
                if (candidate.Name != methodName) continue;
                var parts = new System.Collections.Generic.List<string>();
                foreach (var parameter in candidate.GetParameters()) parts.Add(parameter.ParameterType.Name);
                found.Add(candidate.ReturnType.Name + " " + candidate.Name + "(" +
                          string.Join(", ", parts.ToArray()) + ")");
            }

            return found;
        }

        private static string SafeRead(Func<string> read, string fallback)
        {
            try
            {
                var value = read();
                return string.IsNullOrEmpty(value) ? fallback : value;
            }
            catch (Exception)
            {
                return fallback;
            }
        }
    }
}