using System.Text;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Duplicates.Planning;
using PcTidy.Features.Duplicates.Signatures;
using PcTidy.Features.Duplicates.UI;

namespace PcTidy.Features.Duplicates.Tests;

/// <summary>The wrong-extension finder: the signature table, what is (and isn't) renameable, and a real rename + undo.</summary>
public sealed class SignatureTests
{
    private static readonly ExecuteOptions Real = new() { Mode = ExecutionMode.Real, ContinueWithoutRestorePoint = _ => Task.FromResult(true) };

    public static byte[] Head(ContentKind kind) => kind switch
    {
        ContentKind.Jpeg => new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F' },
        ContentKind.Png => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 },
        ContentKind.Gif => Encoding.ASCII.GetBytes("GIF89a\u0001\u0000"),
        ContentKind.Pdf => Encoding.ASCII.GetBytes("%PDF-1.7\n%"),
        ContentKind.Zip => new byte[] { 0x50, 0x4B, 0x03, 0x04, 20, 0, 0, 0 },
        ContentKind.SevenZip => new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4 },
        ContentKind.Rar => Encoding.ASCII.GetBytes("Rar!\u001A\u0007\u0001\u0000"),
        ContentKind.Gzip => new byte[] { 0x1F, 0x8B, 0x08, 0, 0, 0, 0, 0 },
        ContentKind.IsoMedia => Ftyp("isom"),
        ContentKind.Matroska => new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81 },
        ContentKind.Avi => Riff("AVI "),
        ContentKind.Webp => Riff("WEBP"),
        ContentKind.Wav => Riff("WAVE"),
        ContentKind.Mp3 => Encoding.ASCII.GetBytes("ID3\u0004\u0000\u0000\u0000\u0000"),
        ContentKind.Ogg => Encoding.ASCII.GetBytes("OggS\u0000\u0002"),
        ContentKind.Flac => Encoding.ASCII.GetBytes("fLaC\u0000\u0000"),
        ContentKind.OleCompound => new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 },
        ContentKind.Rtf => Encoding.ASCII.GetBytes("{\\rtf1\\ansi"),
        ContentKind.WindowsProgram => Mz(),
        ContentKind.LinuxProgram => new byte[] { 0x7F, 0x45, 0x4C, 0x46, 2, 1, 1, 0 },
        ContentKind.Bmp => new byte[] { 0x42, 0x4D, 0x36, 0x10, 0, 0, 0, 0, 0, 0, 0x36, 0 },
        ContentKind.Tiff => new byte[] { 0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0 },
        ContentKind.Html => Encoding.UTF8.GetBytes("\n  <!DOCTYPE html><html><head>"),
        _ => Encoding.ASCII.GetBytes("plain old text"),
    };

    private static byte[] Ftyp(string brand) => new byte[] { 0, 0, 0, 0x18 }.Concat(Encoding.ASCII.GetBytes("ftyp" + brand.PadRight(4))).Concat(new byte[8]).ToArray();
    private static byte[] Riff(string form) => Encoding.ASCII.GetBytes("RIFF").Concat(new byte[] { 0x24, 0, 0, 0 }).Concat(Encoding.ASCII.GetBytes(form)).ToArray();

    /// <summary>A believable PE start: "MZ", and the header pointer at 60 pointing at 0x80.</summary>
    public static byte[] Mz()
    {
        var b = new byte[128];
        b[0] = 0x4D; b[1] = 0x5A;
        BitConverter.GetBytes(0x80).CopyTo(b, 60);
        return b;
    }

    [Fact]
    public void Every_signature_is_recognised()
    {
        foreach (var kind in Enum.GetValues<ContentKind>().Where(k => k != ContentKind.Unknown))
            Assert.Equal(kind, FileSignatures.Detect(Head(kind)));
        Assert.Equal(ContentKind.Unknown, FileSignatures.Detect(Head(ContentKind.Unknown)));
        Assert.Equal(ContentKind.Unknown, FileSignatures.Detect(ReadOnlySpan<byte>.Empty));
        // "MZ" alone at the start of a text isn't a program; "BM" alone isn't a bitmap.
        Assert.Equal(ContentKind.Unknown, FileSignatures.Detect(Encoding.ASCII.GetBytes("MZ is my initials and this is a long enough text file to have 64 bytes in it")));
        Assert.Equal(ContentKind.Unknown, FileSignatures.Detect(Encoding.ASCII.GetBytes("BMX notes, 2026")));
        Assert.Equal("mov", FileSignatures.IsoMediaExtension(Ftyp("qt")));
        Assert.Equal("heic", FileSignatures.IsoMediaExtension(Ftyp("heic")));
        Assert.Equal("m4a", FileSignatures.IsoMediaExtension(Ftyp("M4A")));
        Assert.Equal("mp4", FileSignatures.IsoMediaExtension(Ftyp("mp42")));
        Assert.Null(FileSignatures.IsoMediaExtension(Ftyp("zzzz")));
    }

    [Theory]
    // Office files, .jar and .apk are ZIP inside: all fine.
    [InlineData("docx", ContentKind.Zip)]
    [InlineData("xlsx", ContentKind.Zip)]
    [InlineData("pptx", ContentKind.Zip)]
    [InlineData("jar", ContentKind.Zip)]
    [InlineData("apk", ContentKind.Zip)]
    [InlineData("epub", ContentKind.Zip)]
    [InlineData("jpg", ContentKind.Jpeg)]
    [InlineData("JPEG", ContentKind.Jpeg)]
    [InlineData("nef", ContentKind.Tiff)]
    [InlineData("mov", ContentKind.IsoMedia)]
    [InlineData("doc", ContentKind.OleCompound)]
    [InlineData("exe", ContentKind.WindowsProgram)]
    [InlineData("dll", ContentKind.WindowsProgram)]
    // An installer or a script is runnable anyway: not a disguise.
    [InlineData("msi", ContentKind.WindowsProgram)]
    // Names PC Tidy doesn't judge: never a false alarm.
    [InlineData("txt", ContentKind.Pdf)]
    [InlineData("dat", ContentKind.WindowsProgram)]
    [InlineData("bin", ContentKind.Zip)]
    [InlineData("pdf", ContentKind.Unknown)]
    public void Matching_or_unjudged_names_are_left_alone(string ext, ContentKind content)
        => Assert.Null(FileSignatures.Mismatch(ext, Head(content)));

    [Theory]
    [InlineData("pdf", ContentKind.Zip, "zip", "a ZIP archive")]
    [InlineData("jpg", ContentKind.Png, "png", "a PNG picture")]
    [InlineData("png", ContentKind.Jpeg, "jpg", "a JPEG picture")]
    [InlineData("heic", ContentKind.Jpeg, "jpg", "a JPEG picture")]
    [InlineData("docx", ContentKind.Pdf, "pdf", "a PDF document")]
    [InlineData("doc", ContentKind.Zip, "docx", "a ZIP archive")]
    [InlineData("mp3", ContentKind.Wav, "wav", "a WAV sound file")]
    [InlineData("zip", ContentKind.SevenZip, "7z", "a 7-Zip archive")]
    // Can't tell the right name: shown, not renamed.
    [InlineData("pdf", ContentKind.Html, null, "a web page")]
    [InlineData("docx", ContentKind.OleCompound, null, "an old-style Office or installer file")]
    public void Harmless_mismatches_say_what_the_file_really_is(string ext, ContentKind content, string? fix, string words)
    {
        var m = FileSignatures.Mismatch(ext, Head(content));
        Assert.NotNull(m);
        Assert.Equal(words, m!.Value.Real.Words);
        Assert.False(m.Value.Real.IsProgram);
        Assert.Equal(fix, m.Value.FixExtension);
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("docx")]
    [InlineData("jpg")]
    [InlineData("txt")]
    [InlineData("mp4")]
    [InlineData("zip")]
    public void A_program_named_like_a_document_is_a_warning_and_never_gets_a_new_name(string ext)
    {
        foreach (var program in new[] { ContentKind.WindowsProgram, ContentKind.LinuxProgram })
        {
            var m = FileSignatures.Mismatch(ext, Head(program));
            Assert.NotNull(m);
            Assert.True(m!.Value.Real.IsProgram);
            Assert.Null(m.Value.FixExtension);
        }
    }

    [Fact]
    public void No_suggested_name_is_ever_runnable()
    {
        foreach (var t in FileSignatures.Types.Values.Where(t => t.FixExtension is not null))
            Assert.DoesNotContain(t.FixExtension!, FileSignatures.Runnable);
        foreach (var ext in FileSignatures.Types.Values.SelectMany(t => t.Extensions).Concat(FileSignatures.DocumentLike))
            foreach (var kind in Enum.GetValues<ContentKind>())
                if (FileSignatures.Mismatch(ext, Head(kind))?.FixExtension is { } fix)
                    Assert.DoesNotContain(fix, FileSignatures.Runnable);
    }

    [Fact]
    public async Task Scan_flags_a_disguised_program_and_offers_a_harmless_rename_that_undo_puts_back()
    {
        using var pc = new HandMadePc("wrong-ext");
        var dl = pc.K.Downloads;
        var exe = pc.Put(Path.Combine(dl, "invoice.pdf"), Mz().Concat(HandMadePc.Bytes(1, 4000)).ToArray());
        var png = pc.Put(Path.Combine(dl, "holiday.jpg"), Head(ContentKind.Png).Concat(HandMadePc.Bytes(2, 3000)).ToArray());
        pc.Put(Path.Combine(dl, "holiday.png"), Head(ContentKind.Png).Concat(HandMadePc.Bytes(3, 3000)).ToArray()); // the obvious new name is taken
        var zip = pc.Put(Path.Combine(dl, "report.pdf"), Head(ContentKind.Zip).Concat(HandMadePc.Bytes(4, 3000)).ToArray());
        pc.Put(Path.Combine(dl, "fine.docx"), Head(ContentKind.Zip).Concat(HandMadePc.Bytes(5, 3000)).ToArray());
        pc.Put(Path.Combine(dl, "notes.txt"), Encoding.ASCII.GetBytes("MZ: just a text file that starts with two capital letters, nothing more"));

        var report = await new DuplicatesEngine().ScanAsync(pc.Context());
        Assert.Equal(3, report.WrongExtensions.Count);
        var warning = report.WrongExtensions[0];
        Assert.True(DefaultPcFixture.Same(exe, warning.Path));
        Assert.True(warning.IsSecurityWarning);
        Assert.Null(warning.SuggestedPath);
        Assert.Contains("really a Windows program", warning.Reason);
        var jpg = report.WrongExtensions.Single(w => w.Path.EndsWith("holiday.jpg", StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith("holiday (2).png", jpg.SuggestedPath);
        Assert.Contains("This .jpg is really a PNG picture", jpg.Reason);
        Assert.Contains(report.WrongExtensions, w => w.Path.EndsWith("report.pdf") && w.SuggestedPath!.EndsWith("report.zip") && w.Reason.Contains("This .pdf is really a ZIP archive"));

        var result = DuplicatesScanner.ToScanResult(report, TimeSpan.Zero);
        var disguised = Assert.Single(result.Groups.Single(g => g.Id == "duplicates.disguised").Items);
        Assert.Empty(disguised.Actions);
        Assert.NotNull(disguised.DisabledReason);
        Assert.False(disguised.DefaultSelected);
        Assert.Equal("Security warning", disguised.CurrentState);
        var renames = result.Groups.Single(g => g.Id == "duplicates.wrong-extension").Items;
        Assert.All(renames, i =>
        {
            Assert.Equal(Badge.Check, i.Badge);
            Assert.False(i.DefaultSelected);
            var a = Assert.Single(i.Actions);
            Assert.Equal(ActionKind.RenamePath, a.Kind);
            Assert.Equal(NameClashPolicy.Skip, a.ClashPolicy);
            Assert.True(pc.Host.Safety.Check(a).Allowed);
        });

        // The page's row for the program can never be ticked.
        var row = new ExtensionRow(null!, warning, s => s);
        row.IsChecked = true;
        Assert.False(row.IsChecked);
        Assert.Null(row.Action);

        // Real rename, then undo: the names are back.
        var plan = PlanBuilder.FromSelection(DuplicatesModule.FeatureId, "Wrong file types", renames);
        var run = await pc.Host.Executor.ExecuteAsync(plan, Real, null, CancellationToken.None);
        Assert.Equal(2, run.DoneCount);
        Assert.True(File.Exists(Path.Combine(dl, "holiday (2).png")));
        Assert.True(File.Exists(Path.Combine(dl, "report.zip")));
        Assert.True(File.Exists(exe));
        var undo = await pc.Host.Undo.UndoRunAsync(run.RunId, null, CancellationToken.None);
        Assert.True(undo.Complete, string.Join("\n", undo.Messages));
        Assert.True(File.Exists(png) && File.Exists(zip));
        Assert.False(File.Exists(Path.Combine(dl, "report.zip")));
    }
}
