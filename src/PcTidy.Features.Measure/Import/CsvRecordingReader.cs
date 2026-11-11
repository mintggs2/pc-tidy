using System.Diagnostics;
using System.Globalization;
using System.Text;
using PcTidy.Features.Measure.Stats;

namespace PcTidy.Features.Measure.Import;

/// <summary>
/// Streams a PresentMon-style CSV (PresentMon 1.x and 2.x, OCAT, FrameView, CapFrameX CSV) line by line. Only the frame-time,
/// process and swap-chain columns are kept, so a 200,000-frame file costs a few MB. Column names are matched without case
/// (PresentMon 1.x wrote "msBetweenPresents", later versions "MsBetweenPresents"); the separator is found from the header
/// (comma, semicolon or tab); numbers are read with invariant culture, and with a decimal comma when the separator is not a comma.
/// Column names checked against the PresentMon sources (CsvOutput.cpp at v2.0.0 and v2.2.0, README-ConsoleApplication.md on main,
/// README.md at v1.9.2) and CapFrameX's session classes. OCAT and FrameView headers are assumed to follow PresentMon's
/// (verify: their own docs don't list the columns).
/// </summary>
internal static class CsvRecordingReader
{
    private const int HeaderSearchLines = 50;

    private static readonly string[] PresentColumns = { "MsBetweenPresents", "FrameTime" };
    private static readonly string[] DisplayColumns = { "MsBetweenDisplayChange", "DisplayedTime" };
    private static readonly string[] ProcessColumns = { "Application", "ProcessName", "Process" };
    private static readonly string[] SummaryMarkers = { "average fps", "avg fps", "averagefps", "avgfps", "1% low", "p1 fps" };

    private sealed class Columns
    {
        public int Present = -1, Display = -1, Process = -1, Swap = -1, Dropped = -1, CpuBusy = -1, CpuWait = -1;
        public int Needed;
        public RecordingFormat Format;
    }

    private sealed class Track
    {
        public readonly List<float> Present = new();
        public readonly List<float> Display = new();
        public int Dropped;
        public int LongGaps;
    }

