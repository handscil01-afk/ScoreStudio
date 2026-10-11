namespace ScoreStudio.Editing;

/// <summary>A syllable finished in the lyric box: its text, and whether a hyphen or an extender line follows it.</summary>
public sealed record TypedSyllable(string Text, bool Hyphen, bool Extend);

/// <summary>
/// Splits what was typed in the lyric box into finished syllables (docs/SPRINT4G_LYRICS_CHORDS.md): a space ends a
/// syllable, "-" ends one with a hyphen after it, "_" ends one with an extender line. What follows the last of these
/// stays in the box.
/// </summary>
public static class LyricTyping
{
    public static (List<TypedSyllable> Finished, string Unfinished) Take(string typed)
    {
        var finished = new List<TypedSyllable>();
        var start = 0;
        for (var i = 0; i < typed.Length; i++)
        {
            if (typed[i] is not (' ' or '-' or '_' or '\t')) continue;
            var text = typed[start..i].Trim();
            if (text.Length > 0) finished.Add(new TypedSyllable(text, typed[i] == '-', typed[i] == '_'));
            start = i + 1;
        }

        return (finished, typed[start..]);
    }
}
