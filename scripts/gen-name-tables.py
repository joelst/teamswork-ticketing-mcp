#!/usr/bin/env python3
"""Generates the tables the name check uses to compare names as a reader sees them, without globalisation data.

    python scripts/gen-name-tables.py [confusables.txt]

Writes src/TeamsWork.Ticketing.Mcp/Tools/BaseLetters.cs and LookAlikes.cs. The server's Linux build has no ICU, so
string.Normalize() does nothing there; these tables carry the Unicode data the check needs instead:

* BaseLetters: the base letter of each precomposed accented Latin, Greek and Cyrillic letter (from the canonical
  decompositions in Python's unicodedata), plus the stroke letters that have no decomposition.
* LookAlikes: code points that display as plain Latin letters or digits, mapped to them (lower case): the compatibility
  forms NFKC folds (full-width, mathematical, circled, ligatures, Roman numerals) and the UTS #39 confusables whose
  prototype is plain Latin (Greek alpha, Cyrillic a, small capitals). confusables.txt is read from the path given, or
  downloaded from unicode.org. Commit the generated files; nothing is downloaded at build or run time.
"""
import io
import os
import sys
import unicodedata
import urllib.request

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
OUT = os.path.join(ROOT, 'src', 'TeamsWork.Ticketing.Mcp', 'Tools')
CONFUSABLES_URL = 'https://www.unicode.org/Public/security/latest/confusables.txt'


def is_mark(c):
    return unicodedata.category(c) in ('Mn', 'Me')


def assigned(cp):
    return unicodedata.category(chr(cp)) not in ('Cn', 'Cs', 'Co')


# ---- Base letters ---------------------------------------------------------------------------------------------------

BASE_RANGES = [(0x00C0, 0x024F), (0x0370, 0x03FF), (0x0400, 0x04FF), (0x1E00, 0x1EFF), (0x1F00, 0x1FFF)]
# Letters with a stroke or bar have no decomposition but read as their base letter.
STROKES = {
    0x00D8: 'O', 0x00F8: 'o', 0x0110: 'D', 0x0111: 'd', 0x0126: 'H', 0x0127: 'h', 0x0141: 'L', 0x0142: 'l',
    0x0166: 'T', 0x0167: 't', 0x0180: 'b', 0x0197: 'I', 0x01B5: 'Z', 0x01B6: 'z', 0x01E4: 'G', 0x01E5: 'g',
    0x0268: 'i', 0x0289: 'u',
}


def base_letters():
    pairs = []
    for lo, hi in BASE_RANGES:
        for cp in range(lo, hi + 1):
            if cp in STROKES:
                pairs.append((cp, ord(STROKES[cp])))
                continue
            if not assigned(cp):
                continue
            d = unicodedata.normalize('NFD', chr(cp))
            if len(d) >= 2 and ord(d[0]) != cp and all(is_mark(c) for c in d[1:]) and unicodedata.category(d[0]) in ('Lu', 'Ll', 'Lt'):
                pairs.append((cp, ord(d[0])))
    return pairs


# ---- Look-alikes ----------------------------------------------------------------------------------------------------

def plain_latin(text):
    """The text as lower-case ASCII letters and digits once marks are removed, or None if it is anything else."""
    stripped = ''.join(c for c in unicodedata.normalize('NFD', text) if not is_mark(c))
    if 0 < len(stripped) <= 4 and all(c.isascii() and c.isalnum() for c in stripped):
        return stripped.lower()
    return None


def confusables(path):
    if path:
        data = io.open(path, encoding='utf-8-sig').read()
    else:
        data = urllib.request.urlopen(CONFUSABLES_URL, timeout=60).read().decode('utf-8-sig')
    version = next((l.split(':', 1)[1].strip() for l in data.splitlines() if l.startswith('# Version:')), 'unknown')
    table = {}
    for line in data.splitlines():
        line = line.split('#', 1)[0].strip()
        if not line:
            continue
        source, target = [f.strip() for f in line.split(';')[:2]]
        if ' ' in source:
            continue  # a sequence, not one code point
        table[int(source, 16)] = ''.join(chr(int(t, 16)) for t in target.split())
    return table, version


# Look-alikes neither source has: the small capitals, which confusables.txt leaves out and NFKC doesn't fold.
SUPPLEMENT = dict(zip(
    [0x1D00, 0x0299, 0x1D04, 0x1D05, 0x1D07, 0xA730, 0x0262, 0x029C, 0x026A, 0x1D0A, 0x1D0B, 0x029F, 0x1D0D,
     0x0274, 0x1D0F, 0x1D18, 0xA7AF, 0x0280, 0xA731, 0x1D1B, 0x1D1C, 0x1D20, 0x1D21, 0x028F, 0x1D22],
    'abcdefghijklmnopqrstuvwyz'))


