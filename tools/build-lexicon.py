"""Build assets/g2p/en-lexicon.bin from the English IPA lexicons of the misaki package.

misaki (the reference Kokoro front end) ships four flat JSON maps of word -> IPA:
us_gold.json, us_silver.json, gb_gold.json, gb_silver.json. This script folds them into the
single binary the plugin loads, so the mod needs no Python, no spaCy and no JSON parsing at
run time.

Usage, from the repo root, with the server virtualenv (the only interpreter here that has
misaki installed):

    server\\.venv\\Scripts\\python.exe tools\\build-lexicon.py

Binary format (all integers little-endian):

    char[4]  "KGL1"
    int32    entry count
    entry*   uint8 usLen, usLen bytes of UTF-8 US IPA
             uint8 gbLen, gbLen bytes of UTF-8 GB IPA
             uint8 wordLen, wordLen bytes of UTF-8 lowercase word

Entries are sorted by the UTF-8 bytes of the word, which is what EnglishG2P's binary search
assumes; comparisons on equal-prefix ASCII and on multi-byte UTF-8 agree because UTF-8
preserves code point order. A length of zero means that variant has no entry, and the engine
falls back from US to GB.

Which words are kept:

  * The gold tiers (us_gold, gb_gold) define the word list: 98,862 distinct lowercase words.
    They are the curated half of the upstream data; the silver tiers are model-generated
    long-tail guesses whose 112,000 extra headwords the letter-to-sound rules cover instead.
  * Both tiers still feed the IPA *inside* that word list, because a word is often in one
    gold file and only in the other language's silver file.
  * The GB IPA is stored only for words the US tiers do not cover. Carrying both dialects for
    every entry measured 3.37 MB against a 3 MB asset budget, and the engine's lookup is
    US-first, so a GB string is only ever read when the US string is absent. Those are the
    8,010 GB-only words, which is exactly the set the fallback can reach.

The 790 gold entries whose value is a JSON object keyed by part of speech store their
DEFAULT reading: the engine has no tagger, so it cannot choose between the tagged variants.
"""

import json
import os
import struct
import sys
import unicodedata

HEADER = b"KGL1"
MAGIC_ENTRIES = 0

# Source order matters: the first file to define a word wins, gold before silver.
US_SOURCES = ["us_gold.json", "us_silver.json"]
GB_SOURCES = ["gb_gold.json", "gb_silver.json"]
WORD_SOURCES = ["us_gold.json", "gb_gold.json"]


def misaki_data_dir():
    try:
        import misaki
    except ImportError:
        sys.exit("misaki is not importable; run this with server/.venv/Scripts/python.exe")
    return os.path.join(os.path.dirname(os.path.abspath(misaki.__file__)), "data")


def load(data_dir, name):
    path = os.path.join(data_dir, name)
    if not os.path.exists(path):
        sys.exit("missing lexicon file: " + path)
    with open(path, encoding="utf-8") as handle:
        raw = json.load(handle)
    flat = {}
    for word, value in raw.items():
        if isinstance(value, dict):
            value = value.get("DEFAULT")
        if value is None:
            continue
        flat[word] = value
    return flat


def fold(word):
    """The form EnglishG2P looks up: lowercase, apostrophes and accents folded to ASCII.

    The engine folds the query the same way (EnglishG2P.FoldWord), so every stored key is
    reachable. Folding also merges the spellings of one word that differ only in punctuation or
    an accent, and the first one wins.
    """
    builder = []
    lowered = word.lower().replace("\u2019", "'").replace("\u2018", "'").replace("\u02bc", "'")
    lowered = lowered.replace("\u2013", "-").replace("\u2014", "-").replace("\u2212", "-")
    for c in unicodedata.normalize("NFD", lowered):
        if unicodedata.combining(c):
            continue
        if ord(c) < 128:
            builder.append(c)
            continue
        mapped = EXTRA_FOLDS.get(c)
        if mapped is not None:
            builder.append(mapped)
    return "".join(builder)


# Letters that carry a stroke or a slash and do not decompose under NFD; the same list the C#
# side folds, so the two agree on what a word looks like.
EXTRA_FOLDS = {
    "\u00f8": "o",  # ø
    "\u0142": "l",  # ł
    "\u0131": "i",  # ı
    "\u0111": "d",  # đ
    "\u0167": "t",  # ŧ
    "\u0127": "h",  # ħ
}


def lower_index(data_dir, names):
    """Word -> IPA keyed by the folded spelling, first file wins.

    Folding the keys is misaki's own Lexicon.grow_dictionary in spirit: it registers 'london' for
    'London' so that a lowercase lookup hits a capitalised headword. A word that is already in
    the lexicon keeps its own reading, which is why the alias pass runs second: without that
    order the letter name 'A' would shadow the article 'a'.
    """
    index = {}
    for name in names:
        for word, ipa in load(data_dir, name).items():
            if word == word.lower():
                index.setdefault(fold(word), ipa)
    for name in names:
        for word, ipa in load(data_dir, name).items():
            index.setdefault(fold(word), ipa)
    return index


def main():
    repo_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    data_dir = misaki_data_dir()
    us_index = lower_index(data_dir, US_SOURCES)
    gb_index = lower_index(data_dir, GB_SOURCES)

    words = set()
    for word in list(load(data_dir, WORD_SOURCES[0])) + list(load(data_dir, WORD_SOURCES[1])):
        folded = fold(word)
        if not folded or any(c.isspace() for c in folded):
            continue  # unreachable from the whitespace tokeniser
        words.add(folded)

    entries = []
    us_count = 0
    gb_count = 0
    both_known = 0
    for word in words:
        us = us_index.get(word)
        gb = gb_index.get(word)
        if us is not None and gb is not None:
            both_known += 1
        if us:
            us_count += 1
        elif gb:
            gb_count += 1
        else:
            gb = None
        us = us or ""
        gb = gb or ""
        # A GB string is only ever read when the US string is absent; see the module docstring.
        if us:
            gb = ""
        entries.append((word, us, gb))

    entries.sort(key=lambda entry: entry[0].encode("utf-8"))

    out = bytearray()
    out += HEADER
    out += struct.pack("<i", len(entries))
    for word, us, gb in entries:
        us_bytes = us.encode("utf-8")
        gb_bytes = gb.encode("utf-8")
        word_bytes = word.encode("utf-8")
        if len(us_bytes) > 255 or len(gb_bytes) > 255 or len(word_bytes) > 255:
            sys.exit("entry too long for the uint8 length prefix: " + word)
        out += struct.pack("<B", len(us_bytes))
        out += us_bytes
        out += struct.pack("<B", len(gb_bytes))
        out += gb_bytes
        out += struct.pack("<B", len(word_bytes))
        out += word_bytes

    destination = os.path.join(repo_root, "assets", "g2p", "en-lexicon.bin")
    os.makedirs(os.path.dirname(destination), exist_ok=True)
    with open(destination, "wb") as handle:
        handle.write(out)

    print("sources: %s" % ", ".join(WORD_SOURCES))
    print("word list: %d words (%d US, %d GB-only, %d have both)" % (
        len(entries), us_count, gb_count, both_known))
    print("wrote %s: %d bytes (%.2f MB)" % (
        os.path.relpath(destination, repo_root).replace(os.sep, "/"),
        len(out), len(out) / 1048576.0))


if __name__ == "__main__":
    main()
