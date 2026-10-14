using ScoreStudio.Editing;
using ScoreStudio.Models;

namespace ScoreStudio.Recognition;

/// <summary>Joins the scores recognised from several pictures into one (docs/OMR_IMPORT.md, step 7).</summary>
public static class ScoreMerge
{
    /// <summary>Overlaps shorter than this are not removed: one equal measure may be a real repeat.</summary>
    public const int MinimumOverlap = 2;

    /// <summary>
    /// Appends the measures of every later score to the first, part by part. Missing parts get whole-measure rests and
    /// extra parts are dropped; each joined score starts a new system; measures repeated from the end of the score so
    /// far (two or more, in every part) are left out. Returns the joined score and notes for the user.
    /// </summary>
    /// <param name="newPage">Each joined score starts a new page too (to line up with its picture).</param>
    public static (ScoreProject Score, List<string> Notes) Join(IReadOnlyList<ScoreProject> scores, bool newPage = false)
    {
        if (scores.Count == 0) throw new ArgumentException("이어 붙일 악보가 없습니다.", nameof(scores));
        var result = scores[0];
        var notes = new List<string>();
        for (var s = 1; s < scores.Count; s++)
        {
            var next = scores[s];
            var label = $"{s + 1}번째 결과";
            if (next.Parts.Count != result.Parts.Count)
            {
                notes.Add(next.Parts.Count > result.Parts.Count
                    ? $"{label}의 파트가 {next.Parts.Count}개라서 앞 결과에 맞춰 {next.Parts.Count - result.Parts.Count}개를 버렸습니다."
                    : $"{label}의 파트가 {next.Parts.Count}개라서 없는 파트는 쉼표로 채웠습니다.");
            }

            var nextCount = next.Parts.Select(p => p.Measures.Count).DefaultIfEmpty(0).Max();
            var skip = Overlap(result, next);
            if (skip > 0) notes.Add($"{label}의 처음 {skip}마디는 앞 결과의 끝과 같아서 지웠습니다.");

            for (var p = 0; p < result.Parts.Count; p++)
            {
                var part = result.Parts[p];
                for (var m = skip; m < nextCount; m++)
                {
                    var measure = p < next.Parts.Count && m < next.Parts[p].Measures.Count
                        ? next.Parts[p].Measures[m]
                        : RestMeasure(next.Parts.Where(q => m < q.Measures.Count).Select(q => q.Measures[m]).FirstOrDefault());
                    if (m == skip) (measure.SystemBreak, measure.PageBreak) = (true, measure.PageBreak || newPage);
                    part.Measures.Add(measure);
                }
            }
        }

        foreach (var part in result.Parts)
        {
            var first = part.Measures.FirstOrDefault()?.Number ?? 1;
            for (var m = 0; m < part.Measures.Count; m++) part.Measures[m].Number = first + m;
        }

        return (result, notes);
    }

    /// <summary>The most measures (at least <see cref="MinimumOverlap"/>) the end of <paramref name="score"/> and the start of <paramref name="next"/> share.</summary>
    public static int Overlap(ScoreProject score, ScoreProject next)
    {
        if (score.Parts.Count == 0 || score.Parts.Count != next.Parts.Count) return 0;
        var have = score.Parts.Min(p => p.Measures.Count);
        var come = next.Parts.Min(p => p.Measures.Count);
        for (var k = Math.Min(have, come - 1); k >= MinimumOverlap; k--)
        {
            var same = score.Parts.Select((part, p) => (part, p)).All(x =>
                Enumerable.Range(0, k).All(i => Signature(x.part.Measures[have - k + i]) == Signature(next.Parts[x.p].Measures[i])));
            if (same) return k;
        }

        return 0;
    }

    /// <summary>What a measure holds, for comparing: time signature and every event's kind, pitch, place and length.</summary>
    private static string Signature(ScoreMeasure measure) =>
        $"{measure.Numerator}/{measure.Denominator}|" + string.Join(";", measure.Events
            .Select(e => $"{e.Staff},{e.Voice},{e.Kind},{(e.Kind == "Note" ? e.MidiPitch : 0)},{e.PositionNumerator}/{e.PositionDenominator},{e.DurationNumerator}/{e.DurationDenominator}")
            .Order(StringComparer.Ordinal));

    private static ScoreMeasure RestMeasure(ScoreMeasure? like)
    {
        var measure = new ScoreMeasure { Numerator = like?.Numerator ?? 4, Denominator = like?.Denominator ?? 4, KeyFifths = like?.KeyFifths ?? 0 };
        var length = ScoreEditor.MeasureLength(measure);
        measure.Events.Add(new ScoreEvent
        {
            Kind = "Rest", DurationNumerator = (int)length.Numerator, DurationDenominator = (int)length.Denominator,
        });
        return measure;
    }
}
