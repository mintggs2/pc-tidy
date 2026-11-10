using PcTidy.Features.Learning.Model;

namespace PcTidy.Features.Learning.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class TestClock : ILearningClock
{
    public TestClock(DateTime start) => UtcNow = start;
    public DateTime UtcNow { get; set; }
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>A fresh folder under testbed\learning\, deleted afterwards.</summary>
internal sealed class TestFolder : IDisposable
{
    public static readonly string TestbedRoot = Path.Combine(FindCodeRoot(), "testbed", "learning");

    public TestFolder(string name)
    {
        Root = Path.Combine(TestbedRoot, $"{name}-{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public string Db => Path.Combine(Root, "Data", "learning", "learned.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { /* best effort: the testbed folder is disposable */ }
        catch (UnauthorizedAccessException) { }
    }

    public static string FindCodeRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "PcTidy.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("PcTidy.sln not found");
    }
}

internal static class Moves
{
    public static readonly DateTime Day0 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>A move of <paramref name="name"/> from <paramref name="from"/> to <paramref name="to"/> in run <paramref name="run"/>.</summary>
    public static MoveEvent Of(string name, string from, string to, string run, DateTime utc, long size = 2048, string? host = null, long id = 0)
        => new(run, utc, Path.Combine(from, name), Path.Combine(to, name), size, host) { Id = id };

    /// <summary>One move per day (each in its own run) for <paramref name="count"/> days.</summary>
    public static IEnumerable<MoveEvent> Daily(string ext, string from, string to, int count, int startDay = 0, int idStart = 1)
        => Enumerable.Range(0, count).Select(i => Of($"file{i + startDay}.{ext}", from, to, $"run-{ext}-{i + startDay}", Day0.AddDays(i + startDay), id: idStart + i));
}
