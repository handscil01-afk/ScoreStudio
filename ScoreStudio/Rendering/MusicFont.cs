using System.IO;
using System.Windows;
using System.Windows.Media;
using ScoreStudio.Layout;

namespace ScoreStudio.Rendering;

/// <summary>
/// Loads the Bravura music font from the Fonts folder next to the executable. The font is not installed system-wide.
/// </summary>
public static class MusicFont
{
    public const string FileName = "Bravura.otf";

    private static readonly Lazy<Typeface?> LoadedTypeface = new(Load);

    public static string FontDirectory => Path.Combine(AppContext.BaseDirectory, "Fonts");

    /// <summary>The Bravura typeface, or null when the font file is missing or could not be read.</summary>
    public static Typeface? Typeface => LoadedTypeface.Value;

    private static Typeface? Load()
    {
        if (!File.Exists(Path.Combine(FontDirectory, FileName)))
            return null;

        var family = new FontFamily(new Uri(FontDirectory + Path.DirectorySeparatorChar), "./#Bravura");
        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        // WPF silently falls back to another font when a family cannot be loaded, so check that we really got Bravura.
        return typeface.TryGetGlyphTypeface(out var glyphs)
            && glyphs.FamilyNames.Values.Contains("Bravura")
            && glyphs.CharacterToGlyphMap.ContainsKey(Smufl.GClef)
            ? typeface
            : null;
    }
}
