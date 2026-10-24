using System;
using System.Collections.Generic;

namespace ScamWYF.VoiceBackend
{
    /// <summary>
    /// The language decisions in one place: which Parakeet model a language needs, and which
    /// Chatterbox language tag a game language code maps to.
    /// </summary>
    /// <remarks>
    /// Parakeet v2 is English only. Parakeet v3 was trained on 25 European languages and detects the
    /// language itself, so it needs no tag; the list below is exactly the model card's list. Anything
    /// outside it is a conscious choice the player makes in the config, not something this class
    /// quietly guesses at.
    ///
    /// Chatterbox Turbo is English only. Chatterbox Multilingual V3 covers 23 languages, and its
    /// tags accept a region-stripped code such as "pt" for pt-BR. The game also offers a handful of
    /// languages neither Chatterbox checkpoint speaks; those map to the closest neighbour rather
    /// than failing, and the server says which checkpoint it used in its health response.
    /// </remarks>
    internal static class LanguageMap
    {
        private static readonly HashSet<string> ParakeetV3Languages = new HashSet<string>(
            new[]
            {
                "bg", "hr", "cs", "da", "nl", "en", "et", "fi", "fr", "de", "el", "hu", "it",
                "lv", "lt", "mt", "pl", "pt", "ro", "sk", "sl", "es", "sv", "ru", "uk"
            },
            StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> ChatterboxLanguages = new HashSet<string>(
            new[]
            {
                "ar", "da", "de", "el", "en", "es", "fi", "fr", "he", "hi", "it", "ja", "ko",
                "ms", "nl", "no", "pl", "pt", "ru", "sv", "sw", "tr", "zh"
            },
            StringComparer.OrdinalIgnoreCase);

        /// <summary>Game codes Chatterbox has no model for, and the tag that reads closest.</summary>
        private static readonly Dictionary<string, string> Approximations =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "cs", "en" }, // Czech
                { "et", "en" }, // Estonian
                { "id", "en" }, // Indonesian
                { "hu", "en" }, // Hungarian
                { "fa", "ar" }  // Persian uses the Arabic script
            };

        public static string Normalize(string code)
        {
            if (string.IsNullOrEmpty(code)) return "en";
            var value = code.Trim().ToLowerInvariant();
            var separator = value.IndexOfAny(new[] { '-', '_' });
            if (separator > 0) value = value.Substring(0, separator);
            return value.Length == 0 ? "en" : value;
        }

        public static bool IsEnglish(string code)
        {
            return string.Equals(Normalize(code), "en", StringComparison.Ordinal);
        }

        public static bool SupportsParakeetV3(string code)
        {
            return ParakeetV3Languages.Contains(Normalize(code));
        }

        /// <summary>
        /// Which Parakeet checkpoint to use, or null to leave this request to the game's Whisper.
        /// </summary>
        public static ParakeetModel? ModelFor(string languageCode, Config config)
        {
            var code = Normalize(languageCode);

            if (code == "en") return config.EnglishModel.Value;

            if (SupportsParakeetV3(code)) return config.EuropeanModel.Value;

            return config.OtherLanguages.Value == OtherLanguageAsr.ParakeetV3
                ? ParakeetModel.V3
                : (ParakeetModel?)null;
        }

        public static string CodeFor(ParakeetModel model)
        {
            return model == ParakeetModel.V2 ? "v2" : "v3";
        }

        /// <summary>The language tag Chatterbox should be given, never null.</summary>
        public static string ChatterboxLanguage(string code)
        {
            var normalized = Normalize(code);
            if (ChatterboxLanguages.Contains(normalized)) return normalized;

            string approximated;
            if (Approximations.TryGetValue(normalized, out approximated)) return approximated;

            return "en";
        }

        public static bool ChatterboxSupports(string code)
        {
            return ChatterboxLanguages.Contains(Normalize(code));
        }
    }
}
