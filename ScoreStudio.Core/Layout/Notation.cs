using System.Globalization;
using System.Text.RegularExpressions;
using ScoreStudio.Import;
using ScoreStudio.Models;

namespace ScoreStudio.Layout;

/// <summary>Written note value, from double whole (breve) to 64th. Rest and flag code points follow this order.</summary>
public enum NoteType
{
    Breve,
    Whole,
    Half,
    Quarter,
    Eighth,
    Sixteenth,
    ThirtySecond,
    SixtyFourth,
}

/// <summary>How a duration is written: a note type, augmentation dots and whether it sits in a tuplet.</summary>
/// <param name="IsExact">False when the duration could not be written exactly and the nearest shorter value is shown.</param>
public readonly record struct NoteValue(NoteType Type, int Dots, bool IsTuplet, bool IsExact = true);

/// <summary>A pitch as written: step 0–6 (C to B), alteration in semitones, octave (C4 is middle C).</summary>
public readonly record struct SpelledPitch(int Step, int Alter, int Octave)
{
    /// <summary>Counts diatonic steps from C0, ignoring accidentals; used for vertical positions.</summary>
    public int Diatonic => Octave * 7 + Step;
}

/// <summary>Music-theory calculations for drawing notes. No layout decisions are made here.</summary>
public static partial class Notation
{
    private static readonly Rational[] TypeLengths =
    {
        new(8, 1), new(4, 1), new(2, 1), new(1, 1), new(1, 2), new(1, 4), new(1, 8), new(1, 16),
    };

    /// <summary>Tuplet ratios (actual notes in the time of normal notes) tried when a duration has no plain spelling.</summary>
    private static readonly (int Actual, int Normal)[] TupletRatios = { (3, 2), (5, 4), (6, 4), (7, 4), (2, 3), (4, 3), (9, 8) };

    private static readonly int[] SharpOrder = { 3, 0, 4, 1, 5, 2, 6 }; // F C G D A E B
    private static readonly int[] FlatOrder = { 6, 2, 5, 1, 4, 0, 3 };  // B E A D G C F

    /// <summary>Staff positions (0 = bottom line, 8 = top line) of the key signature accidentals in treble clef.</summary>
    private static readonly int[] TrebleSharpPositions = { 8, 5, 9, 6, 3, 7, 4 };
    private static readonly int[] TrebleFlatPositions = { 4, 7, 3, 6, 2, 5, 1 };

    private static readonly string[] SharpNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    private static readonly string[] FlatNames = { "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B" };

    public static Rational Length(NoteType type) => TypeLengths[(int)type];

    /// <summary>
    /// The written value of a duration inside tuplets whose ratios multiply to <paramref name="tupletRatio"/> (actual to
    /// normal notes; 1 outside tuplets): the plain value it stands for, else as <see cref="ValueOf(Rational)"/>.
    /// </summary>
    public static NoteValue ValueOf(Rational duration, Rational tupletRatio)
    {
        if (tupletRatio != new Rational(1, 1) && TryPlain(new Rational(duration.Numerator * tupletRatio.Numerator, duration.Denominator * tupletRatio.Denominator), out var written))
            return written with { IsTuplet = true };
        return ValueOf(duration);
    }

    /// <summary>Finds the written value for a duration in quarter notes: plain or dotted first, then common tuplets.</summary>
    public static NoteValue ValueOf(Rational duration)
    {
        if (TryPlain(duration, out var plain))
            return plain;

        foreach (var (actual, normal) in TupletRatios)
        {
            if (TryPlain(new Rational(duration.Numerator * actual, duration.Denominator * normal), out var inTuplet))
                return inTuplet with { IsTuplet = true };
        }

        // Nothing fits: show the longest value that is not longer than the duration.
        for (var t = NoteType.Breve; t <= NoteType.SixtyFourth; t++)
        {
            if (!(Length(t) > duration))
                return new NoteValue(t, 0, false, IsExact: false);
        }

        return new NoteValue(NoteType.SixtyFourth, 0, false, IsExact: false);
    }

