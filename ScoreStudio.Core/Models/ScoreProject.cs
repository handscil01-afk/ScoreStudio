using System.Text.Json.Serialization;

namespace ScoreStudio.Models;

/// <summary>
/// Music-domain data should remain independent from screen coordinates.
/// This first model is intentionally small and will grow with tested features.
/// </summary>
public sealed class ScoreProject
{
    /// <summary>The project file schema version this build reads and writes.</summary>
    public const int CurrentSchemaVersion = 8;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Title { get; set; } = "새 악보";
    public string Composer { get; set; } = "";
    public string PageSize { get; set; } = "A4";
    public string Orientation { get; set; } = "Portrait";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    public string? ImportedFrom { get; set; }
    public List<ScorePart> Parts { get; set; } = new();

    /// <summary>Parts joined at the left of each system by a bracket or brace, and optionally by their barlines.</summary>
    public List<ScorePartGroup> PartGroups { get; set; } = new();

    /// <summary>Staff size, margins and spacing taken from the source (e.g. Finale's page layout); null uses defaults.</summary>
    public ScoreEngraving? Engraving { get; set; }

    public static ScoreProject CreateNew() => new()
    {
        Title = "새 악보",
        Parts = new List<ScorePart>
        {
            new()
            {
                Name = "파트 1",
                Measures = Enumerable.Range(1, 4).Select(n => new ScoreMeasure { Number = n }).ToList()
            }
        }
    };
}

public sealed class ScorePart
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "파트 1";
    public string Instrument { get; set; } = "Unknown";

    /// <summary>Whether the name is printed left of the first system. Finale exports often hide it.</summary>
    public bool ShowName { get; set; } = true;

    /// <summary>Staves from top to bottom. A piano part has two; most parts have one.</summary>
    public List<ScoreStaff> Staves { get; set; } = new() { new() };

    public List<ScoreMeasure> Measures { get; set; } = new();
}

/// <summary>
/// Page formatting from the source file. Distances between staves and systems are in staff spaces, so they scale with
/// the staff size; the staff size and margins are in millimetres.
/// </summary>
public sealed class ScoreEngraving
{
    /// <summary>Distance between two staff lines, in millimetres.</summary>
    public double StaffSpace { get; set; } = 1.75;

    public double MarginLeft { get; set; } = 15;
    public double MarginRight { get; set; } = 15;
    public double MarginTop { get; set; } = 15;
    public double MarginBottom { get; set; } = 15;

    /// <summary>From the bottom line of one system to the top line of the next, unless a system says otherwise.</summary>
    public double SystemDistance { get; set; } = 10;

    /// <summary>From the top margin to the top line of a page's first system, unless a system says otherwise.</summary>
    public double TopSystemDistance { get; set; }

    /// <summary>From the bottom line of one staff to the top line of the next within a system, unless a system says otherwise.</summary>
    public double StaffDistance { get; set; } = 8;
}

/// <summary>A staff's distance from the staff above it in the system that starts at this measure.</summary>
public sealed class ScoreStaffDistance
{
    public int Staff { get; set; }

    /// <summary>In staff spaces, from the bottom line of the staff above to this staff's top line.</summary>
    public double Distance { get; set; }
}

/// <summary>Consecutive parts grouped on the score, e.g. the choir parts of an anthem.</summary>
public sealed class ScorePartGroup
{
    /// <summary>Index of the first part in the group.</summary>
    public int FirstPart { get; set; }

    /// <summary>Index of the last part in the group (inclusive).</summary>
    public int LastPart { get; set; }

    public PartGroupSymbol Symbol { get; set; } = PartGroupSymbol.Bracket;

    /// <summary>Barlines run through all staves of the group instead of stopping at each part.</summary>
    public bool JoinBarlines { get; set; }
}

public enum PartGroupSymbol
{
    None,
    Bracket,
    Brace,
    Line,
    Square,
}

public sealed class ScoreStaff
{
    public ClefType Clef { get; set; } = ClefType.Treble;
}

/// <summary>Stored in project files by name, so the numeric values may change freely.</summary>
public enum ClefType
{
    Treble,
    Bass,
    Alto,
    Tenor,
    /// <summary>Treble clef sounding an octave lower, used for tenor voices and guitar.</summary>
    Treble8vb,
}

public sealed class ScoreMeasure
{
    public int Number { get; set; } = 1;
    public int Numerator { get; set; } = 4;
    public int Denominator { get; set; } = 4;

    /// <summary>Key signature as a position on the circle of fifths: −7 (seven flats) to 7 (seven sharps).</summary>
    public int KeyFifths { get; set; }

    /// <summary>A start-repeat sign at the beginning of this measure.</summary>
    public bool RepeatStart { get; set; }

    /// <summary>An end-repeat sign at the end of this measure.</summary>
    public bool RepeatEnd { get; set; }

    /// <summary>How many times the repeated section is played in total, when <see cref="RepeatEnd"/> is set.</summary>
    public int RepeatTimes { get; set; } = 2;

    /// <summary>A volta (1st/2nd ending bracket) that starts at this measure, or null.</summary>
    public ScoreVolta? Volta { get; set; }

    public List<ScoreEvent> Events { get; set; } = new();

    /// <summary>Chord symbols written above the part, such as "Bbmaj7/D".</summary>
    public List<ScoreChordSymbol> ChordSymbols { get; set; } = new();

    /// <summary>Clef changes in this measure. The clef at the start of the piece is <see cref="ScoreStaff.Clef"/>.</summary>
    public List<ScoreClefChange> ClefChanges { get; set; } = new();

