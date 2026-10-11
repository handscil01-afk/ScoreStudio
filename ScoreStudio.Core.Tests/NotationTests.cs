using ScoreStudio.Import;
using ScoreStudio.Layout;
using ScoreStudio.Models;

namespace ScoreStudio.Core.Tests;

public sealed class NotationTests
{
    [Theory]
    [InlineData(4, 1, NoteType.Whole, 0, false)]
    [InlineData(6, 1, NoteType.Whole, 1, false)]
    [InlineData(3, 1, NoteType.Half, 1, false)]
    [InlineData(1, 1, NoteType.Quarter, 0, false)]
    [InlineData(3, 2, NoteType.Quarter, 1, false)]
    [InlineData(7, 4, NoteType.Quarter, 2, false)]
    [InlineData(1, 2, NoteType.Eighth, 0, false)]
    [InlineData(1, 4, NoteType.Sixteenth, 0, false)]
    [InlineData(8, 1, NoteType.Breve, 0, false)]
    [InlineData(1, 3, NoteType.Eighth, 0, true)]      // triplet eighth
    [InlineData(2, 3, NoteType.Quarter, 0, true)]     // triplet quarter
    [InlineData(1, 5, NoteType.Sixteenth, 0, true)]   // quintuplet sixteenth
    public void ValueOf_FindsPlainDottedAndTupletValues(int numerator, int denominator, NoteType type, int dots, bool tuplet)
    {
        Assert.Equal(new NoteValue(type, dots, tuplet), Notation.ValueOf(new Rational(numerator, denominator)));
    }

    [Fact]
    public void ValueOf_DurationsWithoutAWrittenForm_ShowTheNearestShorterValue()
    {
        // A quarter tied to a sixteenth (5/4) is one stored event but has no single written value.
        var value = Notation.ValueOf(new Rational(5, 4));
        Assert.Equal((NoteType.Quarter, false), (value.Type, value.IsExact));
    }

    [Theory]
    [InlineData("C4", ClefType.Treble, -2)]
    [InlineData("E4", ClefType.Treble, 0)]
    [InlineData("F5", ClefType.Treble, 8)]
    [InlineData("A5", ClefType.Treble, 10)]
    [InlineData("G2", ClefType.Bass, 0)]
    [InlineData("C4", ClefType.Bass, 10)]
    [InlineData("C4", ClefType.Alto, 4)]
    [InlineData("C4", ClefType.Tenor, 6)]
    [InlineData("C4", ClefType.Treble8vb, 5)]
    [InlineData("Bb4", ClefType.Treble, 4)]
    public void StaffPosition_FollowsTheClef(string spelling, ClefType clef, int position)
    {
        var pitch = Notation.Spell(new ScoreEvent { NotationSpelling = spelling }, 0);
        Assert.Equal(position, Notation.StaffPosition(pitch, clef));
    }

    [Fact]
    public void Spell_UsesTheStoredSpelling_OrTheKeyForMidiOnlyNotes()
    {
        Assert.Equal(new SpelledPitch(2, -1, 5), Notation.Spell(new ScoreEvent { NotationSpelling = "Eb5", MidiPitch = 75 }, 0));
        Assert.Equal(new SpelledPitch(3, 2, 4), Notation.Spell(new ScoreEvent { NotationSpelling = "F##4", MidiPitch = 67 }, 0));
        Assert.Equal(new SpelledPitch(1, -1, 4), Notation.Spell(new ScoreEvent { MidiPitch = 61 }, -2)); // Db4 in a flat key
        Assert.Equal(new SpelledPitch(0, 1, 4), Notation.Spell(new ScoreEvent { MidiPitch = 61 }, 2));   // C#4 in a sharp key
    }

    [Fact]
    public void KeyAlter_AppliesSharpsAndFlatsInCircleOfFifthsOrder()
    {
        Assert.Equal(new[] { 1, 0, 0, 1, 0, 0, 0 }, Enumerable.Range(0, 7).Select(step => Notation.KeyAlter(2, step)));   // D major: F#, C#
        Assert.Equal(new[] { 0, 0, -1, 0, 0, 0, -1 }, Enumerable.Range(0, 7).Select(step => Notation.KeyAlter(-2, step))); // Bb major: Bb, Eb
        Assert.All(Enumerable.Range(0, 7), step => Assert.Equal(0, Notation.KeyAlter(0, step)));
    }

    [Theory]
    [InlineData(2, ClefType.Treble, new[] { 8, 5 })]
    [InlineData(2, ClefType.Bass, new[] { 6, 3 })]
    [InlineData(2, ClefType.Alto, new[] { 7, 4 })]
    [InlineData(2, ClefType.Tenor, new[] { 2, 6 })]
    [InlineData(7, ClefType.Treble, new[] { 8, 5, 9, 6, 3, 7, 4 })]
    [InlineData(7, ClefType.Treble8vb, new[] { 8, 5, 9, 6, 3, 7, 4 })]
    [InlineData(7, ClefType.Bass, new[] { 6, 3, 7, 4, 1, 5, 2 })]
    [InlineData(7, ClefType.Alto, new[] { 7, 4, 8, 5, 2, 6, 3 })]
    [InlineData(7, ClefType.Tenor, new[] { 2, 6, 3, 7, 4, 8, 5 })]
    [InlineData(-7, ClefType.Treble, new[] { 4, 7, 3, 6, 2, 5, 1 })]
    [InlineData(-7, ClefType.Tenor, new[] { 5, 8, 4, 7, 3, 6, 2 })]
    [InlineData(-3, ClefType.Treble, new[] { 4, 7, 3 })]
    [InlineData(-3, ClefType.Bass, new[] { 2, 5, 1 })]
    [InlineData(0, ClefType.Treble, new int[0])]
    public void KeySignaturePositions_MatchStandardEngraving(int fifths, ClefType clef, int[] positions)
    {
        Assert.Equal(positions, Notation.KeySignaturePositions(fifths, clef));
    }
}