    private static bool TryPlain(Rational duration, out NoteValue value)
    {
        for (var t = NoteType.Breve; t <= NoteType.SixtyFourth; t++)
        {
            var length = Length(t);
            var dotted = length;
            var add = length;
            for (var dots = 0; dots <= 2; dots++)
            {
                if (dots > 0)
                {
                    add = new Rational(add.Numerator, add.Denominator * 2);
                    dotted += add;
                }

                if (dotted == duration)
                {
                    value = new NoteValue(t, dots, false);
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    [GeneratedRegex(@"^([A-G])(#{1,3}|b{1,3})?(-?\d+)$")]
    private static partial Regex SpellingPattern();

    /// <summary>
    /// The written pitch of a note: its stored spelling (e.g. "Eb5") when present, otherwise the MIDI pitch spelled
    /// with sharps in sharp keys and flats in flat keys.
    /// </summary>
    public static SpelledPitch Spell(ScoreEvent note, int keyFifths)
    {
        if (note.NotationSpelling is { } spelling && SpellingPattern().Match(spelling) is { Success: true } match)
        {
            var accidentals = match.Groups[2].Value;
            var alter = accidentals.Length == 0 ? 0 : accidentals[0] == '#' ? accidentals.Length : -accidentals.Length;
            return new SpelledPitch("CDEFGAB".IndexOf(match.Groups[1].Value[0]), alter,
                int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
        }

        var name = (keyFifths < 0 ? FlatNames : SharpNames)[note.MidiPitch % 12];
        return new SpelledPitch(
            "CDEFGAB".IndexOf(name[0]),
            name.Length == 1 ? 0 : name[1] == '#' ? 1 : -1,
            note.MidiPitch / 12 - 1);
    }

    /// <summary>Diatonic number of the bottom staff line. Octave clefs use sounding pitch, as MusicXML does.</summary>
    public static int BottomLineDiatonic(ClefType clef) => clef switch
    {
        ClefType.Treble => 4 * 7 + 2,    // E4
        ClefType.Treble8vb => 3 * 7 + 2, // E3
        ClefType.Bass => 2 * 7 + 4,      // G2
        ClefType.Alto => 3 * 7 + 3,      // F3
        ClefType.Tenor => 3 * 7 + 1,     // D3
        _ => throw new ArgumentOutOfRangeException(nameof(clef), clef, null),
    };

    /// <summary>Vertical staff position in half spaces: 0 is the bottom line, 8 the top line, odd numbers are spaces.</summary>
    public static int StaffPosition(SpelledPitch pitch, ClefType clef) => pitch.Diatonic - BottomLineDiatonic(clef);

    /// <summary>The alteration the key signature gives to a step (C = 0 … B = 6).</summary>
    public static int KeyAlter(int keyFifths, int step)
    {
        var count = Math.Min(Math.Abs(keyFifths), 7);
        var order = keyFifths > 0 ? SharpOrder : FlatOrder;
        return Array.IndexOf(order, step) is var index && index >= 0 && index < count ? Math.Sign(keyFifths) : 0;
    }

    /// <summary>Staff positions of the key signature accidentals for a clef, in the order they are written.</summary>
    public static IReadOnlyList<int> KeySignaturePositions(int keyFifths, ClefType clef)
    {
        var count = Math.Min(Math.Abs(keyFifths), 7);
        var treble = keyFifths > 0 ? TrebleSharpPositions : TrebleFlatPositions;
        var shift = clef switch
        {
            ClefType.Bass => -2,
            ClefType.Alto => -1,
            ClefType.Tenor => 1,
            _ => 0,
        };

        // Tenor-clef sharps would rise above the staff, so the high ones are written an octave lower. In the other clefs
        // the third sharp sits in the space above the staff (G sharp in the treble clef).
        return treble.Take(count).Select(p => clef == ClefType.Tenor && p + shift > 8 ? p + shift - 7 : p + shift).ToList();
    }
}
