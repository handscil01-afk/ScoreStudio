using ScoreStudio.Import;
using ScoreStudio.Models;

namespace ScoreStudio.Editing;

/// <summary>Tuplet entry (docs/SPRINT4F_TUPLETS.md, entry).</summary>
public sealed partial class ScoreEditor
{
    /// <summary>
    /// The tuplet the next entry starts (its number of notes), or null. Set by <see cref="StartTuplet"/>; the entry that
    /// starts the tuplet clears it.
    /// </summary>
    public int? PendingTuplet { get; private set; }

    /// <summary>How many normal notes a tuplet of <paramref name="actual"/> notes stands for, or null when not supported.</summary>
    public static int? TupletNormal(int actual) => actual switch
    {
        2 => 3,
        3 => 2,
        4 => 3,
        5 or 6 or 7 => 4,
        9 => 8,
        _ => null,
    };

    /// <summary>A tuplet's name in Korean, e.g. "셋잇단음표".</summary>
    public static string TupletName(int actual) => actual switch
    {
        2 => "둘잇단음표",
        3 => "셋잇단음표",
        4 => "넷잇단음표",
        5 => "다섯잇단음표",
        6 => "여섯잇단음표",
        7 => "일곱잇단음표",
        9 => "아홉잇단음표",
        _ => $"{actual}잇단음표",
    };

    /// <summary>
    /// Makes the next note or rest start a tuplet of <paramref name="actual"/> notes of the input length (Ctrl+3 for a
    /// triplet, before its first note, as in Finale's Speedy Entry). The same number again cancels it.
    /// </summary>
    public EditResult StartTuplet(int actual)
    {
        if (TupletNormal(actual) is not { } normal) return EditResult.Refused("지원하지 않는 잇단음표입니다. 2~7개나 9개를 고르세요.");
        if (PendingTuplet == actual)
        {
            PendingTuplet = null;
            return EditResult.Ok($"{TupletName(actual)} 입력을 취소했습니다.");
        }

        PendingTuplet = actual;
        return EditResult.Ok($"{TupletName(actual)}: 다음에 넣는 음표부터 {DescribeValue(InputType, InputDots, rest: false)} {normal}개 시간에 {actual}개를 넣습니다.");
    }

    /// <summary>Forgets a tuplet started with <see cref="StartTuplet"/> before its first note.</summary>
    public void CancelTuplet() => PendingTuplet = null;

    private static Rational SpanStart(ScoreTuplet t) => new(t.PositionNumerator, t.PositionDenominator);

    private static Rational SpanEnd(ScoreTuplet t) => SpanStart(t) + new Rational(t.DurationNumerator, t.DurationDenominator);

    /// <summary>The tuplets of a voice around a time (a tuplet holds the times from its start up to its end).</summary>
    private static IEnumerable<ScoreTuplet> TupletsAt(ScoreMeasure measure, int staff, int voice, Rational time) =>
        measure.Tuplets.Where(t => t.Staff == staff && t.Voice == voice && SpanStart(t) <= time && time < SpanEnd(t));

    /// <summary>The innermost (shortest) tuplet around a time, or null.</summary>
    private static ScoreTuplet? InnermostTuplet(ScoreMeasure measure, int staff, int voice, Rational time) =>
        TupletsAt(measure, staff, voice, time).OrderBy(t => new Rational(t.DurationNumerator, t.DurationDenominator)).FirstOrDefault();

    /// <summary>
    /// How much the tuplets around a time shorten notes: the product of their actual-to-normal ratios (3/2 in a triplet).
    /// A written value lasts its length divided by this.
    /// </summary>
    private static Rational TupletRatioAt(ScoreMeasure measure, int staff, int voice, Rational time) =>
        TupletsAt(measure, staff, voice, time).Aggregate(new Rational(1, 1), (ratio, t) => ratio * new Rational(t.Actual, t.Normal));

    /// <summary>
    /// Splits real time starting at <paramref name="start"/> into written values (longest first, at most one dot), in the
    /// tuplets around that time.
    /// </summary>
    private static List<Rational> Pieces(ScoreMeasure measure, int staff, int voice, Rational start, Rational length)
    {
        var ratio = TupletRatioAt(measure, staff, voice, start);
        return Pieces(length * ratio).Select(p => p / ratio).ToList();
    }

