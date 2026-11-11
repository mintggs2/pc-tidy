using System.Diagnostics;
using System.Text;
using PcTidy.Features.Measure.Import;
using Xunit;

namespace PcTidy.Features.Measure.Tests;

public class ParserTests
{
    private static RecordingFile Ok(ImportResult r)
    {
        Assert.True(r.Ok, r.Problem);
        return r.File!;
    }

    [Fact]
    public void PresentMon_1x_csv_is_read_with_display_timing_and_dropped_frames()
    {
        var frames = Synth.Frames(3000, 10, seed: 1);
        var file = Ok(RecordingImporter.ImportText(Synth.PresentMonV1(Synth.Rows("game.exe", frames)), "v1.csv"));
        Assert.Equal(RecordingFormat.PresentMonV1Csv, file.Format);
        var p = Assert.Single(file.Processes);
        Assert.Equal("game.exe", p.ProcessName);
        Assert.Equal(3000, p.PresentMs.Length);
        Assert.NotNull(p.DisplayMs);
        Assert.True(p.DroppedFrames > 0);
        Assert.Equal(3000 - p.DroppedFrames, p.DisplayMs!.Length);
        Assert.Equal(frames.Sum(), p.PresentMs.Sum(), 1);
    }

    [Fact]
    public void Old_lower_case_ms_columns_are_matched_without_case()
    {
        var file = Ok(RecordingImporter.ImportText(Synth.PresentMonV1(Synth.Rows("game.exe", Synth.Frames(500, 8, 2)), lowerCaseMs: true), "old.csv"));
        Assert.Equal(500, file.Processes[0].PresentMs.Length);
    }

    [Fact]
    public void PresentMon_2x_csv_uses_MsBetweenPresents_and_skips_NA_display_values()
    {
        var file = Ok(RecordingImporter.ImportText(Synth.PresentMonV2(Synth.Rows("game.exe", Synth.Frames(1000, 7, 3))), "v2.csv"));
        Assert.Equal(RecordingFormat.PresentMonV2Csv, file.Format);
        Assert.Equal(1000, file.Processes[0].PresentMs.Length);
        Assert.Equal(1000, file.Processes[0].DisplayMs!.Length);

        var na = Ok(RecordingImporter.ImportText(Synth.PresentMonV2(Synth.Rows("game.exe", Synth.Frames(1000, 7, 3)), naDisplay: true), "v2na.csv"));
        Assert.Null(na.Processes[0].DisplayMs);
    }

    [Fact]
    public void PresentMon_2_early_csv_uses_FrameTime_and_DisplayedTime()
    {
        var file = Ok(RecordingImporter.ImportText(Synth.PresentMonV2Early(Synth.Rows("game.exe", Synth.Frames(1000, 12, 4))), "v22.csv"));
        Assert.Equal(RecordingFormat.PresentMonV2EarlyCsv, file.Format);
        Assert.Equal(1000, file.Processes[0].PresentMs.Length);
        Assert.Equal(980, file.Processes[0].DisplayMs!.Length); // every 50th frame is NA
    }

    [Fact]
    public void PresentMon_2_0_csv_without_a_frame_time_column_adds_CPUBusy_and_CPUWait()
    {
        var sb = new StringBuilder("Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,CPUStartTime,CPUBusy,CPUWait\n");
        for (int i = 0; i < 400; i++) sb.Append("game.exe,1,0x1,DXGI,0,0,").Append(i * 0.01).Append(",6.0,4.0\n");
        var file = Ok(RecordingImporter.ImportText(sb.ToString(), "v20.csv"));
        Assert.All(file.Processes[0].PresentMs, f => Assert.Equal(10f, f));
    }