    /// <summary>Staves of the part (indices from 0) that are hidden in this measure, as in Finale's hidden empty staves.</summary>
    public List<int> HiddenStaves { get; set; } = new();

    /// <summary>When a system starts here: distance from the previous system, in staff spaces (source layout).</summary>
    public double? SystemDistance { get; set; }

    /// <summary>When a page starts here: distance of the first system from the top margin, in staff spaces.</summary>
    public double? TopSystemDistance { get; set; }

    /// <summary>When a system starts here: distances of this part's staves from the staff above (source layout).</summary>
    public List<ScoreStaffDistance> StaffDistances { get; set; } = new();

    /// <summary>A new system starts with this measure (a line break taken from the source file).</summary>
    public bool SystemBreak { get; set; }

    /// <summary>A new page starts with this measure.</summary>
    public bool PageBreak { get; set; }
}

public sealed class ScoreClefChange
{
    public int Staff { get; set; }
    public int PositionNumerator { get; set; }
    public int PositionDenominator { get; set; } = 1;
    public ClefType Clef { get; set; } = ClefType.Treble;
}

/// <summary>A 1st/2nd ending bracket.</summary>
public sealed class ScoreVolta
{
    /// <summary>The passes it applies to, as written in the file, e.g. "1" or "1, 2".</summary>
    public string Numbers { get; set; } = "1";

    /// <summary>Text printed in the bracket, e.g. "1."; empty means "<see cref="Numbers"/>." is printed.</summary>
    public string Text { get; set; } = "";

    /// <summary>How many measures the bracket spans, starting with the measure that holds it.</summary>
    public int MeasureCount { get; set; } = 1;

    /// <summary>True when the bracket has no closing hook at its end (usually the last ending).</summary>
    public bool OpenEnd { get; set; }
}

public sealed class ScoreChordSymbol
{
    public int PositionNumerator { get; set; }
    public int PositionDenominator { get; set; } = 1;
    public int Staff { get; set; }

    /// <summary>Root note letter, A to G.</summary>
    public string RootStep { get; set; } = "C";

    /// <summary>−1 for flat, 1 for sharp.</summary>
    public int RootAlter { get; set; }

    /// <summary>Chord quality in MusicXML terms, e.g. "major", "minor-seventh", "suspended-fourth".</summary>
    public string Kind { get; set; } = "major";

    public string? BassStep { get; set; }
    public int BassAlter { get; set; }

    /// <summary>The symbol as printed, e.g. "Bbmaj7/D".</summary>
    public string Text { get; set; } = "C";
}

/// <summary>
/// Duration is stored as a rational number of quarter-note units where practical.
/// MIDI pitch is used as a neutral pitch representation; notation spelling is added separately.
/// </summary>
public sealed class ScoreEvent
{
    public string Kind { get; set; } = "Note";
    public int MidiPitch { get; set; } = 60;
    public int DurationNumerator { get; set; } = 1;
    public int DurationDenominator { get; set; } = 1;
    public int PositionNumerator { get; set; } = 0;
    public int PositionDenominator { get; set; } = 1;
    public string? NotationSpelling { get; set; }

    /// <summary>Lyrics under this note, one entry per verse.</summary>
    public List<ScoreLyric> Lyrics { get; set; } = new();

    /// <summary>
    /// Versions 1–3 stored a single lyric here. Loading moves it into <see cref="Lyrics"/>; it is never written.
    /// </summary>
    [JsonPropertyName("Lyric")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyLyric { get; set; }

    /// <summary>A tie from this note to the next note of the same pitch.</summary>
    public bool TieToNext { get; set; }

    /// <summary>
    /// Octave-line shift: 1 for 8va and 2 for 15ma (written lower than sounding), −1 for 8vb and −2 for 15mb.
    /// <see cref="MidiPitch"/> and <see cref="NotationSpelling"/> always hold the sounding pitch.
    /// </summary>
    public int OctaveShift { get; set; }

    /// <summary>Index into the part's staves, starting at 0.</summary>
    public int Staff { get; set; }

    /// <summary>Voice number within the part, starting at 1.</summary>
    public int Voice { get; set; } = 1;

    /// <summary>
    /// Beams at this note, one entry per beam level: index 0 is the 8th-note beam, index 1 the 16th-note beam, and so on.
    /// Empty when the note is not beamed (it keeps its flag). In a chord, the first note carries the chord's beams.
    /// </summary>
    public List<BeamType> Beams { get; set; } = new();
}

public sealed class ScoreLyric
{
    public int Verse { get; set; } = 1;
    public string Text { get; set; } = "";
    public LyricSyllabic Syllabic { get; set; } = LyricSyllabic.Single;

    /// <summary>An extender line follows the syllable (it is held over several notes).</summary>
    public bool Extend { get; set; }
}

/// <summary>Where a syllable sits in its word; a hyphen follows Begin and Middle.</summary>
/// <summary>What a beam does at one note, as in MusicXML's &lt;beam&gt;.</summary>
public enum BeamType
{
    Begin,
    Continue,
    End,

    /// <summary>A short beam pointing right from this note only (e.g. a 16th followed by a dotted 8th).</summary>
    ForwardHook,

    /// <summary>A short beam pointing left from this note only (e.g. the 16th after a dotted 8th).</summary>
    BackwardHook,
}

public enum LyricSyllabic
{
    Single,
    Begin,
    Middle,
    End,
}
