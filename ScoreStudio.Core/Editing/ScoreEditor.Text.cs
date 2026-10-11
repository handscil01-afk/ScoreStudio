using ScoreStudio.Import;
using ScoreStudio.Models;

namespace ScoreStudio.Editing;

/// <summary>Lyric and chord symbol entry (docs/SPRINT4G_LYRICS_CHORDS.md).</summary>
public sealed partial class ScoreEditor
{
    private const int MaxSyllableLength = 40;

    /// <summary>The note a lyric goes to: the selected chord, else the chord at the cursor; null for a rest or nothing.</summary>
    private EditPosition? LyricTarget()
    {
        var at = Selection ?? Cursor;
        return ChordAt(at) is { } chord && chord[0].Kind == "Note" ? at : null;
    }

    /// <summary>Selects the chord at the cursor when nothing is selected (before typing lyrics or chord symbols there).</summary>
    public void SelectAtCursor()
    {
        if (Selection is null && ChordAt(Cursor) is not null) Select(Cursor);
    }

    /// <summary>
    /// Puts a syllable of a verse under the selected note (or the note at the cursor), or removes the verse's syllable
    /// when <paramref name="text"/> is empty. The syllable's place in its word follows from whether the previous syllable
    /// ends with a hyphen, and the next syllable is fixed to follow from this one.
    /// </summary>
    public EditResult SetLyric(string text, int verse, bool hyphen = false, bool extend = false)
    {
        text = text.Trim();
        if (verse is < 1 or > 99) return EditResult.Refused("절 번호가 올바르지 않습니다.");
        if (text.Length > MaxSyllableLength || text.Any(char.IsControl)) return EditResult.Refused($"가사 한 음절은 {MaxSyllableLength}자까지 쓸 수 있습니다.");
        if (LyricTarget() is not { } at) return EditResult.Refused("가사를 붙일 음표를 먼저 선택하세요. 쉼표에는 가사를 붙일 수 없습니다.");

        return Change(() =>
        {
            var chord = ChordAt(at)!;
            foreach (var e in chord) e.Lyrics.RemoveAll(l => l.Verse == verse);
            var (previous, next) = (NeighbourSyllable(at, verse, -1), NeighbourSyllable(at, verse, +1));
            if (text.Length > 0)
            {
                var continues = previous?.Syllabic is LyricSyllabic.Begin or LyricSyllabic.Middle;
                chord[0].Lyrics.Add(new ScoreLyric { Verse = verse, Text = text, Syllabic = Syllabic(continues, hyphen), Extend = extend });
                chord[0].Lyrics.Sort((a, b) => a.Verse.CompareTo(b.Verse));
            }

            // The next syllable continues the word if this one ends with a hyphen; when this one is removed, if the one
            // before it does.
            if (next is not null)
            {
                var nextHyphen = next.Syllabic is LyricSyllabic.Begin or LyricSyllabic.Middle;
                var joined = text.Length > 0 ? hyphen : previous?.Syllabic is LyricSyllabic.Begin or LyricSyllabic.Middle;
                next.Syllabic = Syllabic(joined, nextHyphen);
            }

            return null;
        }, () => text.Length == 0
            ? $"{verse}절 가사를 지웠습니다."
            : $"{verse}절 가사 '{text}{(hyphen ? "-" : extend ? "_" : "")}'을(를) 넣었습니다.");
    }

    /// <summary>Puts a syllable under the target note (as <see cref="SetLyric"/>) and selects the next note a syllable goes to.</summary>
    public EditResult EnterLyric(string text, int verse, bool hyphen = false, bool extend = false)
    {
        var result = SetLyric(text, verse, hyphen, extend);
        if (!result.Done) return result;
        if (NextLyricNote(Selection ?? Cursor) is not { } next) return EditResult.Ok($"{result.Message} 뒤에 가사를 붙일 음표가 없습니다.");
        Select(next);
        return EditResult.Ok($"{result.Message} 다음 음표: {next.Measure + 1}마디 {DescribeBeat(next.Position)}");
    }

    /// <summary>
    /// Enters what was typed in the lyric box: each finished syllable (<see cref="LyricTyping.Take"/>) goes to the next
    /// note in turn. Returns the last result (null when nothing was finished) and the unfinished rest of the text.
    /// </summary>
    public (EditResult? Result, string Unfinished) TypeLyrics(string typed, int verse)
    {
        var (finished, rest) = LyricTyping.Take(typed);
        EditResult? result = null;
        foreach (var syllable in finished)
        {
            result = EnterLyric(syllable.Text, verse, syllable.Hyphen, syllable.Extend);
            if (!result.Done) return (result, rest);
        }

        return (result, rest);
    }