    [Fact]
    public void CapFrameX_json_session_gives_one_run_per_captured_run()
    {
        var json = Synth.CapFrameX("game.exe", Synth.Frames(800, 9, 5), Synth.Frames(900, 9, 6));
        var file = Ok(RecordingImporter.ImportText(json, "cx.json"));
        Assert.Equal(RecordingFormat.CapFrameXJson, file.Format);
        Assert.True(file.RunsAreSeparate);
        Assert.Equal(2, file.Processes.Count);
        Assert.Equal(new[] { 800, 900 }, file.Processes.Select(p => p.PresentMs.Length));
        Assert.All(file.Processes, p => Assert.Equal("game.exe", p.ProcessName));
    }

    [Fact]
    public void Byte_order_marks_utf8_and_utf16_are_handled()
    {
        using var tmp = new TempFolder();
        var text = Synth.PresentMonV1(Synth.Rows("game.exe", Synth.Frames(500, 8, 7)));
        var a = Ok(RecordingImporter.Import(tmp.Write("bom8.csv", text, new UTF8Encoding(true)), ImportLimits.Default, CancellationToken.None));
        var b = Ok(RecordingImporter.Import(tmp.Write("bom16.csv", text, new UnicodeEncoding(false, true)), ImportLimits.Default, CancellationToken.None));
        Assert.Equal(500, a.Processes[0].PresentMs.Length);
        Assert.Equal(500, b.Processes[0].PresentMs.Length);
    }

    [Theory]
    [InlineData(";", true)]
    [InlineData("\t", false)]
    [InlineData(";", false)]
    public void Semicolon_and_tab_separators_and_decimal_commas_work(string sep, bool decimalComma)
    {
        var frames = Synth.Frames(500, 8, 8);
        var file = Ok(RecordingImporter.ImportText(Synth.PresentMonV1(Synth.Rows("game.exe", frames), sep, decimalComma: decimalComma), "sep.csv"));
        Assert.Equal(frames.Sum(), file.Processes[0].PresentMs.Sum(), 0);
    }

    [Fact]
    public void Extra_unknown_columns_quotes_and_notes_above_the_header_are_fine()
    {
        var sb = new StringBuilder("Recorded with an invented tool\n\n");
        sb.Append("\"Extra\",\"Application\",\"MsBetweenPresents\",\"Another, with comma\"\n");
        for (int i = 0; i < 300; i++) sb.Append("\"x\",\"game.exe\",\"16.6\",\"a,b\"\n");
        var file = Ok(RecordingImporter.ImportText(sb.ToString(), "extra.csv"));
        Assert.Equal(300, file.Processes[0].PresentMs.Length);
    }

    [Fact]
    public void Several_processes_are_offered_busiest_first_and_small_ones_are_dropped()
    {
        var rows = Synth.Rows("launcher.exe", Synth.Frames(400, 16, 9))
            .Concat(Synth.Rows("game.exe", Synth.Frames(2000, 8, 10)))
            .Concat(Synth.Rows("tiny.exe", Synth.Frames(50, 16, 11)));
        var file = Ok(RecordingImporter.ImportText(Synth.PresentMonV1(rows), "multi.csv"));
        Assert.False(file.RunsAreSeparate);
        Assert.Equal(new[] { "game.exe", "launcher.exe" }, file.Processes.Select(p => p.ProcessName));
    }

    [Fact]
    public void A_second_swap_chain_of_the_same_process_is_left_out_with_a_note()
    {
        var main = Synth.PresentMonV1(Synth.Rows("game.exe", Synth.Frames(1000, 8, 12)), swap: "0xAAAA");
        var overlay = Synth.PresentMonV1(Synth.Rows("game.exe", Synth.Frames(300, 16, 13)), swap: "0xBBBB");
        var text = main + string.Join("\n", overlay.Split('\n').Skip(1));
        var file = Ok(RecordingImporter.ImportText(text, "swap.csv"));
        var p = Assert.Single(file.Processes);
        Assert.Equal(1000, p.PresentMs.Length);
        Assert.Contains(p.Notes, n => n.Contains("other swap chain"));
    }

