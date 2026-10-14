using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ScoreStudio.Models;

namespace ScoreStudio.Recognition;

/// <summary>A page picture Audiveris read, in pixels at its 300 dpi.</summary>
public sealed record SheetPicture(string Name, byte[] Png, int Width, int Height)
{
    /// <summary>Audiveris works at 300 dpi: its millimetres and staff spaces assume it.</summary>
    public const double Dpi = 300;

    public double WidthMm => Width * 25.4 / Dpi;
    public double HeightMm => Height * 25.4 / Dpi;
}

/// <summary>
/// The original page pictures of a recognition, and the score's page fitted to them so the two can be compared
/// (docs/OMR_COMPARE.md).
/// </summary>
public static partial class SourcePictures
{
    [GeneratedRegex(@"^sheet#(\d+)/BINARY\.png$")]
    private static partial Regex SheetEntry();

    /// <summary>The page pictures in an Audiveris book file (".omr", a zip), by sheet number.</summary>
    public static List<SheetPicture> FromBook(string book, string label)
    {
        using var zip = ZipFile.OpenRead(book);
        var sheets = new List<(int Number, SheetPicture Picture)>();
        foreach (var entry in zip.Entries)
        {
            var match = SheetEntry().Match(entry.FullName);
            if (!match.Success || entry.Length > 64 * 1024 * 1024) continue;
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var png = memory.ToArray();
            if (Size(png) is not { } size) continue;
            var number = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            sheets.Add((number, new SheetPicture($"{label} {number}쪽", png, size.Width, size.Height)));
        }

        return sheets.OrderBy(s => s.Number).Select(s => s.Picture).ToList();
    }

    /// <summary>A PNG's size from its header, or null when it is not a PNG.</summary>
    public static (int Width, int Height)? Size(byte[] png)
    {
        if (png.Length < 24 || png[0] != 137 || png[1] != 80 || png[12] != (byte)'I' || png[13] != (byte)'H') return null;
        var (width, height) = (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        return width > 0 && height > 0 ? (width, height) : null;
    }

    /// <summary>The left and right system margins of the first system in a MusicXML file, in tenths, or null.</summary>
    public static (double Left, double Right)? SystemMargins(string musicXml)
    {
        try
        {
            XDocument document;
            if (string.Equals(Path.GetExtension(musicXml), ".mxl", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(musicXml);
                var entry = zip.Entries.FirstOrDefault(e => !e.FullName.StartsWith("META-INF", StringComparison.OrdinalIgnoreCase)
                    && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
                if (entry is null) return null;
                using var stream = entry.Open();
                document = XDocument.Load(stream);
            }
            else
            {
                document = XDocument.Load(musicXml);
            }

            var margins = document.Descendants("system-margins").FirstOrDefault();
            return double.TryParse(margins?.Element("left-margin")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var left)
                && double.TryParse(margins?.Element("right-margin")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var right)
                ? (left, right)
                : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Fits the score's page to the first picture and keeps the pictures in the score, page by page. Audiveris writes
    /// the picture's size plus a margin of its own as the page and places systems from the margin; with the page made
    /// the picture's size, no top margin and the side margins its systems have, the score's systems land where the
    /// picture's are. Part names are hidden: Audiveris guesses them ("Piano"), the picture shows none.
    /// </summary>
    public static void Attach(ScoreProject score, IReadOnlyList<SheetPicture> sheets, (double Left, double Right)? systemMargins)
    {
        if (sheets.Count == 0) return;
        var engraving = score.Engraving ??= new ScoreEngraving();
        var mmPerTenth = engraving.StaffSpace / 10;
        engraving.PageWidth = sheets[0].WidthMm;
        engraving.PageHeight = sheets[0].HeightMm;
        engraving.MarginTop = 0;
        engraving.MarginBottom = 0;
        if (systemMargins is { } margins)
        {
            engraving.MarginLeft = Math.Clamp(margins.Left * mmPerTenth, 0, 100);
            engraving.MarginRight = Math.Clamp(margins.Right * mmPerTenth, 0, 100);
        }

        foreach (var part in score.Parts) part.ShowName = false;
        score.SourcePages = sheets.Select((sheet, i) => new ScoreSourcePage
        {
            Page = i, Png = sheet.Png, Left = 0, Top = 0, Width = sheet.WidthMm, Height = sheet.HeightMm, Name = sheet.Name,
        }).ToList();
    }
}
