using System.Text;
using ScoreStudio.Import;
using ScoreStudio.Models;

namespace ScoreStudio.Layout;

public static partial class ScoreLayoutEngine
{
    /// <summary>A chord symbol placed at its beat, before its height is known.</summary>
    private sealed record PlacedSymbol(int Slot, int Measure, double X, ScoreChordSymbol Symbol);

    /// <summary>
    /// X of a point in time inside a measure: at a note onset where there is one, otherwise in proportion between the
    /// onsets (or the start and end of the measure) around it.
    /// </summary>
    private static double XAtTime(Rational time, List<(Rational Time, double X)> anchors)
    {
        if (time <= anchors[0].Time) return anchors[0].X;
        for (var i = 1; i < anchors.Count; i++)
        {
            if (time > anchors[i].Time) continue;
            var (t0, x0) = anchors[i - 1];
            var (t1, x1) = anchors[i];
            var span = (t1 - t0).ToDouble();
            return span <= 0 ? x1 : x0 + (x1 - x0) * (time - t0).ToDouble() / span;
        }

        return anchors[^1].X;
    }

    /// <summary>
    /// Draws the chord symbols of one system above their staves, starting at their beat. A staff's symbols share one
    /// baseline: the height the source gives most of them in this system, or else 2.5 spaces above the staff and clear
    /// of the highest note or stem.
    /// </summary>
    /// <returns>The top of the symbols on each staff that has any, so brackets above can clear them.</returns>
    private static Dictionary<int, double> DrawChordSymbols(
        List<DrawItem> items, List<PlacedSymbol> symbols, List<PlacedChord> placed, double[] staffTops, double size, double sp)
    {
        var tops = new Dictionary<int, double>();
        foreach (var staff in symbols.GroupBy(s => s.Slot))
        {
            var staffTop = staffTops[staff.Key];
            var sourceY = staff.Where(s => s.Symbol.Y is not null).GroupBy(s => s.Symbol.Y!.Value)
                .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).Select(g => (double?)g.Key).FirstOrDefault();
            var highest = placed.Where(p => p.Slot == staff.Key).Select(p => p.Top).DefaultIfEmpty(staffTop).Min();
            var baseline = sourceY is { } y ? staffTop - y * sp : Math.Min(staffTop - 2.5 * sp, highest - 0.6 * sp);

            foreach (var symbol in staff)
                items.Add(new TextItem(ChordDisplay(symbol.Symbol.Text), symbol.X, baseline, size, TextAnchor.Start));
            tops[staff.Key] = baseline - 0.75 * size;
        }

        return tops;
    }

    /// <summary>The printed symbol with real flat and sharp signs after the root and the bass ("Bb/D" → "B♭/D").</summary>
    public static string ChordDisplay(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            result.Append(text[i]);
            var isNote = text[i] is >= 'A' and <= 'G' && (i == 0 || text[i - 1] == '/');
            while (isNote && i + 1 < text.Length && text[i + 1] is 'b' or '#')
            {
                i++;
                result.Append(text[i] == 'b' ? '♭' : '♯');
            }
        }

        return result.ToString();
    }
}
