using System.Text.Json;
using System.Text.Json.Serialization;
using PcTidy.Features.Measure.Stats;

namespace PcTidy.Features.Measure.Import;

/// <summary>
/// Reads a CapFrameX session file (.json). Shape, from CapFrameX's own classes (Session, SessionInfo, SessionRun,
/// SessionCaptureData in CXWorld/CapFrameX): { "Hash", "Info": { "ProcessName", "GameName", ... },
/// "Runs": [ { "CaptureData": { "TimeInSeconds": [...], "MsBetweenPresents": [...], "MsBetweenDisplayChange": [...],
/// "Dropped": [...] }, "SensorData2": ... } ] }. Only the frame arrays are kept; sensor data is skipped by the reader.
/// Each run in the session becomes its own run here.
/// The frame arrays are read number by number and counted while they are read: a file with more than
/// <see cref="ImportLimits.MaxRows"/> frames is refused as soon as the count passes it, before it is all in memory
/// (a large session would otherwise build every array first, which can take more than 1 GB).
/// </summary>
internal static class CapFrameXJsonReader
{
    private sealed class CxSession
    {
        public CxInfo? Info { get; set; }
        public List<CxRun?>? Runs { get; set; }
    }

    private sealed class CxInfo
    {
        public string? ProcessName { get; set; }
        public string? GameName { get; set; }
    }

    private sealed class CxRun
    {
        public CxCapture? CaptureData { get; set; }
    }

    private sealed class CxCapture
    {
        public float[]? MsBetweenPresents { get; set; }
        public float[]? MsBetweenDisplayChange { get; set; }
        public bool[]? Dropped { get; set; }
    }

    /// <summary>Thrown by the counting converter when the file holds more frames than the limit.</summary>
    private sealed class TooManyFramesException : Exception { }

    /// <summary>
    /// Reads a frame-time array value by value into floats (half the memory of doubles) and counts every value read across
    /// the whole file. Json.NET's NaN, "NaN" and numbers written as strings are accepted; anything unreadable becomes NaN
    /// (the reader skips it later). Each frame has at most two numbers (present and display), so the count may reach
    /// twice the frame limit.
    /// </summary>
    private sealed class CountingFrameArrayConverter(long maxValues) : JsonConverter<float[]>
    {
        private long _count;

        public override float[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException();
            var values = new List<float>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) return values.ToArray();
                if (++_count > maxValues) throw new TooManyFramesException();
                values.Add(reader.TokenType switch
                {
                    JsonTokenType.Number when reader.TryGetDouble(out var d) => (float)d,
                    JsonTokenType.String when double.TryParse(reader.GetString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var s) => (float)s,
                    JsonTokenType.Number or JsonTokenType.String or JsonTokenType.Null => float.NaN,
                    _ => throw new JsonException(),
                });
            }
            throw new JsonException();
        }

        public override void Write(Utf8JsonWriter writer, float[] value, JsonSerializerOptions options) => throw new NotSupportedException();
    }

    /// <summary>Options for one read (the converter counts the frames of this one file).</summary>
    private static JsonSerializerOptions OptionsFor(ImportLimits limits) => new()
    {
        PropertyNameCaseInsensitive = true,
        // CapFrameX is written with Json.NET, which can write NaN and numbers as strings.
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 64,
        Converters = { new CountingFrameArrayConverter(2 * limits.MaxRows) },
    };

    public static ImportResult Read(Stream stream, string fileName, ImportLimits limits, CancellationToken ct)
    {
        CxSession? session;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(limits.MaxTime);
            try
            {
                session = JsonSerializer.DeserializeAsync<CxSession>(stream, OptionsFor(limits), timeout.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return ImportResult.Fail($"Reading this file took longer than {limits.MaxTime.TotalSeconds:N0} seconds, so PC Tidy stopped.");
            }
            catch (TooManyFramesException)
            {
                return TooManyFrames(limits);
            }
            catch (JsonException ex) when (ex.InnerException is TooManyFramesException)
            {
                return TooManyFrames(limits);
            }
            catch (JsonException)
            {
                return ImportResult.Fail("This JSON file isn't a CapFrameX session PC Tidy can read (the structure doesn't match).");
            }
        }

        if (session?.Runs is not { Count: > 0 } runs)
            return ImportResult.Fail("This JSON file has no CapFrameX runs (no \"Runs\" list with frame times).");

        var name = (session.Info?.ProcessName ?? "").Trim();
        if (name.Length == 0) name = (session.Info?.GameName ?? "").Trim();
        if (name.Length == 0) name = "(no process name)";
        if (name.Length > 260) name = name[..260];

        var captures = new List<ProcessCapture>();
        long skipped = 0, total = 0;
        foreach (var run in runs)
        {
            var data = run?.CaptureData;
            if (data?.MsBetweenPresents is not { } presents) continue;
            total += presents.Length;
            if (total > limits.MaxRows) return TooManyFrames(limits);
            var present = new List<float>(presents.Length);
            var display = new List<float>(presents.Length);
            int dropped = 0, gaps = 0;
            for (int i = 0; i < presents.Length; i++)
            {
                var p = presents[i];
                // Below 0.01 ms (it rounds toward 0) or above a minute: a damaged value, never an infinite FPS.
                if (!FrameStats.IsUsableFrameMs(p)) { skipped++; continue; }
                present.Add(p);
                if (p > 1000) gaps++;
                bool isDropped = data.Dropped is { } d && i < d.Length && d[i];
                if (isDropped) dropped++;
                if (!isDropped && data.MsBetweenDisplayChange is { } dc && i < dc.Length && FrameStats.IsUsableFrameMs(dc[i]))
                    display.Add(dc[i]);
            }
            if (present.Count < limits.MinFrames) continue;
            var notes = new List<string>();
            if (runs.Count > 1) notes.Add($"Run {captures.Count + 1} of the {runs.Count} runs in this CapFrameX session.");
            if (gaps > 0) notes.Add($"{gaps:N0} frame(s) took longer than 1 second (a pause, loading or lost capture events). They count in the numbers.");
            captures.Add(new ProcessCapture
            {
                ProcessName = name,
                PresentMs = present.ToArray(),
                DisplayMs = display.Count >= limits.MinFrames ? display.ToArray() : null,
                DroppedFrames = dropped,
                Notes = notes,
            });
        }
        if (captures.Count == 0)
            return ImportResult.Fail($"No run in this CapFrameX file has at least {limits.MinFrames:N0} frames.");

        return new ImportResult(new RecordingFile
        {
            FileName = fileName,
            Format = RecordingFormat.CapFrameXJson,
            Processes = captures,
            RunsAreSeparate = true,
            SkippedRows = skipped,
        }, null);
    }

    private static ImportResult TooManyFrames(ImportLimits limits)
        => ImportResult.Fail($"This file has more than {limits.MaxRows:N0} frames. Record 60 to 120 seconds per run and import those files.");
}
