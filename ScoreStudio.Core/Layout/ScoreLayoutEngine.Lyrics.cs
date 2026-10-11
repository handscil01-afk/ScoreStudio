using ScoreStudio.Models;

namespace ScoreStudio.Layout;

public static partial class ScoreLayoutEngine
{
    /// <summary>Lyric font size in mm when the source gives none: a little over two staff spaces.</summary>
    private const double DefaultLyricSizeInSpaces = 2.2;

    /// <summary>Height of a hyphen above the baseline, in lyric font sizes: the middle of Hangul syllables.</summary>
    private const double HyphenHeight = 0.35;

    /// <summary>Distance between verse lines, in lyric font sizes.</summary>
    private const double VerseLineSpacing = 1.4;

    private static double LyricSize(ScoreProject project, LayoutOptions options) =>
        project.Engraving?.LyricFontSize ?? DefaultLyricSizeInSpaces * options.StaffSpace;

    /// <summary>
    /// Draws the lyrics of one system: every verse of every staff on its own line, syllables centred under their notes
    /// (a syllable with an extender starts at its note), hyphens between the syllables of a word and extender lines
    /// under held notes. A verse line sits where the source puts most of its syllables in this system; without source
    /// positions it goes under the staff, clear of the lowest note or stem.
    /// </summary>
    private static void DrawLyrics(List<DrawItem> items, List<PlacedChord> placed, double[] staffTops, double systemRight, double size, double sp)
    {
        foreach (var staff in placed.Where(p => p.Chord.Lyrics.Count > 0).GroupBy(p => p.Slot))
        {
            var staffTop = staffTops[staff.Key];
            var lowest = placed.Where(p => p.Slot == staff.Key).Max(p => p.Bottom);
            var firstLine = Math.Max(staffTop + 7 * sp, lowest + 0.9 * size);

            foreach (var verse in staff.SelectMany(p => p.Chord.Lyrics.Select(l => (Placed: p, Lyric: l))).GroupBy(s => s.Lyric.Verse))
            {
                var syllables = verse.OrderBy(s => s.Placed.Measure).ThenBy(s => s.Placed.Chord.Start).ToList();
                var sourceY = syllables.Where(s => s.Lyric.Y is not null).GroupBy(s => s.Lyric.Y!.Value)
                    .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => (double?)g.Key).FirstOrDefault();
                var baseline = sourceY is { } y ? staffTop - y * sp : firstLine + (verse.Key - 1) * VerseLineSpacing * size;

                var spans = new List<(double Left, double Right)>();
                foreach (var (chord, lyric) in syllables)
                {
                    var width = TextMetrics.Width(lyric.Text, size);
                    var head = Smufl.NoteheadWidth(chord.Chord.Value.Type) * sp;
                    var centre = chord.X + head / 2;
                    var left = lyric.Extend ? chord.X : centre - width / 2;
                    items.Add(lyric.Extend
                        ? new TextItem(lyric.Text, left, baseline, size, TextAnchor.Start)
                        : new TextItem(lyric.Text, centre, baseline, size, TextAnchor.Middle));
                    spans.Add((left, left + width));
                }

                for (var i = 0; i < syllables.Count; i++)
                {
                    var (chord, lyric) = syllables[i];
                    var right = spans[i].Right;
                    var hasNext = i + 1 < syllables.Count;

                    if (lyric.Syllabic is LyricSyllabic.Begin or LyricSyllabic.Middle)
                    {
                        // Centred in the gap to the next syllable, or just after this one when the word goes on in the next system.
                        var hyphen = 0.4 * size;
                        var centre = hasNext ? (right + spans[i + 1].Left) / 2 : right + 0.6 * size;
                        if ((!hasNext || spans[i + 1].Left - right > hyphen) && centre + hyphen / 2 <= systemRight)
                            items.Add(new LineItem(centre - hyphen / 2, baseline - HyphenHeight * size, centre + hyphen / 2, baseline - HyphenHeight * size, 0.06 * size));
                    }

                    if (lyric.Extend)
                    {
                        // Under every later note of the same voice up to the next syllable (or the end of the system).
                        var held = placed.Where(p => p.Slot == staff.Key && p.Chord.Voice == chord.Chord.Voice && IsAfter(p, chord)
                                && (!hasNext || IsAfter(syllables[i + 1].Placed, p)))
                            .ToList();
                        if (held.Count == 0) continue;
                        var last = held.MaxBy(p => (p.Measure, p.Chord.Start))!;
                        var end = last.X + Smufl.NoteheadWidth(last.Chord.Value.Type) * sp;
                        if (end > right + 0.3 * size)
                            items.Add(new LineItem(right + 0.15 * size, baseline, end, baseline, 0.06 * size));
                    }
                }
            }
        }
    }

    /// <summary>Whether <paramref name="a"/> comes after <paramref name="b"/> in time.</summary>
    private static bool IsAfter(PlacedChord a, PlacedChord b) =>
        a.Measure > b.Measure || (a.Measure == b.Measure && a.Chord.Start > b.Chord.Start);
}