    private static LyricSyllabic Syllabic(bool continues, bool hyphen) => (continues, hyphen) switch
    {
        (false, false) => LyricSyllabic.Single,
        (false, true) => LyricSyllabic.Begin,
        (true, true) => LyricSyllabic.Middle,
        _ => LyricSyllabic.End,
    };

    /// <summary>Notes of a voice through the whole part, in time order, with where they are.</summary>
    private List<(EditPosition At, List<ScoreEvent> Chord)> VoiceThroughout(EditPosition at)
    {
        var part = Project.Parts[at.Part];
        return Enumerable.Range(0, part.Measures.Count)
            .SelectMany(m => VoiceChords(part.Measures[m], at.Staff, at.Voice).Select(c => (at with { Measure = m, Position = Start(c[0]) }, c)))
            .ToList();
    }

    /// <summary>The verse's syllable on the nearest earlier (<paramref name="direction"/> −1) or later (+1) note that has one.</summary>
    private ScoreLyric? NeighbourSyllable(EditPosition at, int verse, int direction)
    {
        var chords = VoiceThroughout(at);
        var index = chords.FindIndex(c => c.At == at);
        for (var i = index + direction; i >= 0 && i < chords.Count; i += direction)
        {
            if (chords[i].Chord.SelectMany(e => e.Lyrics).FirstOrDefault(l => l.Verse == verse) is { } lyric) return lyric;
        }

        return null;
    }

    /// <summary>The next note of the voice a syllable goes to: not a rest, and not a note tied over from the one before.</summary>
    private EditPosition? NextLyricNote(EditPosition at)
    {
        var chords = VoiceThroughout(at);
        var index = chords.FindIndex(c => c.At == at);
        for (var i = index + 1; i < chords.Count; i++)
        {
            var chord = chords[i].Chord;
            if (chord[0].Kind != "Note") continue;
            var previous = chords[i - 1].Chord;
            var tiedOver = chord.All(n => previous.Any(p => p.Kind == "Note" && p.TieToNext && p.MidiPitch == n.MidiPitch));
            if (!tiedOver) return chords[i].At;
        }

        return null;
    }

    /// <summary>
    /// Writes a chord symbol (e.g. "Bbmaj7/D") above the staff at the selected chord or rest (or the cursor), replacing
    /// one already there; empty text removes it. Then goes on to the next chord or rest of the voice.
    /// </summary>
    public EditResult EnterChordSymbol(string typed)
    {
        var at = Selection ?? Cursor;
        if (!IsValid(at)) return EditResult.Refused("코드 이름을 넣을 자리를 먼저 누르세요.");
        ScoreChordSymbol? symbol = null;
        if (typed.Trim().Length > 0 && (symbol = ChordSymbolText.TryParse(typed, out var error)) is null) return EditResult.Refused(error);

        var result = Change(() =>
        {
            var measure = MeasureAt(at);
            var removed = measure.ChordSymbols.RemoveAll(c => c.Staff == at.Staff && new Rational(c.PositionNumerator, c.PositionDenominator) == at.Position);
            if (symbol is null)
                return removed == 0 ? "그 자리에 지울 코드 이름이 없습니다." : null;

            (symbol.Staff, symbol.PositionNumerator, symbol.PositionDenominator) = (at.Staff, (int)at.Position.Numerator, (int)at.Position.Denominator);
            measure.ChordSymbols.Add(symbol);
            measure.ChordSymbols.Sort((a, b) => new Rational(a.PositionNumerator, a.PositionDenominator).CompareTo(new Rational(b.PositionNumerator, b.PositionDenominator)));
            return null;
        }, () => symbol is null ? "코드 이름을 지웠습니다." : $"코드 이름 '{symbol.Text}'을(를) 넣었습니다.");
        if (!result.Done || symbol is null) return result;

        // On to the next chord or rest of the voice; in an empty measure, to the next measure.
        var chords = VoiceThroughout(at);
        if (chords.FirstOrDefault(c => c.At.Measure > at.Measure || (c.At.Measure == at.Measure && c.At.Position > at.Position)) is { Chord: not null } next)
        {
            Select(next.At);
        }
        else if (at.Measure + 1 < Project.Parts[at.Part].Measures.Count)
        {
            PlaceCursor(at.Part, at.Staff, at.Measure + 1, Rational.Zero);
        }

        return result;
    }
}