def look_alikes(confusable):
    pairs = dict(SUPPLEMENT)
    for cp in range(0x80, 0x20000):
        if not assigned(cp) or cp in SUPPLEMENT:
            continue
        c = chr(cp)
        # The confusables prototype first (what it looks like), then the compatibility form (what it means).
        mapped = plain_latin(confusable[cp]) if cp in confusable else None
        if mapped is None:
            nfkc = unicodedata.normalize('NFKC', c)
            mapped = plain_latin(nfkc) if nfkc != c else None
        if mapped is not None and mapped != c.lower():
            pairs[cp] = mapped
    return pairs


# ---- Output ---------------------------------------------------------------------------------------------------------

def write(name, text):
    io.open(os.path.join(OUT, name), 'w', encoding='utf-8', newline='\r\n').write(text)


def main():
    bases = base_letters()
    rows = []
    for i in range(0, len(bases), 16):
        part = bases[i:i + 16]
        rows.append('        ("%s",\n         "%s"),' % (''.join('\\u%04X' % f for f, _ in part), ''.join('\\u%04X' % t for _, t in part)))
    write('BaseLetters.cs', '''// <auto-generated>Generated by scripts/gen-name-tables.py from Unicode %s data. Don't edit by hand.</auto-generated>
// Contains data derived from the Unicode Character Database and UTS #39. Copyright (c) Unicode, Inc. Used under the
// Unicode License v3: https://www.unicode.org/license.txt
#nullable enable
using System.Collections.Frozen;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// The base letter of each precomposed accented Latin, Greek and Cyrillic letter (é to e, Ǻ to A, ό to ο, й to и), and
/// of the Latin letters with a stroke that have no decomposition (ø, ł, đ), so the name check needs no globalisation
/// data at run time.
/// </summary>
internal static class BaseLetters
{
    private static readonly FrozenDictionary<int, int> Map = new (string From, string To)[]
    {
%s
    }
    .SelectMany(m => m.From.Zip(m.To, (from, to) => KeyValuePair.Create((int)from, (int)to)))
    .ToFrozenDictionary();

    /// <summary>The base letter of <paramref name="c"/>, or <paramref name="c"/> itself when it has no accent.</summary>
    public static int Of(int c) => Map.GetValueOrDefault(c, c);
}
''' % (unicodedata.unidata_version, '\n'.join(rows)))

    table, version = confusables(sys.argv[1] if len(sys.argv) > 1 else None)
    looks = look_alikes(table)
    # Examples the check must catch; a change in the data that drops one fails generation rather than weakening the check.
    for cp, want in {0x1D409: 'j', 0x24BF: 'j', 0x1D0A: 'j', 0x0430: 'a', 0x03B1: 'a', 0xFB01: 'fi', 0x216E: 'd', 0x216D: 'c',
                     0x04CF: 'l', 0xFF2A: 'j', 0x0456: 'i', 0x1F130: 'a'}.items():
        assert looks.get(cp) == want, (hex(cp), looks.get(cp), want)
    entries = ['%X=%s' % (cp, looks[cp]) for cp in sorted(looks)]
    lines = []
    for i in range(0, len(entries), 12):
        lines.append('        "%s;" +' % ';'.join(entries[i:i + 12]))
    lines[-1] = lines[-1][:-2] + ';'
    write('LookAlikes.cs', '''// <auto-generated>Generated by scripts/gen-name-tables.py from Unicode %s data and UTS #39 confusables %s. Don't edit by hand.</auto-generated>
// Contains data derived from the Unicode Character Database and UTS #39. Copyright (c) Unicode, Inc. Used under the
// Unicode License v3: https://www.unicode.org/license.txt
#nullable enable
using System.Collections.Frozen;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// Code points that display as plain Latin letters or digits, mapped to them in lower case: the compatibility forms NFKC
/// folds (full-width, mathematical, circled and squared letters, ligatures, Roman numerals) and the UTS #39 confusables
/// whose prototype is plain Latin (Greek and Cyrillic look-alikes, small capitals), so the name check needs no
/// globalisation data at run time.
/// </summary>
internal static class LookAlikes
{
    // Code point (hexadecimal) = the plain letters it looks like, separated by semicolons.
    private const string Data =
%s

    private static readonly FrozenDictionary<int, string> Map = Data
        .Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(e => e.Split('='))
        .ToFrozenDictionary(e => int.Parse(e[0], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture), e => e[1]);

    /// <summary>The plain lower-case letters <paramref name="c"/> looks like, or null when it isn't a look-alike.</summary>
    public static string? Of(int c) => Map.GetValueOrDefault(c);
}
''' % (unicodedata.unidata_version, version, '\n'.join(lines)))
    print(len(bases), 'base letters,', len(looks), 'look-alikes')


if __name__ == '__main__':
    main()