    [Theory]
    [InlineData("Name,Value\nfoo,1\nbar,2\n", "no frame-time column")]
    [InlineData("Game,Average FPS,1% Low\ngame.exe,100,70\n", "summary file")]
    [InlineData("Application,MsBetweenPresents\n", "no frames")]
    [InlineData("Application,MsBetweenPresents\ngame.exe,8\ngame.exe,8\n", "at least 200 frames")]
    public void Wrong_files_are_refused_with_a_clear_message(string text, string expected)
    {
        var r = RecordingImporter.ImportText(text, "bad.csv");
        Assert.False(r.Ok);
        Assert.Contains(expected, r.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Binary_compressed_and_wrong_extension_files_are_refused()
    {
        using var tmp = new TempFolder();
        var bin = Path.Combine(tmp.Root, "x.csv");
        File.WriteAllBytes(bin, new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 });
        Assert.Contains("isn't a text file", RecordingImporter.Import(bin, ImportLimits.Default, CancellationToken.None).Problem);
        var gz = Path.Combine(tmp.Root, "x.json");
        File.WriteAllBytes(gz, new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x00 });
        Assert.Contains("compressed", RecordingImporter.Import(gz, ImportLimits.Default, CancellationToken.None).Problem);
        var exe = tmp.Write("x.exe", "MZ");
        Assert.Contains(".csv or .json", RecordingImporter.Import(exe, ImportLimits.Default, CancellationToken.None).Problem);
        var empty = tmp.Write("e.csv", "");
        Assert.Contains("empty", RecordingImporter.Import(empty, ImportLimits.Default, CancellationToken.None).Problem);
        Assert.Contains("isn't there", RecordingImporter.Import(Path.Combine(tmp.Root, "gone.csv"), ImportLimits.Default, CancellationToken.None).Problem);
    }

    [Fact]
    public void Json_that_is_not_a_CapFrameX_session_is_refused()
    {
        Assert.Contains("no CapFrameX runs", RecordingImporter.ImportText("{\"name\":\"x\"}", "a.json").Problem);
        Assert.Contains("structure doesn't match", RecordingImporter.ImportText("{\"Runs\":5}", "b.json").Problem);
    }

    [Fact]
    public void Size_row_line_and_time_limits_refuse_the_file()
    {
        var text = Synth.PresentMonV1(Synth.Rows("game.exe", Synth.Frames(1000, 8, 14)));
        Assert.Contains("larger than", RecordingImporter.ImportText(text, "a.csv", new ImportLimits { MaxBytes = 1000 }).Problem);
        Assert.Contains("more than 500 frames", RecordingImporter.ImportText(text, "a.csv", new ImportLimits { MaxRows = 500 }).Problem);
        Assert.Contains("longer than 100 characters", RecordingImporter.ImportText(new string('x', 5000), "a.csv", new ImportLimits { MaxLineChars = 100 }).Problem);
        var big = Synth.PresentMonV1(Synth.Rows("game.exe", Synth.Frames(20_000, 8, 15)));
        Assert.Contains("took longer than", RecordingImporter.ImportText(big, "a.csv", new ImportLimits { MaxTime = TimeSpan.Zero }).Problem);
    }

    [Fact]
    public void A_200k_frame_file_streams_quickly()
    {
        using var tmp = new TempFolder();
        var frames = Synth.Frames(220_000, 4, 16);
        var path = tmp.Write("big.csv", Synth.PresentMonV2(Synth.Rows("game.exe", frames)));
        var sw = Stopwatch.StartNew();
        var file = Ok(RecordingImporter.Import(path, ImportLimits.Default, CancellationToken.None));
        sw.Stop();
        Assert.Equal(220_000, file.Processes[0].PresentMs.Length);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
    }

    [Fact]
    public void Numbers_are_read_with_invariant_culture_whatever_the_thread_culture()
    {
        var old = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var file = Ok(RecordingImporter.ImportText(Synth.PresentMonV1(Synth.Rows("game.exe", Enumerable.Repeat(12.5f, 300).ToArray())), "c.csv"));
            Assert.All(file.Processes[0].PresentMs, f => Assert.Equal(12.5f, f));
        }
        finally { Thread.CurrentThread.CurrentCulture = old; }
    }
}
