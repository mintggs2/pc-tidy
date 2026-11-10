using PcTidy.App.Notifications;

namespace PcTidy.UI.Tests.Shell;

/// <summary>A fresh folder under testbed\shell\ for one test (deleted afterwards). Nothing outside it is touched.</summary>
public sealed class ShellTestFolder : IDisposable
{
    public static readonly string TestbedRoot = Path.Combine(RepoRoot(), "testbed", "shell");

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (System.IO.File.Exists(Path.Combine(d.FullName, "PcTidy.sln"))) return d.FullName;
        throw new InvalidOperationException("Tests must run from inside the PC Tidy repository.");
    }

    public ShellTestFolder(string name)
    {
        Root = Path.Combine(TestbedRoot, $"{name}-{Guid.NewGuid():N}"[..Math.Min(name.Length + 9, 60)]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string relative, string content = "x")
    {
        var p = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    public string Folder(string relative)
    {
        var p = Path.Combine(Root, relative);
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>A clock the test moves by hand; local time = UTC so quiet hours are deterministic.</summary>
public sealed class FakeClock : TimeProvider
{
    public FakeClock(DateTime utc) => UtcNow = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
    public DateTime UtcNow { get; set; }
    public override DateTimeOffset GetUtcNow() => new(UtcNow);
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public void Advance(TimeSpan by) => UtcNow += by;
}

public sealed class FakeBusy : IBusyState
{
    public bool Accepts { get; set; } = true;
    public bool AcceptsNotifications() => Accepts;
}
