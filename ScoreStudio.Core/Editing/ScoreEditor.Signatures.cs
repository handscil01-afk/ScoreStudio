using ScoreStudio.Import;
using ScoreStudio.Layout;
using ScoreStudio.Models;

namespace ScoreStudio.Editing;

/// <summary>Key, time signature and clef changes (docs/SPRINT4_EDITING.md, signatures).</summary>
public sealed partial class ScoreEditor
{
    private static readonly int[] TimeDenominators = { 1, 2, 4, 8, 16, 32 };

    /// <summary>
    /// Sets the key from the cursor's measure up to the next key change. Every part moves by the same number of steps on
    /// the circle of fifths, so transposing instruments keep their interval. Notes keep their pitches.
    /// </summary>
    public EditResult SetKey(int fifths)
    {
        if (fifths is < -7 or > 7) return EditResult.Refused("조표는 ♭7개부터 ♯7개까지입니다.");
        if (!CursorIsValid()) return EditResult.Refused("조표를 바꿀 마디를 먼저 누르세요.");

        var first = Cursor.Measure;
        var measures = Project.Parts[Cursor.Part].Measures;
        var old = measures[first].KeyFifths;
        if (old == fifths) return EditResult.Refused($"이미 {KeyName(fifths)}입니다.");
        var last = first;
        while (last + 1 < measures.Count && measures[last + 1].KeyFifths == old) last++;

        return Change(() =>
        {
            foreach (var part in Project.Parts)
            {
                for (var m = first; m <= last && m < part.Measures.Count; m++)
                {
                    var key = part.Measures[m].KeyFifths + fifths - old;
                    if (key is < -7 or > 7) return $"'{part.Name}' 파트(이조 악기)의 조표가 ♯·♭ 7개를 넘게 되어 바꿀 수 없습니다.";
                    part.Measures[m].KeyFifths = key;
                }
            }

            return null;
        }, () => $"{Span(first, last)}의 조표를 바꿨습니다: {KeyName(fifths)}");
    }

    /// <summary>
    /// Sets the time signature from the cursor's measure up to the next time change, in every part. Voices of rests only
    /// become a whole-measure rest; a longer measure gets rests after a full voice; a shorter one loses the rests past its
    /// end, and is refused when notes would be cut.
    /// </summary>
    public EditResult SetTime(int numerator, int denominator)
    {
        if (numerator is < 1 or > 32 || !TimeDenominators.Contains(denominator)) return EditResult.Refused("지원하지 않는 박자표입니다.");
        if (!CursorIsValid()) return EditResult.Refused("박자표를 바꿀 마디를 먼저 누르세요.");

        var first = Cursor.Measure;
        var measures = Project.Parts[Cursor.Part].Measures;
        var old = (measures[first].Numerator, measures[first].Denominator);
        if (old == (numerator, denominator)) return EditResult.Refused($"이미 {numerator}/{denominator}박자입니다.");
        var last = first;
        while (last + 1 < measures.Count && (measures[last + 1].Numerator, measures[last + 1].Denominator) == old) last++;

        return Change(() =>
        {
            foreach (var part in Project.Parts)
            {
                for (var m = first; m <= last && m < part.Measures.Count; m++)
                {
                    if (Retime(part.Measures[m], numerator, denominator) is { } why)
                        return $"{m + 1}마디{(Project.Parts.Count > 1 ? $"('{part.Name}' 파트)" : "")}: {why}";
                }
            }

            if (!CursorIsValid()) Cursor = Cursor with { Position = Rational.Zero };
            if (Selection is { } selected && ChordAt(selected) is null) ClearSelection();
            return null;
        }, () => $"{Span(first, last)}의 박자표를 바꿨습니다: {numerator}/{denominator}");
    }

    /// <summary>Gives one measure a new time signature; returns why not, or null.</summary>
    private static string? Retime(ScoreMeasure measure, int numerator, int denominator)
    {
        var oldLength = MeasureLength(measure);
        var oldUnit = BeatUnit(measure);
        (measure.Numerator, measure.Denominator) = (numerator, denominator);
        var length = MeasureLength(measure);
        var rebeam = BeatUnit(measure) != oldUnit;

        if (measure.ChordSymbols.Any(c => new Rational(c.PositionNumerator, c.PositionDenominator) >= length))
            return "새 박자보다 뒤에 코드 이름이 있습니다.";
        if (measure.ClefChanges.Any(c => new Rational(c.PositionNumerator, c.PositionDenominator) >= length))
            return "새 박자보다 뒤에 음자리표 변경이 있습니다.";

        foreach (var (staff, voice) in measure.Events.Select(e => (e.Staff, e.Voice)).Distinct().ToList())
        {
            var events = measure.Events.Where(e => e.Staff == staff && e.Voice == voice).ToList();
            var end = events.Max(e => Start(e) + Duration(e));
            var changed = false;
            if (events.All(e => e.Kind == "Rest"))
            {
                // Rests only: one whole-measure rest, as Finale shows an empty measure.
                if (events.Count != 1 || Start(events[0]).Sign != 0 || Duration(events[0]) != length)
                {
                    foreach (var e in events) measure.Events.Remove(e);
                    measure.Events.Add(NewRest(new EditPosition(0, staff, voice, 0, Rational.Zero), Rational.Zero, length));
                    changed = true;
                }
            }
            else if (end > length)
            {
                if (events.Any(e => e.Kind == "Note" && Start(e) + Duration(e) > length))
                    return "새 박자보다 뒤에 음표가 있습니다. 뒤쪽 음표를 먼저 쉼표로 바꾸세요.";
                ClearSpan(measure, staff, voice, length, end);
                changed = true;
            }
            else if (length > oldLength && end == oldLength)
            {
                AddRests(measure, new EditPosition(0, staff, voice, 0, Rational.Zero), oldLength, length);
                changed = true;
            }

            if (changed || rebeam) AutoBeam(measure, staff, voice);
        }

        return null;
    }

