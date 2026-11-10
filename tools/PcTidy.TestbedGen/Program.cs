using System.Diagnostics;
using System.Globalization;
using PcTidy.Core;
using PcTidy.TestbedGen;
using PcTidy.TestbedGen.Manifest;

// Exit codes: 0 ok / identical, 1 differences found, 2 usage error, 3 failure.
const string Usage = """
PcTidy.TestbedGen: deterministic fake PC for PC Tidy tests.

  generate <root> [--size small|large] [--files N] [--seed N] [--anchor 2026-11-10T12:00:00Z] [--force]
                  [--profile default|minimal] [--no-steam] [--no-epic] [--no-onedrive] [--no-redirect] [--one-drive]
                  [--gpu nvidia|amd|intel] [--laptop|--desktop] [--admin] [--reboot-pending]
  manifest <root> <out.json>
  compare  <a.json> <b.json> [--strict]       exit 1 if they differ
  lock     <file> <seconds>                   holds an exclusive handle; prints LOCKED when held
  verify   <root>                             checks catalogued files, canaries, placeholders, junctions exist
  clean    <root>                             deletes a testbed made by this tool
""";

try
{
    if (args.Length == 0) return Fail(Usage);
    var rest = args.Skip(1).ToList();
    return args[0].ToLowerInvariant() switch
    {
        "generate" => Generate(rest),
        "manifest" => Manifest(rest),
        "compare" => Compare(rest),
        "lock" => Lock(rest),
        "clean" => Clean(rest),
        "verify" => Verify(rest),
        _ => Fail(Usage),
    };
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    return Fail(ex.Message + "\n\n" + Usage);
}
catch (IOException ex)
{
    Console.Error.WriteLine("Failed: " + ex.Message);
    return 3;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Failed: " + ex);
    return 3;
}

static int Generate(List<string> a)
{
    if (a.Count < 1) return Fail(Usage);
    var root = a[0];
    var size = Value(a, "--size") is { } s ? Enum.Parse<TestbedSize>(s, ignoreCase: true) : TestbedSize.Small;
    var profile = (Value(a, "--profile") ?? "default").ToLowerInvariant() switch
    {
        "default" => SandboxProfile.Default,
        "minimal" => SandboxProfile.Minimal,
        var p => throw new ArgumentException("Unknown profile: " + p),
    };
    if (Flag(a, "--no-steam")) profile = profile with { Steam = false };
    if (Flag(a, "--no-epic")) profile = profile with { Epic = false };
    if (Flag(a, "--no-onedrive")) profile = profile with { OneDrive = false, OneDriveRedirectsFolders = false };
    if (Flag(a, "--no-redirect")) profile = profile with { OneDriveRedirectsFolders = false };
    if (Flag(a, "--one-drive")) profile = profile with { SecondDrive = false };

    var hw = SimHardware.For(profile);
    if (Value(a, "--gpu") is { } gpu) hw = hw with { GpuVendor = Enum.Parse<GpuVendor>(gpu, ignoreCase: true) };
    if (Flag(a, "--laptop")) hw = hw with { IsLaptop = true };
    if (Flag(a, "--desktop")) hw = hw with { IsLaptop = false };
    if (Flag(a, "--admin")) hw = hw with { IsAdmin = true };
    if (Flag(a, "--reboot-pending")) hw = hw with { RebootPending = true };

    var options = new TestbedOptions
    {
        BulkFiles = Value(a, "--files") is { } n ? int.Parse(n, CultureInfo.InvariantCulture) : null,
        Seed = Value(a, "--seed") is { } seed ? int.Parse(seed, CultureInfo.InvariantCulture) : new TestbedOptions().Seed,
        AnchorUtc = Value(a, "--anchor") is { } anchor
            ? DateTime.Parse(anchor, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
            : null,
        Hardware = hw,
        Force = Flag(a, "--force"),
    };
    var info = TestbedBuilder.Generate(root, size, profile, options);
    Console.WriteLine($"Generated {info.Root}");
    Console.WriteLine($"  profile   {Describe(info.Profile)}");
    Console.WriteLine($"  hardware  {info.Hardware.GpuVendor}, {(info.Hardware.IsLaptop ? "laptop" : "desktop")}, admin={info.Hardware.IsAdmin}, rebootPending={info.Hardware.RebootPending}");
    Console.WriteLine($"  files     {info.FileCount:N0} ({info.Catalog.Count:N0} catalogued + {info.BulkCounts.Values.Sum():N0} bulk) in {info.DirectoryCount:N0} folders, {Sizes.Format(info.LogicalBytes)} logical");
    Console.WriteLine($"  canaries  {info.Canaries.Count} ({info.Canaries.Count(c => c.IsTree)} trees), placeholders {info.Placeholders.Count}, junctions {info.Junctions.Count}");
    Console.WriteLine($"  anchor    {info.AnchorUtc:O}  seed {info.Seed}");
    Console.WriteLine($"  time      {info.Elapsed.TotalSeconds:0.00} s");
    return 0;
}

static int Manifest(List<string> a)
{
    if (a.Count < 2) return Fail(Usage);
    var sw = Stopwatch.StartNew();
    var m = TestbedBuilder.Manifest(a[0]);
    m.Save(a[1]);
    Console.WriteLine($"Manifest: {m.FileCount:N0} files, {m.DirectoryCount:N0} folders, {m.Entries.Count(e => e.Kind is EntryKind.Junction or EntryKind.FileLink)} links, " +
                      $"{Sizes.Format(m.TotalBytes)} hashed in {sw.Elapsed.TotalSeconds:0.00} s -> {a[1]}");
    return 0;
}

static int Compare(List<string> a)
{
    if (a.Count < 2) return Fail(Usage);
    var diff = TestbedBuilder.Compare(TestbedManifest.Load(a[0]), TestbedManifest.Load(a[1]),
        Flag(a, "--strict") ? ManifestCompareOptions.Strict : ManifestCompareOptions.Default);
    Console.WriteLine(diff.Format());
    return diff.Identical ? 0 : 1;
}

static int Lock(List<string> a)
{
    if (a.Count < 2) return Fail(Usage);
    using (TestbedBuilder.LockFile(a[0]))
    {
        Console.WriteLine("LOCKED " + a[0]);
        Console.Out.Flush();
        Thread.Sleep(TimeSpan.FromSeconds(double.Parse(a[1], CultureInfo.InvariantCulture)));
    }
    Console.WriteLine("RELEASED " + a[0]);
    return 0;
}

static int Clean(List<string> a)
{
    if (a.Count < 1) return Fail(Usage);
    TestbedBuilder.Delete(a[0]);
    Console.WriteLine("Deleted " + a[0]);
    return 0;
}

static int Verify(List<string> a)
{
    if (a.Count < 1) return Fail(Usage);
    var problems = TestbedBuilder.Verify(a[0]);
    foreach (var p in problems) Console.WriteLine("  " + p);
    Console.WriteLine(problems.Count == 0 ? "Testbed OK." : $"{problems.Count} problem(s).");
    return problems.Count == 0 ? 0 : 1;
}

static string Describe(SandboxProfile p)
    => $"drives={(p.SecondDrive ? "C+D" : "C")}, steam={p.Steam}, epic={p.Epic}, onedrive={p.OneDrive}, redirect={p.OneDriveRedirectsFolders}, user={p.UserName}";

static string? Value(List<string> a, string name)
{
    int i = a.FindIndex(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < a.Count ? a[i + 1] : null;
}

static bool Flag(List<string> a, string name) => a.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}
