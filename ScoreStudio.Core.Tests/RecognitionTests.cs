using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using ScoreStudio.Import;
using ScoreStudio.Layout;
using ScoreStudio.Models;
using ScoreStudio.Recognition;

namespace ScoreStudio.Core.Tests;

/// <summary>Music recognition with Audiveris (docs/OMR_IMPORT.md).</summary>
public sealed class RecognitionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ScoreStudioRecognitionTests", Guid.NewGuid().ToString("N"));

    public RecognitionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ---- Pictures --------------------------------------------------------------------------------------------------

    /// <summary>Two five-line staves with notes and some text, like a captured score.</summary>
    private static GrayImage Staves(int spacing, int thickness, int width = 600)
    {
        var height = 30 * spacing + 40;
        var image = new GrayImage(width, height);
        void Box(int left, int top, int w, int h)
        {
            for (var y = Math.Max(0, top); y < Math.Min(height, top + h); y++)
                for (var x = Math.Max(0, left); x < Math.Min(width, left + w); x++)
                    image[x, y] = 0;
        }

        foreach (var staffTop in new[] { 2 * spacing + 20, 14 * spacing + 20 })
        {
            for (var line = 0; line < 5; line++) Box(10, staffTop + line * spacing, width - 20, thickness);
            Box(10, staffTop, 2, 4 * spacing + thickness); // barlines
            Box(width - 12, staffTop, 2, 4 * spacing + thickness);
            var random = new Random(spacing * 31 + staffTop);
            for (var x = 40; x < width - 40; x += 3 * spacing)
            {
                var step = random.Next(-2, 11);
                Box(x, staffTop + step * spacing / 2 - spacing / 2, spacing + spacing / 3, spacing); // notehead
                Box(x + spacing, staffTop + step * spacing / 2 - 3 * spacing, 2, 3 * spacing); // stem
            }
        }

        Box(30, 2, 120, spacing); // a title-like block of text
        return image;
    }

    [Theory]
    [InlineData(8, 1)]
    [InlineData(12, 2)]
    [InlineData(20, 3)]
    public void StaffSpacing_IsMeasuredToAPixel(int spacing, int thickness)
    {
        var measured = StaffSpacing.Estimate(Staves(spacing, thickness));
        Assert.NotNull(measured);
        Assert.InRange(measured.Value, spacing - 1, spacing + 1);
    }

    [Fact]
    public void StaffSpacing_IsNullWithoutStaves()
    {
        Assert.Null(StaffSpacing.Estimate(new GrayImage(300, 200)));
        var text = new GrayImage(300, 200);
        for (var y = 50; y < 60; y++)
            for (var x = 20; x < 280; x += 3)
                text[x, y] = 0;
        Assert.Null(StaffSpacing.Estimate(text));
    }

    [Theory]
    [InlineData(8.0, 3)]
    [InlineData(10.2, 2)]
    [InlineData(4.0, 4)]
    [InlineData(20.0, 1)]
    [InlineData(null, 2)]
    public void EnlargingFactor_BringsTheSpacingToAboutTwentyPixels(double? spacing, int factor) =>
        Assert.Equal(factor, ImageScaling.Factor(spacing));

    [Fact]
    public void Enlarging_KeepsLinesInPlace_AtTwiceTheThickness()
    {
        var image = new GrayImage(20, 20);
        for (var x = 0; x < 20; x++) image[x, 5] = 0;
        var big = ImageScaling.Enlarge(image, 2);

        Assert.Equal((40, 40), (big.Width, big.Height));
        Assert.True(big[17, 10] < 128 && big[17, 11] < 128);
        Assert.True(big[17, 8] > 128 && big[17, 13] > 128);
        Assert.Same(image, ImageScaling.Enlarge(image, 1));
        Assert.Equal(8, StaffSpacing.Estimate(Staves(8, 1)) is { } s ? (int)Math.Round(s) : 0);
        Assert.InRange(StaffSpacing.Estimate(ImageScaling.Enlarge(Staves(8, 1), 3))!.Value, 23, 25);
    }

    // ---- PNG -------------------------------------------------------------------------------------------------------

    /// <summary>A PNG with the given color type and bit depth whose rows use each filter type in turn.</summary>
    private static byte[] Png(int width, int height, int colorType, int depth, Func<int, int, int[]> samples, byte[]? palette = null, byte[]? transparency = null)
    {
        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        var bits = channels * depth;
        var stride = (width * bits + 7) / 8;
        var bpp = Math.Max(1, bits / 8);
        var raw = new MemoryStream();
        var previous = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var line = new byte[stride];
            for (var x = 0; x < width; x++)
            {
                var values = samples(x, y);
                for (var c = 0; c < channels; c++)
                {
                    if (depth == 16)
                    {
                        line[(x * channels + c) * 2] = (byte)values[c];
                        line[(x * channels + c) * 2 + 1] = 0x55;
                    }
                    else if (depth == 8)
                    {
                        line[x * channels + c] = (byte)values[c];
                    }
                    else
                    {
                        var bit = (x * channels + c) * depth;
                        line[bit / 8] |= (byte)(values[c] << (8 - depth - bit % 8));
                    }
                }
            }

            var filter = (byte)(y % 5);
            raw.WriteByte(filter);
            for (var i = 0; i < stride; i++)
            {
                int left = i >= bpp ? line[i - bpp] : 0, up = previous[i], upLeft = i >= bpp ? previous[i - bpp] : 0;
                var p = left + up - upLeft;
                var predictor = filter switch
                {
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Math.Abs(p - left) <= Math.Abs(p - up) && Math.Abs(p - left) <= Math.Abs(p - upLeft) ? left : Math.Abs(p - up) <= Math.Abs(p - upLeft) ? up : upLeft,
                    _ => 0,
                };
                raw.WriteByte((byte)(line[i] - predictor));
            }

            previous = line;
        }

        var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string type, byte[] body)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
            output.Write(length);
            output.Write(System.Text.Encoding.ASCII.GetBytes(type));
            output.Write(body);
            output.Write(new byte[4]); // CRC is not checked
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        (header[8], header[9]) = ((byte)depth, (byte)colorType);
        Chunk("IHDR", header);
        if (palette is not null) Chunk("PLTE", palette);
        if (transparency is not null) Chunk("tRNS", transparency);
        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw.ToArray());
        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static GrayImage Decode(byte[] png) => PngCodec.Decode(new MemoryStream(png));

    [Fact]
    public void Png_AllColorTypesAndFilters_AreReadAsGray()
    {
        // RGB, 8 bits: gray levels by column, every filter type down the rows.
        var rgb = Decode(Png(7, 6, 2, 8, (x, y) => new[] { x * 40, x * 40, x * 40 }));
        Assert.All(Enumerable.Range(0, 6), y => Assert.Equal(new byte[] { 0, 40, 80, 120, 160, 200, 240 }, Enumerable.Range(0, 7).Select(x => rgb[x, y])));

        // Pure red is the luma of red.
        Assert.Equal(76, Decode(Png(2, 2, 2, 8, (_, _) => new[] { 255, 0, 0 }))[1, 1]);

        // Gray, 1 bit: black and white stripes.
        var bits = Decode(Png(9, 5, 0, 1, (x, _) => new[] { x % 2 }));
        Assert.Equal(new byte[] { 0, 255, 0, 255, 0, 255, 0, 255, 0 }, Enumerable.Range(0, 9).Select(x => bits[x, 4]));

        // Palette, 2 bits, with a transparent entry (shown as white).
        var palette = Decode(Png(5, 5, 3, 2, (x, _) => new[] { x % 3 }, new byte[] { 0, 0, 0, 255, 255, 255, 10, 10, 10 }, new byte[] { 255, 255, 0 }));
        Assert.Equal(new byte[] { 0, 255, 255, 0, 255 }, Enumerable.Range(0, 5).Select(x => palette[x, 2]));

        // Gray with alpha, and RGBA with 16 bits: half-transparent black over white is mid gray.
        Assert.InRange(Decode(Png(3, 3, 4, 8, (_, _) => new[] { 0, 128 }))[2, 2], 125, 129);
        Assert.InRange(Decode(Png(3, 3, 6, 16, (_, _) => new[] { 0, 0, 0, 128 }))[1, 1], 125, 129);
    }

    [Fact]
    public void Png_WrittenGray_IsReadBackTheSame_AndOthersAreRefusedInKorean()
    {
        var image = Staves(10, 1, 120);
        var path = Path.Combine(_dir, "staves.png");
        PngCodec.Write(image, path);
        Assert.Equal(image.Pixels, new PngCodec().Read(path).Pixels);

        var error = Assert.Throws<InvalidDataException>(() => PngCodec.Decode(new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 })));
        Assert.Contains("PNG", error.Message);
    }

    // ---- Audiveris -------------------------------------------------------------------------------------------------

    [Fact]
    public void Audiveris_IsLookedForWhereItsInstallersPutIt_ChosenPathFirst()
    {
        var environment = new Dictionary<string, string?>
        {
            ["ProgramFiles"] = @"C:\Program Files", ["LOCALAPPDATA"] = @"C:\Users\u\AppData\Local", ["USERPROFILE"] = @"C:\Users\u", ["PATH"] = "",
        };
        string? Env(string name) => environment.TryGetValue(name, out var v) ? v : null;
        var candidates = AudiverisLocator.Candidates(@"D:\Tools\Audiveris.exe", Env).ToList();
        Assert.Equal(@"D:\Tools\Audiveris.exe", candidates[0]);
        Assert.Contains(Path.Combine(@"C:\Program Files", "Audiveris", "Audiveris.exe"), candidates);
        Assert.Contains(Path.Combine(@"C:\Users\u\AppData\Local", "Programs", "Audiveris", "Audiveris.exe"), candidates);

        var installed = Path.Combine(@"C:\Program Files", "Audiveris", "Audiveris.exe");
        Assert.Equal(installed, AudiverisLocator.Find(@"D:\gone.exe", Env, p => p == installed));
        Assert.Null(AudiverisLocator.Find(null, Env, _ => false));
    }

    [Fact]
    public void Audiveris_IsRunInBatch_WithLanguagesAndOutputBeforeTheInputs()
    {
        Assert.Equal(
            new[] { "-batch", "-export", "-constant", "org.audiveris.omr.text.Language.defaultSpecification=kor+eng", "-output", "out", "--", "a.png", "b.pdf" },
            AudiverisRunner.Arguments(new[] { "a.png", "b.pdf" }, "out", "kor+eng"));
        Assert.DoesNotContain("-constant", AudiverisRunner.Arguments(new[] { "a.png" }, "out", null));

        foreach (var name in new[] { "01-song.mvt2.mxl", "01-song.mxl", "01-song.mvt10.mxl", "01-song.omr", "02-other.mxl", "01-songs.mxl" })
            File.WriteAllText(Path.Combine(_dir, name), "");
        Assert.Equal(new[] { "01-song.mxl", "01-song.mvt2.mxl", "01-song.mvt10.mxl" },
            AudiverisRunner.ScoresFor("input/01-song.png", _dir).Select(Path.GetFileName));
        Assert.Empty(AudiverisRunner.ScoresFor("x.png", Path.Combine(_dir, "none")));
    }

    /// <summary>
    /// A stand-in for Audiveris (a shell script): exports the sample score for every input except those named "*fail*",
    /// or sleeps when an input is named "*slow*".
    /// </summary>
    private string FakeAudiveris()
    {
        var sample = Path.Combine(AppContext.BaseDirectory, "samples", "musicxml", "beams.musicxml");
        var book = Book(Path.Combine(_dir, "book.omr"), Staves(19, 2, 2480), Staves(19, 2, 2480));
        var script = Path.Combine(_dir, "audiveris.sh");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            out=""; inputs=0
            while [ $# -gt 0 ]; do
              case "$1" in
                -output) out="$2"; shift 2;;
                --) shift; inputs=1;;
                *) if [ $inputs = 1 ]; then
                     name=$(basename "$1"); base="${name%.*}"
                     case "$name" in *slow*) sleep 30;; *fail*) ;; *) cp "{{sample}}" "$out/$base.xml"; cp "{{book}}" "$out/$base.omr";; esac
                     echo "INFO processed $name"
                   fi; shift;;
              esac
            done
            """.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    /// <summary>An Audiveris book file holding these pages' pictures (and other entries, as the real one has).</summary>
    private static string Book(string path, params GrayImage[] pages)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        zip.CreateEntry("book.xml");
        for (var i = pages.Length - 1; i >= 0; i--) // out of order on purpose
        {
            zip.CreateEntry($"sheet#{i + 1}/sheet#{i + 1}.xml");
            using var stream = zip.CreateEntry($"sheet#{i + 1}/BINARY.png").Open();
            PngCodec.Encode(pages[i], stream);
        }

        return path;
    }

    [Fact]
    public async Task Runner_CollectsTheScores_ReportsFailures_AndStopsWhenCancelledOrTooSlow()
    {
        if (OperatingSystem.IsWindows()) return; // the stand-in is a shell script
        var fake = FakeAudiveris();
        var log = new List<string>();
        var results = await AudiverisRunner.RunAsync(fake, new[] { "in/01-a.png", "in/02-fail.png" }, Path.Combine(_dir, "out"), "eng",
            TimeSpan.FromMinutes(1), new SyncProgress(log));
        Assert.Single(results[0].Scores);
        Assert.Empty(results[1].Scores);
        Assert.Contains(log, l => l.Contains("01-a.png"));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            AudiverisRunner.RunAsync(fake, new[] { "in/slow.png" }, Path.Combine(_dir, "out2"), null, TimeSpan.FromSeconds(1)));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AudiverisRunner.RunAsync(fake, new[] { "in/slow.png" }, Path.Combine(_dir, "out3"), null, TimeSpan.FromMinutes(1), null, cancel.Token));
    }

    private sealed class SyncProgress(List<string> lines) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (lines) lines.Add(value);
        }
    }

    [Fact]
    public async Task Job_EnlargesSmallPictures_RecognisesAll_AndJoinsTheResults()
    {
        if (OperatingSystem.IsWindows()) return;
        var small = Path.Combine(_dir, "small.png");
        PngCodec.Write(Staves(10, 1), small);
        var sharp = Path.Combine(_dir, "sharp.png");
        PngCodec.Write(Staves(20, 2), sharp);
        var broken = Path.Combine(_dir, "fail.png");
        PngCodec.Write(Staves(20, 2), broken);
        var work = Path.Combine(_dir, "work");
        var job = new RecognitionJob { Audiveris = FakeAudiveris(), Pictures = new PngCodec(), Languages = "kor+eng", WorkFolder = work };

        var log = new List<string>();
        var result = await job.RunAsync(new[] { small, sharp, broken }, new SyncProgress(log));

        var prepared = Directory.GetFiles(Path.Combine(work, "input")).Order().ToList();
        Assert.Equal(new[] { "01-small.png", "02-sharp.png", "03-fail.png" }, prepared.Select(Path.GetFileName));
        Assert.Equal(1200, PngCodec.Decode(File.OpenRead(prepared[0])).Width); // 600 × 2
        Assert.Equal(600, PngCodec.Decode(File.OpenRead(prepared[1])).Width);
        Assert.Contains(log, l => l.Contains("small.png") && l.Contains("2배"));

        var sample = MusicXmlImporter.ImportFile(Path.Combine(AppContext.BaseDirectory, "samples", "musicxml", "beams.musicxml")).Project;
        Assert.Equal(2 * sample.Parts[0].Measures.Count, result.Score.Parts[0].Measures.Count);
        Assert.Contains(result.Notes, n => n.Contains("fail.png"));

        // The pictures are kept, page by page, and each file's music starts a page.
        Assert.Equal(4, result.Score.SourcePages.Count); // two pages in each of the two books read
        Assert.Equal(new[] { 0, 1, 2, 3 }, result.Score.SourcePages.Select(p => p.Page));
        Assert.Equal("small.png 1쪽", result.Score.SourcePages[0].Name);
        Assert.Equal(2480 * 25.4 / 300, result.Score.Engraving!.PageWidth!.Value, 6);
        Assert.True(result.Score.Parts[0].Measures[sample.Parts[0].Measures.Count].PageBreak);
        Assert.All(result.Score.Parts, p => Assert.False(p.ShowName));
        Assert.Contains(result.Notes, n => n.Contains("틀린 곳"));
        Assert.DoesNotContain(result.Warnings, w => w.Message.Contains("용지 크기"));

        await Assert.ThrowsAsync<NotSupportedException>(() => job.RunAsync(new[] { Path.Combine(_dir, "song.docx") }));
    }

    [Fact]
    public void Files_AreJoinedInTheOrderTheirNamesNumberThem()
    {
        var names = new[] { "캡처 10.png", "캡처 2.png", "캡처 1.png", "Capture-002.jpg", "capture-1.jpg", "악보.pdf" };
        Assert.Equal(new[] { "capture-1.jpg", "Capture-002.jpg", "악보.pdf", "캡처 1.png", "캡처 2.png", "캡처 10.png" },
            names.Order(NaturalOrder.Instance));
        Assert.True(NaturalOrder.Instance.Compare("p9", "p10") < 0);
        Assert.Equal(0, NaturalOrder.Instance.Compare("a01", "a1"));
    }

    // ---- Joining ---------------------------------------------------------------------------------------------------

    /// <summary>A score of one or more parts whose measures hold one whole note each, at the given pitches.</summary>
    private static ScoreProject Score(int parts, params int[] pitches) => new()
    {
        Parts = Enumerable.Range(0, parts).Select(p => new ScorePart
        {
            Name = $"P{p + 1}",
            Measures = pitches.Select((pitch, i) => new ScoreMeasure
            {
                Number = i + 1, Events = { new ScoreEvent { Kind = "Note", MidiPitch = pitch, DurationNumerator = 4 } },
            }).ToList(),
        }).ToList(),
    };

    private static int[] Pitches(ScorePart part) => part.Measures.Select(m => m.Events.FirstOrDefault(e => e.Kind == "Note")?.MidiPitch ?? -1).ToArray();

    [Fact]
    public void Joining_AppendsMeasures_StartsANewSystem_AndNumbersOn()
    {
        var (score, notes) = ScoreMerge.Join(new[] { Score(2, 60, 62), Score(2, 64, 65, 67) });
        Assert.Equal(new[] { 60, 62, 64, 65, 67 }, Pitches(score.Parts[1]));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, score.Parts[0].Measures.Select(m => m.Number));
        Assert.True(score.Parts[0].Measures[2].SystemBreak);
        Assert.Empty(notes);
    }

    [Fact]
    public void Joining_FitsThePartsOfLaterResults_ToTheFirst()
    {
        var (fewer, fewerNotes) = ScoreMerge.Join(new[] { Score(2, 60), Score(1, 62) });
        Assert.Equal(new[] { 60, -1 }, Pitches(fewer.Parts[1])); // a whole-measure rest
        Assert.Equal("Rest", fewer.Parts[1].Measures[1].Events.Single().Kind);
        Assert.Contains(fewerNotes, n => n.Contains("쉼표로 채웠습니다"));

        var (more, moreNotes) = ScoreMerge.Join(new[] { Score(1, 60), Score(3, 62) });
        Assert.Single(more.Parts);
        Assert.Contains(moreNotes, n => n.Contains("2개를 버렸습니다"));
    }

    [Fact]
    public void Joining_DropsMeasuresRepeatedFromTheEnd_ButNotASingleOne()
    {
        // Captures of a scrolling video: the last line of one picture is the first of the next.
        var (score, notes) = ScoreMerge.Join(new[] { Score(1, 60, 62, 64, 65), Score(1, 64, 65, 67, 69) });
        Assert.Equal(new[] { 60, 62, 64, 65, 67, 69 }, Pitches(score.Parts[0]));
        Assert.Contains(notes, n => n.Contains("처음 2마디"));
        Assert.True(score.Parts[0].Measures[4].SystemBreak);

        // One equal measure may be a real repeat.
        var (repeat, _) = ScoreMerge.Join(new[] { Score(1, 60, 62), Score(1, 62, 64) });
        Assert.Equal(new[] { 60, 62, 62, 64 }, Pitches(repeat.Parts[0]));
    }

    // ---- Original page pictures -----------------------------------------------------------------------------------

    [Fact]
    public void PagePictures_AreReadFromTheBook_InSheetOrder()
    {
        var book = Book(Path.Combine(_dir, "song.omr"), Staves(10, 1, 300), Staves(12, 1, 200));
        var sheets = SourcePictures.FromBook(book, "노래.pdf");
        Assert.Equal(new[] { ("노래.pdf 1쪽", 300), ("노래.pdf 2쪽", 200) }, sheets.Select(s => (s.Name, s.Width)));
        Assert.Equal(300 * 25.4 / 300, sheets[0].WidthMm, 9);
        Assert.Equal((300, Staves(10, 1, 300).Height), SourcePictures.Size(sheets[0].Png));
        Assert.Null(SourcePictures.Size(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void SystemMargins_AreReadFromTheFirstSystem_OfAnMxlOrXmlFile()
    {
        const string xml = """
            <score-partwise><part-list><score-part id="P1"/></part-list><part id="P1">
              <measure number="1"><print><system-layout><system-margins><left-margin>80</left-margin><right-margin>53</right-margin></system-margins></system-layout></print></measure>
              <measure number="2"><print new-system="yes"><system-layout><system-margins><left-margin>10</left-margin><right-margin>5</right-margin></system-margins></system-layout></print></measure>
            </part></score-partwise>
            """;
        var plain = Path.Combine(_dir, "a.xml");
        File.WriteAllText(plain, xml);
        Assert.Equal((80.0, 53.0), SourcePictures.SystemMargins(plain));

        var compressed = Path.Combine(_dir, "a.mxl");
        using (var zip = ZipFile.Open(compressed, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("a.xml").Open());
            writer.Write(xml);
        }

        Assert.Equal((80.0, 53.0), SourcePictures.SystemMargins(compressed));
        File.WriteAllText(plain, "<score-partwise/>");
        Assert.Null(SourcePictures.SystemMargins(plain));
        File.WriteAllText(plain, "not xml");
        Assert.Null(SourcePictures.SystemMargins(plain));
    }

    [Fact]
    public void Attaching_FitsThePageToThePicture_AndKeepsThePictures()
    {
        var score = Score(1, 60, 62);
        score.Engraving = new ScoreEngraving { StaffSpace = 1.6, MarginTop = 13, MarginBottom = 13 };
        var picture = new SheetPicture("a.png 1쪽", new byte[] { 1 }, 2480, 3508);
        SourcePictures.Attach(score, new[] { picture }, (80, 50));

        var engraving = score.Engraving;
        Assert.Equal((209.97, 297.01), (Math.Round(engraving.PageWidth!.Value, 2), Math.Round(engraving.PageHeight!.Value, 2)));
        Assert.Equal((0.0, 0.0, 12.8, 8.0), (engraving.MarginTop, engraving.MarginBottom, Math.Round(engraving.MarginLeft, 6), Math.Round(engraving.MarginRight, 6)));
        Assert.Equal((209.97, 297.01), PageSizes.Of(score) is var size ? (Math.Round(size.Width, 2), Math.Round(size.Height, 2)) : default);
        var page = Assert.Single(score.SourcePages);
        Assert.Equal((0, 0.0, 0.0, "a.png 1쪽"), (page.Page, page.Left, page.Top, page.Name));
        Assert.False(score.Parts[0].ShowName);
    }

    [Fact]
    public void PagePictures_SurviveSaving_AndStayOutOfUndo()
    {
        var project = ScoreProject.CreateNew();
        project.Engraving = new ScoreEngraving { PageWidth = 219, PageHeight = 95 };
        var png = new MemoryStream();
        PngCodec.Encode(Staves(10, 1, 300), png);
        project.SourcePages.Add(new ScoreSourcePage { Page = 0, Png = png.ToArray(), Width = 219, Height = 95, Name = "캡처 1쪽" });

        var path = Path.Combine(_dir, "with-pictures.scoreproj");
        ScoreStudio.Services.ProjectFileService.Save(path, project);
        var reopened = ScoreStudio.Services.ProjectFileService.Load(path);
        Assert.Equal(png.ToArray(), reopened.SourcePages.Single().Png);
        Assert.Equal((219.0, 95.0), (reopened.Engraving!.PageWidth, reopened.Engraving.PageHeight));
        Assert.Equal((219.0, 95.0), PageSizes.Of(reopened));

        // Editing and undoing keeps the pictures; undo snapshots stay small.
        var editor = new ScoreStudio.Editing.ScoreEditor(reopened);
        Assert.True(editor.EnterNote('C').Done);
        Assert.True(editor.Undo().Done);
        Assert.Same(reopened.SourcePages[0].Png, editor.Project.SourcePages.Single().Png);
        Assert.True(editor.Redo().Done);
        Assert.Single(editor.Project.SourcePages);
        Assert.Empty(ScoreStudio.Services.ProjectFileService.FromJson(ScoreStudio.Services.ProjectFileService.ToJson(ScoreProject.CreateNew())).SourcePages);

        Assert.Empty(ScoreStudio.Services.ProjectFileService.Parse("""{ "SchemaVersion": 15, "Parts": [] }""").SourcePages);
        foreach (var bad in new[]
                 {
                     """{ "SchemaVersion": 16, "SourcePages": [ { "Page": -1, "Png": "AQ==", "Width": 10, "Height": 10 } ] }""",
                     """{ "SchemaVersion": 16, "SourcePages": [ { "Page": 0, "Png": "", "Width": 10, "Height": 10 } ] }""",
                     """{ "SchemaVersion": 16, "SourcePages": [ { "Page": 0, "Png": "AQ==", "Width": 0, "Height": 10 } ] }""",
                     """{ "SchemaVersion": 16, "SourcePages": [ null ] }""",
                     """{ "SchemaVersion": 16, "Engraving": { "PageWidth": 200 } }""",
                     """{ "SchemaVersion": 16, "Engraving": { "PageWidth": 5, "PageHeight": 5 } }""",
                 })
        {
            var error = Assert.Throws<ScoreStudio.Services.ProjectFileException>(() => ScoreStudio.Services.ProjectFileService.Parse(bad));
            Assert.Matches("원본 쪽 그림|페이지 크기", error.Message);
        }
    }

    [Fact]
    public void WorkNames_ArePlainAscii()
    {
        Assert.Equal("score", RecognitionJob.SafeName("악보 score!"));
        Assert.Equal("page", RecognitionJob.SafeName("좋아 좋아"));
    }

    // ---- OCR languages ---------------------------------------------------------------------------------------------

    [Fact]
    public void OcrLanguageFiles_AreLookedForWhereAudiverisKeepsThem()
    {
        var environment = new Dictionary<string, string?> { ["APPDATA"] = @"C:\Users\u\AppData\Roaming", ["HOME"] = "/home/u" };
        string? Env(string name) => environment.TryGetValue(name, out var v) ? v : null;
        Assert.Equal(Path.Combine(@"C:\Users\u\AppData\Roaming", "AudiverisLtd", "audiveris", "config", "tessdata"), OcrLanguages.TessdataFolder(true, false, Env, _ => false));
        Assert.Equal(Path.Combine("/home/u", ".config", "AudiverisLtd", "audiveris", "tessdata"), OcrLanguages.TessdataFolder(false, false, Env, _ => false));
        environment["XDG_CONFIG_HOME"] = "/xdg";
        Assert.Equal(Path.Combine("/xdg", "AudiverisLtd", "audiveris", "tessdata"), OcrLanguages.TessdataFolder(false, false, Env, _ => false));
        environment["TESSDATA_PREFIX"] = "/data/tess";
        Assert.Equal("/data/tess", OcrLanguages.TessdataFolder(false, false, Env, d => d == "/data/tess"));

        Assert.Equal(new[] { "kor", "eng" }, OcrLanguages.Missing(_dir));
        Assert.Null(OcrLanguages.Specification(_dir));
        File.WriteAllText(Path.Combine(_dir, "eng.traineddata"), "x");
        Assert.Equal(new[] { "kor" }, OcrLanguages.Missing(_dir));
        Assert.Equal("eng", OcrLanguages.Specification(_dir));
        Assert.Equal("https://raw.githubusercontent.com/tesseract-ocr/tessdata/4.1.0/kor.traineddata", OcrLanguages.DownloadUrl("kor"));
    }

    [Fact]
    public async Task OcrLanguageFiles_AreDownloaded_AndAFailureLeavesNoFile()
    {
        var port = FreePort();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        var serving = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                var context = await server.GetContextAsync();
                if (context.Request.Url!.AbsolutePath.EndsWith("kor.traineddata"))
                {
                    var body = new byte[100_000];
                    body[0] = 42;
                    context.Response.OutputStream.Write(body);
                }
                else
                {
                    context.Response.StatusCode = 404;
                }

                context.Response.Close();
            }
        });

        using var http = new HttpClient();
        var folder = Path.Combine(_dir, "tessdata");
        await OcrLanguages.DownloadAsync(http, $"http://127.0.0.1:{port}/kor.traineddata", folder, "kor");
        await Assert.ThrowsAsync<HttpRequestException>(() => OcrLanguages.DownloadAsync(http, $"http://127.0.0.1:{port}/eng.traineddata", folder, "eng"));
        await serving;

        Assert.Equal(new[] { "kor.traineddata" }, Directory.GetFiles(folder).Select(Path.GetFileName));
        Assert.Equal(100_000, new FileInfo(Path.Combine(folder, "kor.traineddata")).Length);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // ---- With the real Audiveris -----------------------------------------------------------------------------------

    /// <summary>
    /// The whole job with the Audiveris named by SCORESTUDIO_AUDIVERIS (skipped without it): two captures of ScoreStudio
    /// samples at 150 dpi, too coarse for Audiveris unless enlarged, become one score whose notes match the samples.
    /// </summary>
    [Fact]
    public async Task RealAudiveris_ReadsCoarseCaptures_AndJoinsThem()
    {
        var audiveris = Environment.GetEnvironmentVariable("SCORESTUDIO_AUDIVERIS");
        if (string.IsNullOrEmpty(audiveris) || !File.Exists(audiveris))
        {
            Assert.True(Environment.GetEnvironmentVariable("SCORESTUDIO_REQUIRE_AUDIVERIS") != "1", $"Audiveris was required but not found at '{audiveris}'");
            return;
        }

        var pictures = new[] { "beams-150dpi.png", "two-voices-150dpi.png" }.Select(f => Path.Combine(AppContext.BaseDirectory, "samples", "omr", f)).ToList();
        var job = new RecognitionJob
        {
            Audiveris = audiveris, Pictures = new PngCodec(), Languages = OcrLanguages.Specification(OcrLanguages.TessdataFolder()),
            WorkFolder = Path.Combine(_dir, "real"),
        };
        var result = await job.RunAsync(pictures);

        var expected = new[] { "beams.musicxml", "two-voices.musicxml" }
            .SelectMany(f => Tokens(MusicXmlImporter.ImportFile(Path.Combine(AppContext.BaseDirectory, "samples", "musicxml", f)).Project.Parts[0]))
            .ToList();
        var recognised = Tokens(result.Score.Parts[0]);
        var accuracy = (double)Lcs(expected, recognised) / expected.Count;
        Console.WriteLine($"Audiveris accuracy on the 150 dpi captures: {accuracy:P1} ({recognised.Count} of {expected.Count} notes and rests)");
        Assert.True(accuracy >= 0.9, $"only {accuracy:P1} of the notes and rests were recognised");
        Assert.Equal(11, result.Score.Parts[0].Measures.Count);

        // The score's pages line up with the pictures: one page each, the first staff line where the picture has it.
        Assert.Equal(2, result.Score.SourcePages.Count);
        var layout = ScoreLayoutEngine.Layout(result.Score);
        Assert.Equal(2, layout.Pages.Count);
        for (var p = 0; p < 2; p++)
        {
            var source = result.Score.SourcePages[p];
            var picture = PngCodec.Decode(new MemoryStream(source.Png));
            var pictureTop = FirstStaffLine(picture) * source.Height / picture.Height + source.Top;
            var scoreTop = layout.Pages[p].Systems[0].Staves[0].Top;
            Console.WriteLine($"page {p + 1}: first staff line at {scoreTop:F2} mm in the score, {pictureTop:F2} mm in the picture");
            Assert.InRange(scoreTop - pictureTop, -1, 1);
        }
    }

    /// <summary>The first row of a picture that is dark across most of its width: the top line of the first staff.</summary>
    private static int FirstStaffLine(GrayImage picture)
    {
        for (var y = 0; y < picture.Height; y++)
        {
            var dark = 0;
            for (var x = 0; x < picture.Width; x++) if (picture[x, y] < 128) dark++;
            if (dark > picture.Width / 2) return y;
        }

        return -1;
    }

    /// <summary>Notes and rests of a part in time order, as "pitch or r / length".</summary>
    internal static List<string> Tokens(ScorePart part) =>
        part.Measures.SelectMany(m => m.Events
            .OrderBy(e => e.Staff).ThenBy(e => e.Voice).ThenBy(e => (double)e.PositionNumerator / e.PositionDenominator).ThenBy(e => e.MidiPitch)
            .Select(e => $"{(e.Kind == "Rest" ? "r" : e.MidiPitch.ToString())}/{new Rational(e.DurationNumerator, e.DurationDenominator)}")).ToList();

    /// <summary>Length of the longest common subsequence: how many tokens of one appear, in order, in the other.</summary>
    internal static int Lcs(List<string> a, List<string> b)
    {
        var row = new int[b.Count + 1];
        for (var i = 1; i <= a.Count; i++)
        {
            var diagonal = 0;
            for (var j = 1; j <= b.Count; j++)
            {
                var above = row[j];
                row[j] = a[i - 1] == b[j - 1] ? diagonal + 1 : Math.Max(row[j], row[j - 1]);
                diagonal = above;
            }
        }

        return row[b.Count];
    }
}