    /// <summary>The beat that beams group by: a dotted quarter in 6/8, 9/8 and 12/8, else a quarter.</summary>
    private static Rational BeatUnit(ScoreMeasure measure) =>
        measure.Denominator == 8 && measure.Numerator % 3 == 0 ? new Rational(3, 2) : new Rational(1, 1);

    /// <summary>
    /// Puts a clef at the selection (or the cursor); at the very start of the piece it replaces the staff's first clef.
    /// Notes keep their pitches. A later change to the same clef, now redundant, is removed.
    /// </summary>
    public EditResult SetClef(ClefType clef)
    {
        var at = Selection ?? Cursor;
        if (!IsValid(at)) return EditResult.Refused("음자리표를 바꿀 자리를 먼저 누르세요.");
        var current = new ClefTimeline(Project).ClefAt(at.Part, at.Staff, at.Measure, at.Position);
        if (current == clef) return EditResult.Refused($"이미 {ClefName(clef)}입니다.");
        var atStart = at.Measure == 0 && at.Position.Sign == 0;

        return Change(() =>
        {
            var part = Project.Parts[at.Part];
            part.Measures[at.Measure].ClefChanges.RemoveAll(c => c.Staff == at.Staff && new Rational(c.PositionNumerator, c.PositionDenominator) == at.Position);
            if (atStart)
            {
                part.Staves[at.Staff].Clef = clef;
            }
            else if (new ClefTimeline(Project).ClefAt(at.Part, at.Staff, at.Measure, at.Position) != clef)
            {
                part.Measures[at.Measure].ClefChanges.Add(new ScoreClefChange
                {
                    Staff = at.Staff, PositionNumerator = (int)at.Position.Numerator, PositionDenominator = (int)at.Position.Denominator, Clef = clef,
                });
            }

            var next = Enumerable.Range(at.Measure, part.Measures.Count - at.Measure)
                .SelectMany(m => part.Measures[m].ClefChanges
                    .Where(c => c.Staff == at.Staff && (m > at.Measure || new Rational(c.PositionNumerator, c.PositionDenominator) > at.Position))
                    .OrderBy(c => new Rational(c.PositionNumerator, c.PositionDenominator))
                    .Select(c => (Measure: m, Change: c)))
                .FirstOrDefault();
            if (next.Change is { } redundant && redundant.Clef == clef) part.Measures[next.Measure].ClefChanges.Remove(redundant);
            return null;
        }, () => atStart
            ? $"첫 음자리표를 바꿨습니다: {ClefName(clef)}"
            : $"{at.Measure + 1}마디 {DescribeBeat(at.Position)}에 음자리표를 넣었습니다: {ClefName(clef)}");
    }

    private static string Span(int first, int last) => first == last ? $"{first + 1}마디" : $"{first + 1}~{last + 1}마디";

    /// <summary>A key in Korean with its relative minor and accidentals, e.g. "사장조/마단조(♯1)".</summary>
    public static string KeyName(int fifths)
    {
        string[] majors = { "내림다", "내림사", "내림라", "내림가", "내림마", "내림나", "바", "다", "사", "라", "가", "마", "나", "올림바", "올림다" };
        string[] minors = { "내림가", "내림마", "내림나", "바", "다", "사", "라", "가", "마", "나", "올림바", "올림다", "올림사", "올림라", "올림가" };
        var i = Math.Clamp(fifths, -7, 7) + 7;
        var accidentals = fifths == 0 ? "조표 없음" : fifths > 0 ? $"♯{fifths}" : $"♭{-fifths}";
        return $"{majors[i]}장조/{minors[i]}단조({accidentals})";
    }

    public static string ClefName(ClefType clef) => clef switch
    {
        ClefType.Treble => "높은음자리표",
        ClefType.Bass => "낮은음자리표",
        ClefType.Alto => "가온음자리표(알토)",
        ClefType.Tenor => "가온음자리표(테너)",
        ClefType.Treble8vb => "옥타브 아래 높은음자리표",
        _ => clef.ToString(),
    };
}