    /// <summary>
    /// Tuplets of a voice that are now rests only (their notes were deleted) go: their time becomes plain rests, or rests
    /// of the tuplet around them. Inner tuplets go first.
    /// </summary>
    private static void DissolveRestTuplets(ScoreMeasure measure, int staff, int voice)
    {
        foreach (var span in measure.Tuplets.Where(t => t.Staff == staff && t.Voice == voice)
                     .OrderBy(t => new Rational(t.DurationNumerator, t.DurationDenominator)).ToList())
        {
            var events = measure.Events.Where(e => e.Staff == staff && e.Voice == voice && Start(e) >= SpanStart(span) && Start(e) < SpanEnd(span)).ToList();
            if (events.Any(e => e.Kind == "Note")) continue;
            foreach (var e in events) measure.Events.Remove(e);
            measure.Tuplets.Remove(span);
            AddRests(measure, new EditPosition(0, staff, voice, 0, Rational.Zero), SpanStart(span), SpanEnd(span));
        }
    }

    /// <summary>
    /// Starts the pending tuplet at <paramref name="at"/> (nested when inside another) with the note or rest as its
    /// first, or enters the note or rest inside the innermost tuplet there. Returns why not, or null.
    /// </summary>
    private string? EnterInTuplet(EditPosition at, ScoreEvent? note, Rational input, out string tupletName)
    {
        var measure = MeasureAt(at);
        var around = InnermostTuplet(measure, at.Staff, at.Voice, at.Position);
        Rational length;
        if (PendingTuplet is null && around is { } span)
        {
            tupletName = TupletName(span.Actual);
            length = input / TupletRatioAt(measure, at.Staff, at.Voice, at.Position);
            if (at.Position + length > SpanEnd(span))
                return $"{tupletName}에 남은 길이보다 긴 {DescribeValue(InputType, InputDots, note is null)}는 넣을 수 없습니다.";
        }
        else
        {
            var actual = PendingTuplet!.Value;
            var normal = TupletNormal(actual)!.Value;
            tupletName = TupletName(actual);
            // Inside another tuplet the new one is nested: its notes are shortened by both.
            var spanLength = input * new Rational(normal, 1) / TupletRatioAt(measure, at.Staff, at.Voice, at.Position);
            var room = (around is null ? MeasureLength(measure) : SpanEnd(around)) - at.Position;
            if (spanLength > room)
                return $"{(around is null ? "이 마디" : TupletName(around.Actual))}에 남은 길이에는 {DescribeValue(InputType, InputDots, rest: false)} {normal}개 시간의 {tupletName}를 넣을 수 없습니다.";

            // The tuplet's time is cleared and filled with rests of its own values; the note then goes over the first.
            ClearSpan(measure, at.Staff, at.Voice, at.Position, at.Position + spanLength);
            measure.Tuplets.Add(new ScoreTuplet
            {
                Staff = at.Staff, Voice = at.Voice,
                PositionNumerator = (int)at.Position.Numerator, PositionDenominator = (int)at.Position.Denominator,
                DurationNumerator = (int)spanLength.Numerator, DurationDenominator = (int)spanLength.Denominator,
                Actual = actual, Normal = normal,
            });
            AddRests(measure, at, at.Position, at.Position + spanLength);
            length = input / TupletRatioAt(measure, at.Staff, at.Voice, at.Position);
            PendingTuplet = null;
        }

        ClearSpan(measure, at.Staff, at.Voice, at.Position, at.Position + length);
        measure.Events.Add(note is null ? NewRest(at, at.Position, length) : WithTime(note, at.Position, length));
        AutoBeam(measure, at.Staff, at.Voice);

        var after = at.Position + length;
        if (after < MeasureLength(measure))
        {
            Cursor = at with { Position = after };
        }
        else
        {
            if (at.Measure == Project.Parts[at.Part].Measures.Count - 1) AddMeasureAfter(at.Measure);
            Cursor = at with { Measure = at.Measure + 1, Position = Rational.Zero };
        }

        Selection = at;
        SelectionIsEntry = true;
        return null;
    }
}
