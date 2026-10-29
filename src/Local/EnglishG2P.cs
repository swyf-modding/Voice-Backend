using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ScamWYF.VoiceBackend.Local
{
    /// <summary>
    /// Text to Kokoro-82M phonemes for English, with no dependencies outside mscorlib.
    /// </summary>
    /// <remarks>
    /// Kokoro consumes IPA, and the reference front end (misaki) builds that IPA from spaCy part-of-speech
    /// tagging plus a BART sequence-to-sequence model for words outside its lexicon. Neither can ship
    /// inside a Mono plugin: spaCy is Python, and the BART fallback is another few hundred megabytes of
    /// transformer. This class keeps the parts that travel well and replaces the rest:
    ///
    /// <list type="bullet">
    /// <item>the IPA lexicon (<c>assets/g2p/en-lexicon.bin</c>, built by <c>tools/build-lexicon.py</c>),</item>
    /// <item>misaki's inflection morphology, which derives <c>-s</c>/<c>-ed</c>/<c>-ing</c> from the stem's
    /// final phoneme instead of storing every inflected form,</item>
    /// <item>a letter-to-sound rule table for words in neither, in place of the neural fallback.</item>
    /// </list>
    ///
    /// What is deliberately absent is the tagger. misaki uses part of speech to pick between readings of
    /// <c>read</c>, <c>use</c>, <c>live</c>, to reduce <c>the</c> to <c>ðə</c>, and to turn <c>a</c> into a
    /// schwa. A lexicon entry therefore carries its <c>DEFAULT</c> reading here, and function words are
    /// read as written. The cost is a small set of context-dependent pronunciations; the gain is a
    /// deterministic, allocation-light pipeline that runs on the game's own runtime.
    ///
    /// The pipeline is: normalise, split into words and modelled punctuation, expand numbers and
    /// abbreviations to words, then per word look the lexicon up, try the morphology, and finally spell it
    /// with the rule table. Everything that reaches the output is a character from
    /// <see cref="KokoroVocab"/>; anything else is dropped rather than emitted as an unknown symbol.
    /// </remarks>
    internal static class EnglishG2P
    {
        // ---------------------------------------------------------------- public surface

        /// <summary>True once a lexicon has been handed to <see cref="Load"/>.</summary>
        internal static bool IsLoaded { get { return _lexicon != null; } }

        /// <summary>
        /// Adopts a lexicon blob produced by <c>tools/build-lexicon.py</c>. Idempotent: a second call with
        /// the same or another blob is ignored, so a reloaded plugin does not pay for a second copy.
        /// </summary>
        /// <remarks>
        /// The blob is kept as-is instead of being unpacked into 98,862 strings and a dictionary: lookups
        /// binary-search the UTF-8 word bytes in place, so the only cost of loading is the offset table
        /// (395 KB of <c>int</c>) and one array reference to the caller's bytes.
        /// </remarks>
        /// <param name="lexicon">The whole file, magic header included. Not copied.</param>
        /// <exception cref="InvalidDataException">The header or an entry length is inconsistent.</exception>
        internal static void Load(byte[] lexicon)
        {
            if (_lexicon != null || lexicon == null)
            {
                return;
            }
            if (lexicon.Length < 8 || lexicon[0] != (byte)'K' || lexicon[1] != (byte)'G' ||
                lexicon[2] != (byte)'L' || lexicon[3] != (byte)'1')
            {
                throw new InvalidDataException("en-lexicon.bin: expected the KGL1 header");
            }
            int count = lexicon[4] | (lexicon[5] << 8) | (lexicon[6] << 16) | (lexicon[7] << 24);
            if (count < 0 || count > lexicon.Length / 4)
            {
                // Four bytes per entry is the floor (three length prefixes and a one-letter word), so a
                // larger count cannot be honest, and allocating for it would be the corruption's problem.
                throw new InvalidDataException("en-lexicon.bin: implausible entry count " + count.ToString(CultureInfo.InvariantCulture));
            }
            var offsets = new int[count];
            int at = 8;
            for (int i = 0; i < count; i++)
            {
                offsets[i] = at;
                int usLength = lexicon[at];
                at += 2 + usLength;
                if (at > lexicon.Length)
                {
                    throw new InvalidDataException("en-lexicon.bin: truncated entry " + i.ToString(CultureInfo.InvariantCulture));
                }
                int gbLength = lexicon[at - 1];
                at += gbLength;
                if (at > lexicon.Length)
                {
                    throw new InvalidDataException("en-lexicon.bin: truncated entry " + i.ToString(CultureInfo.InvariantCulture));
                }
                int wordLength = lexicon[at];
                at += 1 + wordLength;
                if (at > lexicon.Length)
                {
                    throw new InvalidDataException("en-lexicon.bin: truncated entry " + i.ToString(CultureInfo.InvariantCulture));
                }
            }
            if (at != lexicon.Length)
            {
                throw new InvalidDataException("en-lexicon.bin: " + (lexicon.Length - at).ToString(CultureInfo.InvariantCulture) + " trailing bytes");
            }
            _offsets = offsets;
            _lexicon = lexicon;
        }

        /// <summary>
        /// The Kokoro IPA for English text, tokens separated by single spaces and punctuation attached the
        /// way the source text had it. Never throws and never returns null.
        /// </summary>
        /// <remarks>
        /// Characters that carry no pronunciation and no Kokoro symbol (symbols such as <c>@</c> or
        /// <c>#</c>, and scripts with no Latin letters in them) are dropped, which is also what
        /// <see cref="KokoroVocab.Tokenize"/> would otherwise report as an unknown symbol.
        /// </remarks>
        internal static string Phonemize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            var tokens = new List<Token>(text.Length / 4 + 4);
            Split(Normalize(text), tokens);
            if (tokens.Count == 0)
            {
                return string.Empty;
            }

            var words = new List<string>(4);
            var spelled = new List<string>(4);
            var output = new StringBuilder(text.Length * 2);
            bool empty = true;
            for (int i = 0; i < tokens.Count; i++)
            {
                Token token = tokens[i];
                if (!token.IsWord)
                {
                    Append(output, token.Text, !empty && token.Spaced, ref empty);
                    continue;
                }
                words.Clear();
                if (IsNumberLabel(token.Text) && NextIsNumeric(tokens, i + 1))
                {
                    words.Add("number");
                }
                else
                {
                    Expand(token.Text, words);
                }
                for (int w = 0; w < words.Count; w++)
                {
                    spelled.Clear();
                    PhonemizeWord(words[w], spelled);
                    for (int s = 0; s < spelled.Count; s++)
                    {
                        Append(output, spelled[s], !empty && (token.Spaced || w > 0 || s > 0), ref empty);
                    }
                }
            }
            // misaki's v1 inventory, which Kokoro-82M was trained on, writes the US flap as 'T' and folds
            // the glottal stop into 't'. The lexicon stores 'ɾ'/'ʔ', so the substitution belongs here, in
            // one place, exactly where the reference front end does it.
            output.Replace("ɾ", "T").Replace("ʔ", "t");
            return output.ToString();
        }

        // ---------------------------------------------------------------- lexicon

        private static byte[] _lexicon;
        private static int[] _offsets;
        private static byte[] _encode = new byte[256];

        /// <summary>
        /// The IPA for a lowercase word, US first and GB second, or null.
        /// </summary>
        /// <remarks>
        /// The file is sorted by the UTF-8 bytes of the word, and UTF-8 byte order equals code point
        /// order, so comparing encoded bytes agrees with comparing the words themselves.
        /// </remarks>
        private static string Lookup(string word)
        {
            if (_lexicon == null)
            {
                return null;
            }
            int length = Encoding.UTF8.GetByteCount(word);
            if (_encode.Length < length)
            {
                _encode = new byte[length * 2];
            }
            Encoding.UTF8.GetBytes(word, 0, word.Length, _encode, 0);

            int low = 0;
            int high = _offsets.Length - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                int entry = _offsets[middle];
                int usLength = _lexicon[entry];
                int gbLength = _lexicon[entry + 1 + usLength];
                int wordAt = entry + 3 + usLength + gbLength;
                int wordLength = _lexicon[wordAt - 1];
                int order = Compare(_encode, length, wordAt, wordLength);
                if (order == 0)
                {
                    if (usLength > 0)
                    {
                        return Encoding.UTF8.GetString(_lexicon, entry + 1, usLength);
                    }
                    if (gbLength > 0)
                    {
                        return Encoding.UTF8.GetString(_lexicon, entry + 2 + usLength, gbLength);
                    }
                    return null;
                }
                if (order < 0)
                {
                    high = middle - 1;
                }
                else
                {
                    low = middle + 1;
                }
            }
            return null;
        }

        private static int Compare(byte[] query, int queryLength, int at, int length)
        {
            int shared = queryLength < length ? queryLength : length;
            for (int i = 0; i < shared; i++)
            {
                int difference = query[i] - _lexicon[at + i];
                if (difference != 0)
                {
                    return difference;
                }
            }
            return queryLength - length;
        }

        /// <summary>Lowercases unless the word already is, so the common case allocates nothing.</summary>
        private static string Lower(string word)
        {
            for (int i = 0; i < word.Length; i++)
            {
                char c = word[i];
                if (c >= 'A' && c <= 'Z')
                {
                    return word.ToLowerInvariant();
                }
                if (c > 127 && char.IsUpper(c))
                {
                    return word.ToLowerInvariant();
                }
            }
            return word;
        }

        // ---------------------------------------------------------------- morphology

        // misaki's US_TAUS: the phonemes that make a following /t/ flap. Consonant sets below are
        // misaki's, unchanged, because the derived forms have to land on the same strings the lexicon
        // would have stored.
        private const string VoicelessStops = "ptkfθ";
        private const string Sibilants = "szʃʒʧʤ";
        private const string VoicelessEd = "pkfθʃsʧ";
        private const string Taus = "AIOWYiuæɑəɛɪɹʊʌ";

        /// <summary>Plural and third-person <c>-s</c>, from the stem's final phoneme.</summary>
        private static string AddS(string stem)
        {
            char last = stem[stem.Length - 1];
            if (VoicelessStops.IndexOf(last) >= 0)
            {
                return stem + "s";
            }
            if (Sibilants.IndexOf(last) >= 0)
            {
                return stem + "ᵻz";
            }
            return stem + "z";
        }

        /// <summary>Past tense <c>-ed</c>; <c>t</c> and <c>d</c> stems flap in US English.</summary>
        private static string AddEd(string stem)
        {
            char last = stem[stem.Length - 1];
            if (VoicelessEd.IndexOf(last) >= 0)
            {
                return stem + "t";
            }
            if (last == 'd')
            {
                return stem + "ᵻd";
            }
            if (last != 't')
            {
                return stem + "d";
            }
            if (stem.Length > 1 && Taus.IndexOf(stem[stem.Length - 2]) >= 0)
            {
                return stem.Substring(0, stem.Length - 1) + "ɾᵻd";
            }
            return stem + "ɪd";
        }

        /// <summary>Present participle <c>-ing</c>.</summary>
        private static string AddIng(string stem)
        {
            if (stem.Length > 1 && stem[stem.Length - 1] == 't' && Taus.IndexOf(stem[stem.Length - 2]) >= 0)
            {
                return stem.Substring(0, stem.Length - 1) + "ɾɪŋ";
            }
            return stem + "ɪŋ";
        }

        /// <summary>
        /// Resolves a word to IPA: exact entry, then possessive or inflection on a known stem, then the
        /// rule table.
        /// </summary>
        private static void PhonemizeWord(string raw, List<string> output)
        {
            if (raw.Length == 0)
            {
                return;
            }
            string word = FoldWord(Lower(raw));
            string ipa = Lookup(word);
            if (ipa != null)
            {
                output.Add(ipa);
                return;
            }
            if (word.IndexOf('-') > 0)
            {
                // A compound the lexicon does not list: pronounce the parts, which is what the reference
                // front end ends up doing for anything hyphenated it cannot look up whole.
                int part = 0;
                for (int i = 0; i <= word.Length; i++)
                {
                    if (i == word.Length || word[i] == '-')
                    {
                        if (i > part)
                        {
                            PhonemizeWord(word.Substring(part, i - part), output);
                        }
                        part = i + 1;
                    }
                }
                return;
            }

            // Possessives first, exactly as misaki's get_word orders them: an explicit 's is a stored
            // or derived stem plus /z/, not a word in its own right.
            if (word.Length > 2 && word.EndsWith("'s", StringComparison.Ordinal))
            {
                string stem = Lookup(word.Substring(0, word.Length - 2));
                if (stem != null)
                {
                    output.Add(AddS(stem));
                    return;
                }
            }
            if (word.Length > 2 && word.EndsWith("s'", StringComparison.Ordinal))
            {
                string stem = Lookup(word.Substring(0, word.Length - 2));
                if (stem != null)
                {
                    output.Add(AddS(stem));
                    return;
                }
            }
            if (word.Length > 1 && word[word.Length - 1] == '\'')
            {
                string stem = Lookup(word.Substring(0, word.Length - 1));
                if (stem != null)
                {
                    output.Add(stem);
                    return;
                }
            }

            if (StemS(word, output) || StemEd(word, output) || StemIng(word, output))
            {
                return;
            }
            AddLetterToSound(word, output);
        }

        private static bool StemS(string word, List<string> output)
        {
            if (word.Length < 3 || word[word.Length - 1] != 's')
            {
                return false;
            }
            string stem = null;
            if (!word.EndsWith("ss", StringComparison.Ordinal) && word.Length > 1)
            {
                stem = Lookup(word.Substring(0, word.Length - 1));
            }
            if (stem == null &&
                (word.EndsWith("'s", StringComparison.Ordinal) ||
                 (word.Length > 4 && word.EndsWith("es", StringComparison.Ordinal) &&
                  !word.EndsWith("ies", StringComparison.Ordinal))))
            {
                stem = Lookup(word.Substring(0, word.Length - 2));
            }
            if (stem == null && word.Length > 4 && word.EndsWith("ies", StringComparison.Ordinal))
            {
                stem = Lookup(word.Substring(0, word.Length - 3) + "y");
            }
            if (stem == null)
            {
                return false;
            }
            output.Add(AddS(stem));
            return true;
        }

        private static bool StemEd(string word, List<string> output)
        {
            if (word.Length < 4 || word[word.Length - 1] != 'd')
            {
                return false;
            }
            string stem = null;
            if (!word.EndsWith("dd", StringComparison.Ordinal))
            {
                stem = Lookup(word.Substring(0, word.Length - 1));
            }
            if (stem == null && word.Length > 4 && word.EndsWith("ed", StringComparison.Ordinal) &&
                !word.EndsWith("eed", StringComparison.Ordinal))
            {
                stem = Lookup(word.Substring(0, word.Length - 2));
            }
            if (stem == null)
            {
                return false;
            }
            output.Add(AddEd(stem));
            return true;
        }

        private static bool StemIng(string word, List<string> output)
        {
            if (word.Length < 5 || !word.EndsWith("ing", StringComparison.Ordinal))
            {
                return false;
            }
            string stem = Lookup(word.Substring(0, word.Length - 3));
            if (stem == null)
            {
                // "making" is "make": the stem keeps the silent e the spelling dropped.
                stem = Lookup(word.Substring(0, word.Length - 3) + "e");
            }
            if (stem == null && word.Length > 5)
            {
                // "running", "stopping", "picking": the doubling belongs to the spelling, not the stem.
                string trimmed = word.Substring(0, word.Length - 4);
                if (IsDoubledStem(word, trimmed.Length))
                {
                    stem = Lookup(trimmed);
                }
            }
            if (stem == null)
            {
                return false;
            }
            output.Add(AddIng(stem));
            return true;
        }

        private static bool IsDoubledStem(string word, int stemLength)
        {
            if (word.Length > 5 && word.EndsWith("cking", StringComparison.Ordinal))
            {
                return true;
            }
            char last = word[stemLength];
            if ("bcdgklmnprstvxz".IndexOf(last) < 0)
            {
                return false;
            }
            return word[stemLength - 1] == last;
        }

        // ---------------------------------------------------------------- numbers

        private static readonly string[] Ones =
        {
            "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
            "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen",
            "nineteen"
        };

        private static readonly string[] Tens =
        {
            "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"
        };

        private static readonly long[] Scales = { 1000000000000L, 1000000000L, 1000000L, 1000L };
        private static readonly string[] ScaleNames = { "trillion", "billion", "million", "thousand" };

        /// <summary>
        /// Splits a token into words to phonemise, expanding numbers, currency, ordinals, times and the
        /// abbreviation table. Appends at least one word unless the token carries no pronunciation.
        /// </summary>
        private static void Expand(string token, List<string> words)
        {
            if (TryExpandNumber(token, words))
            {
                return;
            }
            string lower = Lower(token);
            string replacement = Abbreviation(lower, token);
            // A lexicon headword beats the abbreviation table ("etc", "mr"), because that is the reading the
            // reference front end uses too. Two exceptions: an upper-case spelling is the abbreviation by
            // definition, and "vs" is stored as the letter names rather than as the word it stands for.
            bool lexiconWins = replacement != null && !IsUppercase(token) && lower != "vs" &&
                Lookup(FoldWord(lower)) != null;
            if (replacement == null || lexiconWins)
            {
                words.Add(token);
                return;
            }
            int start = 0;
            for (int i = 0; i <= replacement.Length; i++)
            {
                if (i == replacement.Length || replacement[i] == ' ')
                {
                    if (i > start)
                    {
                        words.Add(replacement.Substring(start, i - start));
                    }
                    start = i + 1;
                }
            }
        }

        private static string Abbreviation(string lower, string original)
        {
            string replacement;
            bool uppercase = IsUppercase(original);
            switch (lower)
            {
                case "mr":
                    replacement = "mister";
                    break;
                case "mrs":
                    replacement = "missus";
                    break;
                case "ms":
                    replacement = "miz";
                    break;
                case "dr":
                    replacement = "doctor";
                    break;
                case "vs":
                    replacement = "versus";
                    break;
                case "st":
                    replacement = "saint";
                    break;
                case "etc":
                    replacement = "et cetera";
                    break;
                case "eg":
                case "e.g":
                    replacement = "for example";
                    break;
                case "ie":
                case "i.e":
                    replacement = "that is";
                    break;
                case "ok":
                    replacement = "okay";
                    break;
                case "tv":
                    replacement = "tee vee";
                    break;
                case "ai":
                    replacement = "ay eye";
                    break;
                case "uk":
                    replacement = "you kay";
                    break;
                case "us":
                    replacement = "you ess";
                    break;
                default:
                    return null;
            }
            if (!uppercase && (lower == "ok" || lower == "us"))
            {
                // "ok" and "us" are words in lower-case prose far more often than they are abbreviations,
                // so only their upper-case spellings are expanded. "no" is left to the number expander,
                // because it is the word "no" everywhere except in "no. 5".
                return null;
            }
            return replacement;
        }

        /// <summary>"no" is only the abbreviation for "number" when a number follows it.</summary>
        private static bool IsNumberLabel(string token)
        {
            string lower = Lower(token);
            return lower == "no" || lower == "no.";
        }

        private static bool NextIsNumeric(List<Token> tokens, int at)
        {
            for (int i = at; i < tokens.Count; i++)
            {
                if (!tokens[i].IsWord)
                {
                    if (tokens[i].Text == "." && i == at)
                    {
                        continue;
                    }
                    return false;
                }
                string text = tokens[i].Text;
                for (int c = 0; c < text.Length; c++)
                {
                    if (char.IsDigit(text[c]))
                    {
                        return true;
                    }
                }
                return false;
            }
            return false;
        }

        private static bool IsUppercase(string token)
        {
            bool letter = false;
            for (int i = 0; i < token.Length; i++)
            {
                char c = token[i];
                if (char.IsLetter(c))
                {
                    if (!char.IsUpper(c))
                    {
                        return false;
                    }
                    letter = true;
                }
            }
            return letter;
        }

        /// <summary>
        /// Expands a numeric token to words, returning false when the token is not a number after all.
        /// </summary>
        private static bool TryExpandNumber(string token, List<string> words)
        {
            int start = 0;
            string currency = null;
            char subdivision = '\0';
            if (token[0] == '$' || token[0] == '£' || token[0] == '€')
            {
                currency = token[0] == '$' ? "dollar" : (token[0] == '£' ? "pound" : "euro");
                subdivision = token[0] == '£' ? (char)0 : 'c';
                start = 1;
            }
            int end = token.Length;
            bool percent = end > start && token[end - 1] == '%';
            if (percent)
            {
                end--;
            }
            bool ordinal = false;
            if (end - start > 2)
            {
                string tail = token.Substring(end - 2, 2).ToLowerInvariant();
                if (tail == "st" || tail == "nd" || tail == "rd" || tail == "th")
                {
                    bool digitsBefore = end - start > 2;
                    for (int i = start; i < end - 2 && digitsBefore; i++)
                    {
                        digitsBefore = char.IsDigit(token[i]) || token[i] == ',';
                    }
                    if (digitsBefore)
                    {
                        ordinal = true;
                        end -= 2;
                    }
                }
            }
            bool negative = false;
            if (start < end && (token[start] == '-' || token[start] == '+'))
            {
                negative = token[start] == '-';
                start++;
            }
            if (end <= start)
            {
                return false;
            }

            bool colon = false;
            bool dot = false;
            for (int i = start; i < end; i++)
            {
                char c = token[i];
                if (c == ',')
                {
                    continue;
                }
                if (c == ':')
                {
                    colon = true;
                    continue;
                }
                if (c == '.')
                {
                    dot = true;
                    continue;
                }
                if (!char.IsDigit(c))
                {
                    return false;
                }
            }

            string body = token.Substring(start, end - start);
            string upper = body.Replace(",", string.Empty);
            if (colon)
            {
                if (currency != null || percent || ordinal)
                {
                    return false;
                }
                if (TryTime(upper, negative, words))
                {
                    return true;
                }
                // Not a clock time ("2:30:15"): read the segments as numbers instead of losing the token.
                if (upper[upper.Length - 1] == ':' || upper[0] == ':' ||
                    upper.IndexOf("::", StringComparison.Ordinal) >= 0)
                {
                    return false;
                }
                if (negative)
                {
                    words.Add("minus");
                }
                int segment = 0;
                for (int i = 0; i <= upper.Length; i++)
                {
                    if (i != upper.Length && upper[i] != ':')
                    {
                        continue;
                    }
                    long part;
                    if (i <= segment || !long.TryParse(upper.Substring(segment, i - segment), NumberStyles.None,
                            CultureInfo.InvariantCulture, out part))
                    {
                        return false;
                    }
                    Cardinal(part, words);
                    segment = i + 1;
                }
                return true;
            }

            if (currency != null)
            {
                return TryMoney(upper, currency, subdivision, negative, words);
            }

            if (dot)
            {
                return TryDecimal(upper, negative, ordinal, words);
            }

            long value;
            if (!long.TryParse(upper, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            {
                return false;
            }
            if (negative)
            {
                words.Add("minus");
            }
            if (ordinal)
            {
                var parts = new List<string>(4);
                Cardinal(value, parts);
                Ordinalize(parts);
                Append(parts, words);
            }
            else if (percent)
            {
                Cardinal(value, words);
                words.Add("percent");
            }
            else if (upper.Length > 1 && upper[0] == '0')
            {
                // "007" is a code, not the number seven: read the digits out.
                for (int i = 0; i < upper.Length; i++)
                {
                    words.Add(Ones[upper[i] - '0']);
                }
            }
            else if (upper.Length == 4 && value >= 1100 && value <= 2099)
            {
                AddYear((int)value, words);
            }
            else
            {
                Cardinal(value, words);
            }
            return true;
        }

        private static bool TryTime(string body, bool negative, List<string> words)
        {
            int split = body.IndexOf(':');
            if (split <= 0 || split != body.LastIndexOf(':'))
            {
                return false;
            }
            int hour;
            int minute;
            if (!int.TryParse(body.Substring(0, split), NumberStyles.None, CultureInfo.InvariantCulture, out hour) ||
                !int.TryParse(body.Substring(split + 1), NumberStyles.None, CultureInfo.InvariantCulture, out minute))
            {
                return false;
            }
            if (negative || hour > 24 || minute > 59 || body.Length - split - 1 < 1 ||
                body.Length - split - 1 > 2)
            {
                return false;
            }
            Cardinal(hour % 24, words);
            if (minute == 0)
            {
                words.Add("o'clock");
            }
            else if (minute < 10)
            {
                words.Add("oh");
                Cardinal(minute, words);
            }
            else
            {
                Cardinal(minute, words);
            }
            return true;
        }

        private static bool TryMoney(string body, string unit, char subdivision, bool negative,
            List<string> words)
        {
            int split = body.IndexOf('.');
            string whole = split < 0 ? body : body.Substring(0, split);
            string cents = split < 0 ? null : body.Substring(split + 1);
            long major;
            if (!long.TryParse(whole.Length == 0 ? "0" : whole, NumberStyles.None,
                    CultureInfo.InvariantCulture, out major))
            {
                return false;
            }
            long minor = 0;
            if (cents != null && cents.Length > 0)
            {
                if (cents.Length > 2 ||
                    !long.TryParse(cents, NumberStyles.None, CultureInfo.InvariantCulture, out minor))
                {
                    return false;
                }
                if (cents.Length == 1)
                {
                    minor *= 10;
                }
            }
            if (negative)
            {
                words.Add("minus");
            }
            if (major != 0 || minor == 0)
            {
                Cardinal(major, words);
                words.Add(major == 1 ? unit : unit + "s");
            }
            if (minor != 0)
            {
                if (major != 0)
                {
                    words.Add("and");
                }
                Cardinal(minor, words);
                if (subdivision == 'c')
                {
                    words.Add(minor == 1 ? "cent" : "cents");
                }
                else
                {
                    words.Add("pence");
                }
            }
            return true;
        }

        private static bool TryDecimal(string body, bool negative, bool ordinal, List<string> words)
        {
            int split = body.IndexOf('.');
            string whole = body.Substring(0, split);
            string fraction = body.Substring(split + 1).TrimEnd('.');
            if (fraction.Length == 0)
            {
                return false;
            }
            for (int i = 0; i < fraction.Length; i++)
            {
                if (!char.IsDigit(fraction[i]))
                {
                    return false;
                }
            }
            long major = 0;
            if (whole.Length > 0 &&
                !long.TryParse(whole, NumberStyles.None, CultureInfo.InvariantCulture, out major))
            {
                return false;
            }
            if (negative)
            {
                words.Add("minus");
            }
            if (whole.Length > 0)
            {
                if (ordinal)
                {
                    var parts = new List<string>(4);
                    Cardinal(major, parts);
                    Ordinalize(parts);
                    Append(parts, words);
                }
                else
                {
                    Cardinal(major, words);
                }
            }
            words.Add("point");
            for (int i = 0; i < fraction.Length; i++)
            {
                words.Add(Ones[fraction[i] - '0']);
            }
            return true;
        }

        /// <summary>
        /// Four-digit years, the way num2words renders them for misaki: 1990 is "nineteen ninety", not
        /// "one thousand nine hundred and ninety".
        /// </summary>
        private static void AddYear(int year, List<string> words)
        {
            int high = year / 100;
            int low = year % 100;
            if (year == 2000)
            {
                words.Add("two");
                words.Add("thousand");
            }
            else if (high == 20 && low < 10)
            {
                words.Add("two");
                words.Add("thousand");
                words.Add("and");
                Cardinal(low, words);
            }
            else if (low == 0)
            {
                Cardinal(high, words);
                words.Add("hundred");
            }
            else if (low < 10)
            {
                Cardinal(high, words);
                words.Add("oh");
                Cardinal(low, words);
            }
            else
            {
                Cardinal(high, words);
                Cardinal(low, words);
            }
        }

        /// <summary>Cardinal wording with "and" after hundreds, which is what the reference forms use.</summary>
        private static void Cardinal(long value, List<string> words)
        {
            if (value < 0)
            {
                words.Add("minus");
                value = -value;
            }
            if (value < 20)
            {
                words.Add(Ones[value]);
                return;
            }
            if (value < 100)
            {
                words.Add(Tens[value / 10]);
                if (value % 10 != 0)
                {
                    words.Add(Ones[value % 10]);
                }
                return;
            }
            if (value < 1000)
            {
                words.Add(Ones[value / 100]);
                words.Add("hundred");
                if (value % 100 != 0)
                {
                    words.Add("and");
                    Cardinal(value % 100, words);
                }
                return;
            }
            for (int i = 0; i < Scales.Length; i++)
            {
                if (value >= Scales[i])
                {
                    Cardinal(value / Scales[i], words);
                    words.Add(ScaleNames[i]);
                    if (value % Scales[i] != 0)
                    {
                        Cardinal(value % Scales[i], words);
                    }
                    return;
                }
            }
            words.Add(Ones[0]);
        }

        private static readonly string[] OrdinalOnes =
        {
            "zeroth", "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth",
            "ninth", "tenth", "eleventh", "twelfth", "thirteenth", "fourteenth", "fifteenth", "sixteenth",
            "seventeenth", "eighteenth", "nineteenth"
        };

        private static readonly string[] OrdinalTens =
        {
            "", "", "twentieth", "thirtieth", "fortieth", "fiftieth", "sixtieth", "seventieth",
            "eightieth", "ninetieth"
        };

        /// <summary>Turns the last word of a cardinal phrase into its ordinal: "twenty one" -> "twenty first".</summary>
        private static void Ordinalize(List<string> words)
        {
            if (words.Count == 0)
            {
                return;
            }
            string last = words[words.Count - 1];
            string replacement = null;
            for (int i = 0; i < Ones.Length; i++)
            {
                if (Ones[i] == last)
                {
                    replacement = OrdinalOnes[i];
                    break;
                }
            }
            if (replacement == null)
            {
                for (int i = 2; i < Tens.Length; i++)
                {
                    if (Tens[i] == last)
                    {
                        replacement = OrdinalTens[i];
                        break;
                    }
                }
            }
            if (replacement == null)
            {
                if (last == "hundred")
                {
                    replacement = "hundredth";
                }
                else if (last == "thousand")
                {
                    replacement = "thousandth";
                }
                else if (last == "million")
                {
                    replacement = "millionth";
                }
                else if (last == "billion")
                {
                    replacement = "billionth";
                }
                else if (last == "trillion")
                {
                    replacement = "trillionth";
                }
            }
            if (replacement != null)
            {
                words[words.Count - 1] = replacement;
            }
        }

        private static void Append(List<string> source, List<string> destination)
        {
            for (int i = 0; i < source.Count; i++)
            {
                destination.Add(source[i]);
            }
        }

        // ---------------------------------------------------------------- letter to sound

        private struct Rule
        {
            internal string Grapheme;
            internal string Left;
            internal string Right;
            internal string Phones;
        }

        /// <summary>
        /// The rule table: <c>grapheme|left context|right context|phonemes</c>, most specific first.
        /// </summary>
        /// <remarks>
        /// Context patterns are matched one letter per character, so no rule needs backtracking:
        /// <list type="bullet">
        /// <item><c>#</c> is the word boundary,</item>
        /// <item><c>@</c> any vowel letter, <c>+</c> a front vowel (e,i,y), <c>-</c> a back vowel (a,o,u),</item>
        /// <item><c>C</c> any consonant letter, <c>V</c> any letter,</item>
        /// <item>anything else is that letter.</item>
        /// </list>
        /// The outputs use only IPA characters from <see cref="KokoroVocab"/>; in particular /g/ is
        /// written <c>ɡ</c> (U+0261) and the diphthongs use Kokoro's single-letter forms, because ASCII
        /// <c>g</c> and <c>e</c> are not in the vocabulary at all. Anything the table cannot explain falls
        /// through unmatched and is dropped, which is what keeps this total: no input can produce a symbol
        /// Kokoro does not have.
        /// </remarks>
        private static readonly string[] RawRules =
        {
            "tion|||ʃən",
            "tions|||ʃənz",
            "sion|||ʒən",
            "cious|||ʃəs",
            "cial|||ʃəl",
            "tial|||ʃəl",
            "ture|||ʧɚ",
            "sure||#|ʒɚ",
            "ough||t|ɔ",
            "ough||#|O",
            "ough|||ʌf",
            "augh|||ɔ",
            "eigh|||A",
            "igh|||I",
            "ight|||It",
            "air|||ɛɹ",
            "ear|||ɪɹ",
            "eer|||ɪɹ",
            "are||#|ɛɹ",
            "ore||#|ɔɹ",
            "ire||#|Iɹ",
            "ere||#|ɪɹ",
            "ure||#|jɚ",
            "oar|||ɔɹ",
            "our||#|Wɚ",
            "sch|#||sk",
            "tch|||ʧ",
            "dge|||ʤ",
            "ing||#|ɪŋ",
            "ous||#|əs",
            "ful||#|fəl",
            "ness||#|nəs",
            "less||#|ləs",
            "ment||#|mənt",
            "able||#|əbəl",
            "ible||#|əbəl",
            "ould|||Ud",
            "auld|||ɔd",
            "alk|||ɔk",
            "old|||Old",
            "ee|||i",
            "ea|||i",
            "ei|||A",
            "eu|||ju",
            "ew|||u",
            "ey|||i",
            "oi|||Y",
            "oy|||Y",
            "ou|||W",
            "ow||#|O",
            "ow|||W",
            "oa|||O",
            "oo||k|ʊ",
            "oo|||u",
            "au|||ɔ",
            "aw|||ɔ",
            "ai|||A",
            "ay|||A",
            "ue|||u",
            "ui|||u",
            "ie|||i",
            "oe|||O",
            "sh|||ʃ",
            "ch|||ʧ",
            "ph|||f",
            "gh|#||ɡ",
            "gh|||",
            "ck|||k",
            "qu|||kw",
            "wh|||w",
            "ng||#|ŋ",
            "ng|||ŋɡ",
            "n|@|k|ŋ",
            "n|@|g|ŋ",
            "kn|#||n",
            "wr|#||ɹ",
            "gn|#||n",
            "mb||#|m",
            "mn||#|m",
            "ps|#||s",
            "pn|#||n",
            "dg|||ʤ",
            "tz|||ts",
            "sc||+|s",
            "sc|||sk",
            "th|||θ",
            "cc|||k",
            "bb|||b",
            "dd|||d",
            "ff|||f",
            "gg|||ɡ",
            "ll|||l",
            "mm|||m",
            "nn|||n",
            "pp|||p",
            "rr|||ɹ",
            "ss|||s",
            "tt|||t",
            "zz|||z",
            "ar||#|ɑɹ",
            "ar|||ɑɹ",
            "or|||ɔɹ",
            "er||#|ɚ",
            "er|||ɜɹ",
            "ir|||ɜɹ",
            "ur|||ɜɹ",
            "yr|||ɜɹ",
            "ed|t|#|ᵻd",
            "ed|d|#|ᵻd",
            "ed|p|#|t",
            "ed|k|#|t",
            "ed|f|#|t",
            "ed|s|#|t",
            "ed|x|#|t",
            "ed|h|#|t",
            "ed||#|d",
            "es|s|#|ᵻz",
            "es|x|#|ᵻz",
            "es||#|z",
            "s|@|@|z",
            "s|||s",
            "le|C|#|əl",
            "a||Ce#|A",
            "a|||æ",
            "e||#|",
            "e|||ɛ",
            "i||Ce#|I",
            "i|||ɪ",
            "o||Ce#|O",
            "o||#|O",
            "o|||ɑ",
            "u||Ce#|ju",
            "u|||ʌ",
            "c||+|s",
            "c|||k",
            "g||+|ʤ",
            "g|||ɡ",
            "y|#||j",
            "y||#|i",
            "y|||ɪ",
            "x|#||z",
            "x|||ks",
            "q|||k",
            "b|||b",
            "d|||d",
            "f|||f",
            "h|||h",
            "j|||ʤ",
            "k|||k",
            "l|||l",
            "m|||m",
            "n|||n",
            "p|||p",
            "r|||ɹ",
            "t|||t",
            "v|||v",
            "w|||w",
            "z|||z"
        };

        private static readonly char[] FieldSeparator = { '|' };
        private static readonly Dictionary<char, Rule[]> Rules = BuildRules();

        private const string VowelLetters = "aeiou";
        private const string FrontVowels = "eiy";
        private const string BackVowels = "aou";
        private const string ConsonantLetters = "bcdfghjklmnpqrstvwxz";
        private const string IpaVowels = "AIOUWYaiouæɑɒɔəɛɜɪʊʌᵻ";

        private static Dictionary<char, Rule[]> BuildRules()
        {
            var grouped = new Dictionary<char, List<Rule>>(RawRules.Length);
            for (int i = 0; i < RawRules.Length; i++)
            {
                string[] fields = RawRules[i].Split(FieldSeparator);
                if (fields.Length != 4 || fields[0].Length == 0)
                {
                    throw new InvalidOperationException("letter-to-sound rule " + i.ToString(CultureInfo.InvariantCulture) + " is malformed");
                }
                var rule = new Rule();
                rule.Grapheme = fields[0];
                rule.Left = fields[1];
                rule.Right = fields[2];
                rule.Phones = fields[3];
                char first = rule.Grapheme[0];
                List<Rule> list;
                if (!grouped.TryGetValue(first, out list))
                {
                    list = new List<Rule>(8);
                    grouped.Add(first, list);
                }
                list.Add(rule);
            }
            var result = new Dictionary<char, Rule[]>(grouped.Count);
            foreach (KeyValuePair<char, List<Rule>> entry in grouped)
            {
                List<Rule> list = entry.Value;
                // Longest grapheme first, and a stable order for equal lengths so the authored priority
                // inside one grapheme survives (insertion sort: the arrays are under a dozen entries).
                for (int i = 1; i < list.Count; i++)
                {
                    Rule carried = list[i];
                    int j = i - 1;
                    while (j >= 0 && list[j].Grapheme.Length < carried.Grapheme.Length)
                    {
                        list[j + 1] = list[j];
                        j--;
                    }
                    list[j + 1] = carried;
                }
                result.Add(entry.Key, list.ToArray());
            }
            return result;
        }

        /// <summary>
        /// Spelling of a lowercase word to IPA, with one primary stress before its first vowel.
        /// </summary>
        private static void AddLetterToSound(string word, List<string> output)
        {
            var letters = new StringBuilder(word.Length);
            for (int i = 0; i < word.Length; i++)
            {
                char c = word[i];
                if (c >= 'a' && c <= 'z')
                {
                    letters.Append(c);
                }
            }
            if (letters.Length == 0)
            {
                return;
            }
            string spelling = letters.ToString();
            var ipa = new StringBuilder(spelling.Length * 2);
            int at = 0;
            while (at < spelling.Length)
            {
                int length = spelling.Length - at;
                if (length > 4)
                {
                    length = 4;
                }
                bool matched = false;
                Rule[] candidates;
                if (Rules.TryGetValue(spelling[at], out candidates))
                {
                    for (int r = 0; r < candidates.Length; r++)
                    {
                        Rule rule = candidates[r];
                        if (rule.Grapheme.Length > length)
                        {
                            continue;
                        }
                        if (string.CompareOrdinal(spelling, at, rule.Grapheme, 0, rule.Grapheme.Length) != 0)
                        {
                            continue;
                        }
                        if (!MatchLeft(spelling, at, rule.Left) || !MatchRight(spelling, at + rule.Grapheme.Length, rule.Right))
                        {
                            continue;
                        }
                        if (rule.Grapheme == "y" && rule.Phones == "i" && NoVowelBefore(spelling, at))
                        {
                            // "my", "why", "sky": a final y with no other vowel is /aɪ/, not /i/.
                            ipa.Append('I');
                        }
                        else
                        {
                            ipa.Append(rule.Phones);
                        }
                        at += rule.Grapheme.Length;
                        matched = true;
                        break;
                    }
                }
                if (!matched)
                {
                    at++;
                }
            }
            if (ipa.Length == 0)
            {
                return;
            }
            string result = ipa.ToString();
            int vowel = FirstVowel(result);
            output.Add(vowel < 0 ? result : result.Insert(vowel, "ˈ"));
        }

        private static int FirstVowel(string ipa)
        {
            for (int i = 0; i < ipa.Length; i++)
            {
                if (IpaVowels.IndexOf(ipa[i]) >= 0)
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool NoVowelBefore(string letters, int at)
        {
            for (int i = 0; i < at; i++)
            {
                if (VowelLetters.IndexOf(letters[i]) >= 0)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool MatchLeft(string letters, int at, string pattern)
        {
            if (pattern.Length == 0)
            {
                return true;
            }
            int cursor = at - 1;
            for (int p = pattern.Length - 1; p >= 0; p--)
            {
                char test = pattern[p];
                if (test == '#')
                {
                    if (cursor >= 0)
                    {
                        return false;
                    }
                    continue;
                }
                if (cursor < 0)
                {
                    return false;
                }
                if (!LetterMatches(test, letters[cursor]))
                {
                    return false;
                }
                cursor--;
            }
            return true;
        }

        private static bool MatchRight(string letters, int at, string pattern)
        {
            int cursor = at;
            for (int p = 0; p < pattern.Length; p++)
            {
                char test = pattern[p];
                if (test == '#')
                {
                    if (cursor != letters.Length)
                    {
                        return false;
                    }
                    continue;
                }
                if (cursor >= letters.Length)
                {
                    return false;
                }
                if (!LetterMatches(test, letters[cursor]))
                {
                    return false;
                }
                cursor++;
            }
            return true;
        }

        private static bool LetterMatches(char test, char letter)
        {
            switch (test)
            {
                case '@':
                    return VowelLetters.IndexOf(letter) >= 0;
                case '+':
                    return FrontVowels.IndexOf(letter) >= 0;
                case '-':
                    return BackVowels.IndexOf(letter) >= 0;
                case 'C':
                    return ConsonantLetters.IndexOf(letter) >= 0;
                case 'V':
                    return true;
                default:
                    return test == letter;
            }
        }

        /// <summary>
        /// Folds accented Latin letters to ASCII and drops everything else, so that <c>café</c> reaches the
        /// lexicon as <c>cafe</c> and text in other scripts reduces to nothing.
        /// </summary>
        private static string FoldWord(string word)
        {
            bool changed = false;
            var builder = new StringBuilder(word.Length);
            for (int i = 0; i < word.Length; i++)
            {
                char c = word[i];
                if (c < 128)
                {
                    builder.Append(c);
                    continue;
                }
                changed = true;
                char folded = FoldLatin(c);
                if (folded != '\0')
                {
                    builder.Append(folded);
                }
            }
            return changed ? builder.ToString() : word;
        }

        /// <summary>Accented Latin letters folded to ASCII, or NUL for anything else (which is dropped).</summary>
        private static char FoldLatin(char c)
        {
            switch (c)
            {
                case 'à': case 'á': case 'â': case 'ã': case 'ä': case 'å': case 'ā': case 'ă': case 'ą':
                    return 'a';
                case 'ç': case 'ć': case 'č':
                    return 'c';
                case 'ď': case 'ð':
                    return 'd';
                case 'è': case 'é': case 'ê': case 'ë': case 'ē': case 'ĕ': case 'ė': case 'ę': case 'ě':
                    return 'e';
                case 'ĝ': case 'ğ':
                    return 'g';
                case 'ì': case 'í': case 'î': case 'ï': case 'ī': case 'ĭ': case 'į': case 'ı':
                    return 'i';
                case 'ł': case 'ľ':
                    return 'l';
                case 'ñ': case 'ń': case 'ň':
                    return 'n';
                case 'ò': case 'ó': case 'ô': case 'õ': case 'ö': case 'ø': case 'ō': case 'ŏ': case 'ő':
                    return 'o';
                case 'ŕ': case 'ř':
                    return 'r';
                case 'ś': case 'š': case 'ş':
                    return 's';
                case 'ť': case 'ţ':
                    return 't';
                case 'ù': case 'ú': case 'û': case 'ü': case 'ū': case 'ŭ': case 'ů': case 'ű':
                    return 'u';
                case 'ý': case 'ÿ':
                    return 'y';
                case 'ź': case 'ż': case 'ž':
                    return 'z';
                default:
                    return '\0';
            }
        }

        // ---------------------------------------------------------------- text handling

        private struct Token
        {
            internal string Text;

            /// <summary>False for punctuation, which passes through as its own Kokoro symbol.</summary>
            internal bool IsWord;

            /// <summary>True when the source had whitespace before this token.</summary>
            internal bool Spaced;
        }

        /// <summary>The punctuation Kokoro models as phonemes, so it survives as its own token.</summary>
        private const string Punctuation = ",.!?;:—…\"()“”";

        /// <summary>
        /// Collapses whitespace, folds the apostrophe and dash families, and drops control and zero-width
        /// characters.
        /// </summary>
        /// <remarks>
        /// Curly double quotes are left alone: <c>“</c>, <c>”</c> and <c>"</c> are three separate Kokoro
        /// symbols, and the model was trained on them as such. Only the apostrophe family (which the
        /// lexicon keys on) and the dash family (which the model has one symbol for) are folded.
        /// </remarks>
        private static string Normalize(string text)
        {
            var builder = new StringBuilder(text.Length);
            bool space = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    space = builder.Length > 0;
                    continue;
                }
                if (c < ' ' || c == '\u007f' || (c >= '\u200b' && c <= '\u200d') || c == '\ufeff' ||
                    c == '\u00ad')
                {
                    continue;
                }
                if (space)
                {
                    builder.Append(' ');
                    space = false;
                }
                switch (c)
                {
                    case '‘':
                    case '’':
                    case '‛':
                    case 'ʼ':
                    case '′':
                    case '`':
                        builder.Append('\'');
                        break;
                    case '–':
                    case '―':
                    case '−':
                        builder.Append('—');
                        break;
                    case '„':
                    case '‟':
                    case '«':
                    case '»':
                        builder.Append('"');
                        break;
                    default:
                        builder.Append(c);
                        break;
                }
            }
            return builder.ToString();
        }

        /// <summary>
        /// Splits normalised text into words and punctuation tokens, remembering the source spacing so the
        /// output can reproduce it.
        /// </summary>
        private static void Split(string text, List<Token> tokens)
        {
            bool space = false;
            int at = 0;
            while (at < text.Length)
            {
                char c = text[at];
                if (c == ' ')
                {
                    space = true;
                    at++;
                    continue;
                }
                if (Punctuation.IndexOf(c) >= 0)
                {
                    Push(tokens, text.Substring(at, 1), false, space);
                    space = false;
                    at++;
                    continue;
                }
                if (c == '-' || c == '+')
                {
                    char next = at + 1 < text.Length ? text[at + 1] : '\0';
                    char previous = at > 0 ? text[at - 1] : '\0';
                    bool sign = char.IsDigit(next) && (space || !char.IsLetterOrDigit(previous));
                    if (!sign)
                    {
                        // A hyphen between letters never reaches here: the word scanner below takes it with
                        // the compound, so "well-known" is looked up as one headword. What is left is a
                        // hyphen hanging off a word edge ("-able", "multi-"), which carries no
                        // pronunciation, or a real dash between spaces.
                        if (char.IsLetter(next) || (!space && char.IsLetter(previous)))
                        {
                            at++;
                            space = true;
                            continue;
                        }
                        Push(tokens, "—", false, space);
                        space = false;
                        at++;
                        continue;
                    }
                }
                int start = at;
                if ((c == '-' || c == '+') && at + 1 < text.Length && char.IsDigit(text[at + 1]))
                {
                    at++;
                }
                else if ((c == '$' || c == '£' || c == '€') && at + 1 < text.Length && char.IsDigit(text[at + 1]))
                {
                    at++;
                }
                else if (c == '$' || c == '£' || c == '€' || c == '%')
                {
                    if (c == '%')
                    {
                        Push(tokens, "percent", true, space);
                        space = false;
                    }
                    at++;
                    continue;
                }
                while (at < text.Length && IsWordBody(text, at, start))
                {
                    at++;
                }
                if (at == start)
                {
                    at++;
                    continue;
                }
                Push(tokens, text.Substring(start, at - start), true, space);
                space = false;
            }
        }

        /// <summary>Letters, digits and apostrophes, plus the separators inside numbers and abbreviations.</summary>
        private static bool IsWordBody(string text, int at, int start)
        {
            char c = text[at];
            if (c == '\'')
            {
                return true;
            }
            if (char.IsLetterOrDigit(c))
            {
                return true;
            }
            char previous = at > 0 ? text[at - 1] : '\0';
            char next = at + 1 < text.Length ? text[at + 1] : '\0';
            if (c == ',' || c == ':')
            {
                return char.IsDigit(previous) && char.IsDigit(next);
            }
            if (c == '-')
            {
                // "well-known" and "cat-o'-nine-tails" are single headwords.
                return (char.IsLetter(previous) || previous == '\'') && char.IsLetter(next);
            }
            if (c == '.')
            {
                if (char.IsDigit(previous) && char.IsDigit(next))
                {
                    return true;
                }
                // One letter and a dot is an abbreviation, "e.g" and "i.e"; "example.com" is not.
                return at - start == 1 && char.IsLetter(previous) && char.IsLetter(next);
            }
            if (c == '%')
            {
                return char.IsDigit(previous);
            }
            return false;
        }

        private static void Push(List<Token> tokens, string text, bool isWord, bool spaced)
        {
            var token = new Token();
            token.Text = text;
            token.IsWord = isWord;
            token.Spaced = spaced;
            tokens.Add(token);
        }

        private static void Append(StringBuilder builder, string ipa, bool space, ref bool empty)
        {
            if (ipa.Length == 0)
            {
                return;
            }
            if (space)
            {
                builder.Append(' ');
            }
            builder.Append(ipa);
            empty = false;
        }
    }
}
