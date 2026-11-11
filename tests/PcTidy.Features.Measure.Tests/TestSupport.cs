using System.Globalization;
using System.Text;
using PcTidy.Features.Measure.Compare;
using PcTidy.Features.Measure.Import;
using PcTidy.Features.Measure.Stats;

namespace PcTidy.Features.Measure.Tests;

/// <summary>Invented frame data only. Nothing here comes from a real capture.</summary>
internal static class Synth
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Frame times around <paramref name="meanMs"/> with some jitter and occasional hitches (seeded).</summary>
    public static float[] Frames(int count, double meanMs, int seed, double jitter = 0.08, double hitchChance = 0.002)
    {
        var rng = new Random(seed);
        var f = new float[count];
        for (int i = 0; i < count; i++)
        {
            double z = Gaussian(rng);
            double v = meanMs * Math.Exp(jitter * z - jitter * jitter / 2);
            if (rng.NextDouble() < hitchChance) v *= 3 + rng.NextDouble() * 3;
            f[i] = (float)v;
        }
        return f;
    }

    public static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    public static RunSummary Run(double avgFps, double lowFps, double seconds = 90, string process = "game.exe", RecordingFormat format = RecordingFormat.PresentMonV2Csv)
    {
        int frames = (int)(avgFps * seconds);
        return new RunSummary
        {
            FileName = "run.csv",
            ProcessName = process,
            Format = format,
            Presents = new RunStats { Frames = frames, Seconds = seconds, AverageFps = avgFps, OnePercentLowFps = lowFps, PointOnePercentLowFps = lowFps * 0.8, MedianFrameMs = 1000 / avgFps, P99FrameMs = 1000 / lowFps, Stutters = 3 },
        };
    }

    public static RunSummary FromFrames(float[] frames, string process = "game.exe") => new()
    {
        FileName = "sim.csv",
        ProcessName = process,
        Format = RecordingFormat.PresentMonV2Csv,
        Presents = FrameStats.Compute(frames),
    };

    /// <summary>PresentMon 1.x-style CSV (as PresentMon 1.x, OCAT and FrameView write it; columns per the PresentMon 1.9.2 README).</summary>
    public static string PresentMonV1(IEnumerable<(string App, float Ms)> rows, string sep = ",", bool lowerCaseMs = false, bool decimalComma = false, string swap = "0x0000020A1B2C3D40")
    {
        var sb = new StringBuilder();
        var ms = lowerCaseMs ? "ms" : "Ms";
        sb.AppendLine(string.Join(sep, "Application", "ProcessID", "SwapChainAddress", "Runtime", "SyncInterval", "PresentFlags", "AllowsTearing", "PresentMode",
            "Dropped", "TimeInSeconds", ms + "BetweenPresents", ms + "BetweenDisplayChange", ms + "InPresentAPI", ms + "UntilRenderComplete", ms + "UntilDisplayed"));
        double t = 0;
        int i = 0;
        foreach (var (app, f) in rows)
        {
            t += f / 1000.0;
            bool dropped = i++ % 97 == 5;
            string N(double v) => decimalComma ? v.ToString("0.0000", Inv).Replace('.', ',') : v.ToString("0.0000", Inv);
            sb.AppendLine(string.Join(sep, app, "4242", swap, "DXGI", "0", "0", "1", "Hardware: Independent Flip",
                dropped ? "1" : "0", N(t), N(f), dropped ? "0" : N(f), "0.2000", "1.1000", dropped ? "0" : "3.4000"));
        }
        return sb.ToString();
    }

    /// <summary>PresentMon 2.4+ console CSV (column names per README-ConsoleApplication.md on main).</summary>
    public static string PresentMonV2(IEnumerable<(string App, float Ms)> rows, bool naDisplay = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,FrameType,CPUStartTime,MsBetweenSimulationStart,MsBetweenPresents,MsBetweenDisplayChange,MsInPresentAPI,MsRenderPresentLatency,MsUntilDisplayed,MsPCLatency,MsCPUBusy,MsCPUWait,MsGPULatency,MsGPUTime,MsGPUBusy,MsGPUWait,MsAnimationError,AnimationTime,MsFlipDelay");
        double t = 0;
        foreach (var (app, f) in rows)
        {
            t += f;
            var fs = f.ToString("0.000000", Inv);
            sb.AppendLine($"{app},9001,0x00000123ABCD0000,DXGI,0,0,1,Hardware: Independent Flip,Application,{(t / 1000).ToString("0.000000", Inv)},NA,{fs},{(naDisplay ? "NA" : fs)},0.150000,4.000000,5.000000,NA,{(f * 0.7).ToString("0.000000", Inv)},{(f * 0.3).ToString("0.000000", Inv)},0.5,4.0,3.9,0.1,NA,NA,0");
        }
        return sb.ToString();
    }

    /// <summary>PresentMon 2.0-2.3 console CSV (CsvOutput.cpp at v2.2.0: FrameTime, DisplayedTime with NA when not displayed).</summary>
    public static string PresentMonV2Early(IEnumerable<(string App, float Ms)> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,CPUStartTime,FrameTime,CPUBusy,CPUWait,GPULatency,GPUTime,GPUBusy,GPUWait,DisplayLatency,DisplayedTime,AnimationError");
        int i = 0;
        double t = 0;
        foreach (var (app, f) in rows)
        {
            t += f;
            var fs = f.ToString("0.000000", Inv);
            bool shown = i++ % 50 != 3;
            sb.AppendLine($"{app},77,0x0000000000ABC000,DXGI,1,0,0,Composed: Flip,{(t / 1000).ToString("0.000000", Inv)},{fs},1.0,1.0,0.2,3.0,3.0,0.1,{(shown ? "8.0" : "NA")},{(shown ? fs : "NA")},{(shown ? "0.0" : "NA")}");
        }
        return sb.ToString();
    }

    /// <summary>CapFrameX session JSON (shape per CapFrameX's Session / SessionInfo / SessionRun / SessionCaptureData classes).</summary>
    public static string CapFrameX(string process, params float[][] runs)
    {
        string Arr(IEnumerable<double> v) => "[" + string.Join(",", v.Select(x => x.ToString("0.####", Inv))) + "]";
        var sb = new StringBuilder();
        sb.Append("{\"Hash\":\"abc\",\"Info\":{\"ProcessName\":\"").Append(process).Append("\",\"GameName\":\"Invented Game\",\"Comment\":\"\"},\"Runs\":[");
        for (int r = 0; r < runs.Length; r++)
        {
            if (r > 0) sb.Append(',');
            var f = runs[r];
            double t = 0;
            var times = f.Select(x => t += x / 1000.0).ToList();
            sb.Append("{\"Hash\":\"r").Append(r).Append("\",\"PresentMonRuntime\":\"DXGI\",\"CaptureData\":{")
              .Append("\"TimeInSeconds\":").Append(Arr(times)).Append(',')
              .Append("\"MsBetweenPresents\":").Append(Arr(f.Select(x => (double)x))).Append(',')
              .Append("\"MsBetweenDisplayChange\":").Append(Arr(f.Select(x => (double)x))).Append(',')
              .Append("\"Dropped\":[").Append(string.Join(",", f.Select(_ => "false"))).Append("]},")
              .Append("\"SensorData2\":{\"MeasureTime\":{\"Values\":[1,2,3]}}}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    public static IEnumerable<(string, float)> Rows(string app, float[] frames) => frames.Select(f => (app, f));
}

internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "pctidy-measure-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Write(string name, string text, Encoding? encoding = null)
    {
        var p = Path.Combine(Root, name);
        File.WriteAllText(p, text, encoding ?? new UTF8Encoding(false));
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