    public static ImportResult Read(TextReader reader, string fileName, ImportLimits limits, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var lines = new BoundedLineReader(reader, limits.MaxLineChars);

        // 1. Find the header within the first lines (some exports put a title or notes above it).
        Columns? cols = null;
        char sep = ',';
        bool sawSummary = false;
        for (int i = 0; i < HeaderSearchLines; i++)
        {
            var line = lines.Next(out var tooLong);
            if (tooLong) return ImportResult.Fail($"A line in this file is longer than {limits.MaxLineChars:N0} characters, so it isn't a frame-time recording.");
            if (line is null) break;
            if (line.Length == 0) continue;
            var lower = line.ToLowerInvariant();
            if (SummaryMarkers.Any(lower.Contains)) sawSummary = true;
            foreach (var candidate in new[] { ',', ';', '\t' })
            {
                if (line.IndexOf(candidate) < 0) continue;
                var names = Split(line, candidate);
                if (names.Count > limits.MaxColumns) return ImportResult.Fail($"This file has more than {limits.MaxColumns:N0} columns, so PC Tidy won't read it.");
                if (MapColumns(names) is { } c) { cols = c; sep = candidate; break; }
            }
            if (cols is not null) break;
        }
        if (cols is null)
        {
            return ImportResult.Fail(sawSummary
                ? "This looks like a summary file (averages only). Import the per-frame recording instead: the CSV with one row per frame."
                : "This file has no frame-time column (MsBetweenPresents or FrameTime). It doesn't look like a PresentMon, OCAT, FrameView or CapFrameX recording.");
        }

        // 2. Stream the rows.
        bool decimalComma = sep != ',';
        var tracks = new Dictionary<(string Process, string Swap), Track>();
        var processNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long rows = 0, skipped = 0;
        while (true)
        {
            var line = lines.Next(out var tooLong);
            if (tooLong) return ImportResult.Fail($"A line in this file is longer than {limits.MaxLineChars:N0} characters, so it isn't a frame-time recording.");
            if (line is null) break;
            if (line.Length == 0) continue;
            if (++rows > limits.MaxRows) return ImportResult.Fail($"This file has more than {limits.MaxRows:N0} frames. Record 60 to 120 seconds per run and import those files.");
            if ((rows & 0xFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                if (clock.Elapsed > limits.MaxTime) return ImportResult.Fail($"Reading this file took longer than {limits.MaxTime.TotalSeconds:N0} seconds, so PC Tidy stopped. Try a shorter recording.");
            }

            var f = Split(line, sep);
            if (f.Count <= cols.Needed) { skipped++; continue; }

            double present;
            if (cols.Present >= 0)
            {
                if (!TryNumber(f[cols.Present], decimalComma, out present)) { skipped++; continue; }
            }
            else
            {
                if (!TryNumber(f[cols.CpuBusy], decimalComma, out var busy) || !TryNumber(f[cols.CpuWait], decimalComma, out var wait)) { skipped++; continue; }
                present = busy + wait;
            }
            // Zero is the first frame of a swap chain; above a minute is a broken row or a paused capture; below 0.01 ms (or
            // anything that rounds to 0 as a float) is a damaged row that would make the FPS infinite.
            if (!FrameStats.IsUsableFrameMs(present)) { skipped++; continue; }

            var process = cols.Process >= 0 ? CleanName(f[cols.Process]) : "";
            if (process.Length == 0) process = "(no process name)";
            if (!processNames.ContainsKey(process))
            {
                if (processNames.Count >= limits.MaxProcesses) { skipped++; continue; }
                processNames[process] = process;
            }
            var swap = cols.Swap >= 0 ? f[cols.Swap].Trim().Trim('"') : "";
            var key = (processNames[process].ToLowerInvariant(), swap);
            if (!tracks.TryGetValue(key, out var t)) tracks[key] = t = new Track();

            t.Present.Add((float)present);
            if (present > 1000) t.LongGaps++;
            bool dropped = cols.Dropped >= 0 && IsTrue(f[cols.Dropped]);
            if (dropped) t.Dropped++;
            if (cols.Display >= 0 && !dropped && TryNumber(f[cols.Display], decimalComma, out var shown) && FrameStats.IsUsableFrameMs(shown))
                t.Display.Add((float)shown);
        }

        if (tracks.Count == 0) return ImportResult.Fail("This file has a frame-time header but no frames PC Tidy could read.");

        // 3. One capture per process: its busiest swap chain (overlays and launchers present on their own swap chains).
        var captures = new List<ProcessCapture>();
        foreach (var group in tracks.GroupBy(kv => kv.Key.Process))
        {
            var ordered = group.OrderByDescending(kv => kv.Value.Present.Count).ToList();
            var main = ordered[0].Value;
            if (main.Present.Count < limits.MinFrames) continue;
            var notes = new List<string>();
            int others = ordered.Skip(1).Sum(kv => kv.Value.Present.Count);
            if (others > 0) notes.Add($"{others:N0} frames from {ordered.Count - 1} other swap chain(s) of this process were left out (overlays or a second window).");
            if (main.LongGaps > 0) notes.Add($"{main.LongGaps:N0} frame(s) took longer than 1 second (a pause, loading or lost capture events). They count in the numbers.");
            bool hasDisplay = cols.Display >= 0 && main.Display.Count >= limits.MinFrames;
            captures.Add(new ProcessCapture
            {
                ProcessName = processNames[ordered[0].Key.Process],
                PresentMs = main.Present.ToArray(),
                DisplayMs = hasDisplay ? main.Display.ToArray() : null,
                DroppedFrames = main.Dropped,
                Notes = notes,
            });
        }
        if (captures.Count == 0)
            return ImportResult.Fail($"No process in this file has at least {limits.MinFrames:N0} frames. Record 60 to 120 seconds of the game.");

        return new ImportResult(new RecordingFile
        {
            FileName = fileName,
            Format = cols.Format,
            Processes = captures.OrderByDescending(c => c.PresentMs.Length).ToList(),
            SkippedRows = skipped,
        }, null);
    }

    private static Columns? MapColumns(IReadOnlyList<string> raw)
    {
        var names = raw.Select(n => n.Trim().Trim('﻿').Trim().Trim('"').Trim()).ToList();
        int Find(params string[] candidates)
        {
            foreach (var c in candidates)
            {
                int i = names.FindIndex(n => n.Equals(c, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) return i;
            }
            return -1;
        }

        var c = new Columns
        {
            Present = Find(PresentColumns),
            Display = Find(DisplayColumns),
            Process = Find(ProcessColumns),
            Swap = Find("SwapChainAddress"),
            Dropped = Find("Dropped"),
        };
        if (c.Present < 0)
        {
            // PresentMon 2.0 wrote no frame-time column; its CPU frame time is CPUBusy + CPUWait (verify on a 2.0 capture).
            c.CpuBusy = Find("CPUBusy", "MsCPUBusy");
            c.CpuWait = Find("CPUWait", "MsCPUWait");
            if (c.CpuBusy < 0 || c.CpuWait < 0) return null;
        }

        bool v1 = Find("TimeInSeconds") >= 0 || c.Dropped >= 0;
        bool cpuStart = Find("CPUStartTime", "CPUStartQPC", "CPUStartQPCTime", "CPUStartDateTime") >= 0;
        bool msPresents = c.Present >= 0 && names[c.Present].Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase);
        c.Format = v1 && !cpuStart ? RecordingFormat.PresentMonV1Csv
            : msPresents ? (cpuStart ? RecordingFormat.PresentMonV2Csv : RecordingFormat.PresentMonV1Csv)
            : RecordingFormat.PresentMonV2EarlyCsv;
        c.Needed = new[] { c.Present, c.Display, c.Process, c.Swap, c.Dropped, c.CpuBusy, c.CpuWait }.Max();
        return c;
    }

    private static string CleanName(string s)
    {
        s = s.Trim().Trim('"').Trim();
        return s.Length > 260 ? s[..260] : s;
    }

    private static bool IsTrue(string s)
    {
        s = s.Trim().Trim('"');
        return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryNumber(string s, bool decimalComma, out double value)
    {
        var span = s.AsSpan().Trim().Trim('"');
        if (double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value)) return true;
        if (decimalComma && span.IndexOf(',') >= 0 && span.IndexOf('.') < 0
            && double.TryParse(span.ToString().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value))
            return true;
        value = 0;
        return false;
    }

    /// <summary>Splits one CSV line; double quotes may wrap a field and "" is a quote inside one.</summary>
    internal static List<string> Split(string line, char sep)
    {
        var result = new List<string>();
        if (line.IndexOf('"') < 0)
        {
            int start = 0;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] != sep) continue;
                result.Add(line.Substring(start, i - start));
                start = i + 1;
            }
            result.Add(line.Substring(start));
            return result;
        }
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == sep) { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        result.Add(sb.ToString());
        return result;
    }

    /// <summary>Reads lines without ever holding more than <c>maxChars</c> of one line (a binary or one-line file can't blow up memory).</summary>
    private sealed class BoundedLineReader
    {
        private readonly TextReader _reader;
        private readonly int _max;
        private readonly char[] _buffer = new char[64 * 1024];
        private int _pos, _len;
        private readonly StringBuilder _sb = new();

        public BoundedLineReader(TextReader reader, int maxChars)
        {
            _reader = reader;
            _max = maxChars;
        }

        public string? Next(out bool tooLong)
        {
            tooLong = false;
            _sb.Clear();
            bool any = false;
            while (true)
            {
                if (_pos >= _len)
                {
                    _len = _reader.Read(_buffer, 0, _buffer.Length);
                    _pos = 0;
                    if (_len <= 0) return any ? Finish() : null;
                }
                any = true;
                int start = _pos;
                while (_pos < _len && _buffer[_pos] != '\n') _pos++;
                _sb.Append(_buffer, start, _pos - start);
                if (_sb.Length > _max) { tooLong = true; return null; }
                if (_pos < _len) { _pos++; return Finish(); }
            }
        }

        private string Finish()
        {
            if (_sb.Length > 0 && _sb[^1] == '\r') _sb.Length--;
            var s = _sb.ToString();
            return s.Length > 0 && s[0] == '﻿' ? s[1..] : s;
        }
    }
}
