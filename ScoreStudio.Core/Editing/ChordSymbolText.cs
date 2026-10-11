using ScoreStudio.Models;

namespace ScoreStudio.Editing;

/// <summary>Reads a typed chord symbol such as "Bbmaj7/D" (docs/SPRINT4G_LYRICS_CHORDS.md).</summary>
public static class ChordSymbolText
{
    private const int MaxLength = 20;

    /// <summary>Chord qualities by their usual spellings, in MusicXML's names.</summary>
    private static readonly Dictionary<string, string> Kinds = new(StringComparer.Ordinal)
    {
        [""] = "major",
        ["m"] = "minor", ["min"] = "minor", ["-"] = "minor",
        ["7"] = "dominant",
        ["maj7"] = "major-seventh", ["M7"] = "major-seventh", ["Δ7"] = "major-seventh", ["Δ"] = "major-seventh",
        ["m7"] = "minor-seventh", ["min7"] = "minor-seventh", ["-7"] = "minor-seventh",
        ["dim"] = "diminished", ["o"] = "diminished",
        ["dim7"] = "diminished-seventh", ["o7"] = "diminished-seventh",
        ["m7b5"] = "half-diminished", ["ø"] = "half-diminished", ["ø7"] = "half-diminished",
        ["aug"] = "augmented", ["+"] = "augmented",
        ["sus4"] = "suspended-fourth", ["sus"] = "suspended-fourth",
        ["sus2"] = "suspended-second",
        ["6"] = "major-sixth", ["m6"] = "minor-sixth",
        ["9"] = "dominant-ninth", ["maj9"] = "major-ninth", ["m9"] = "minor-ninth",
    };

    /// <summary>
    /// The chord symbol for <paramref name="typed"/>, or null with the reason in Korean. The printed text keeps what was
    /// typed, with ♯ and ♭ stored as # and b.
    /// </summary>
    public static ScoreChordSymbol? TryParse(string typed, out string error)
    {
        var text = typed.Trim().Replace('♯', '#').Replace('♭', 'b');
        error = "";
        if (text.Length == 0)
        {
            error = "코드 이름이 비어 있습니다.";
            return null;
        }

        if (text.Length > MaxLength || text.Any(char.IsControl) || text.Contains(' '))
        {
            error = $"코드 이름은 공백 없이 {MaxLength}자까지 쓸 수 있습니다.";
            return null;
        }

        if (!TryNote(text, 0, out var rootStep, out var rootAlter, out var afterRoot))
        {
            error = "코드 이름은 A~G의 근음으로 시작해야 합니다(예: C, F#m, Bbmaj7/D).";
            return null;
        }

        var slash = text.IndexOf('/', afterRoot);
        var quality = slash < 0 ? text[afterRoot..] : text[afterRoot..slash];
        string? bassStep = null;
        var bassAlter = 0;
        if (slash >= 0)
        {
            if (!TryNote(text, slash + 1, out var step, out bassAlter, out var end) || end != text.Length)
            {
                error = "'/' 뒤에는 베이스음 이름(A~G)을 씁니다(예: C/E, Bb/D).";
                return null;
            }

            bassStep = step;
        }

        return new ScoreChordSymbol
        {
            RootStep = rootStep, RootAlter = rootAlter, BassStep = bassStep, BassAlter = bassAlter,
            Kind = Kinds.TryGetValue(quality, out var kind) ? kind : "other",
            Text = text,
        };
    }

    /// <summary>A note name at <paramref name="index"/>: a letter A–G and at most one # or b.</summary>
    private static bool TryNote(string text, int index, out string step, out int alter, out int end)
    {
        (step, alter, end) = ("", 0, index);
        if (index >= text.Length || text[index] is < 'A' or > 'G') return false;
        step = text[index].ToString();
        end = index + 1;
        if (end < text.Length && text[end] is '#' or 'b')
        {
            alter = text[end] == '#' ? 1 : -1;
            end++;
        }

        return true;
    }
}
