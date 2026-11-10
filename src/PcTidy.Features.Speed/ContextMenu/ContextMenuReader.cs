using PcTidy.Features.Speed.Common;

namespace PcTidy.Features.Speed.ContextMenu;

public enum ShellEntryKind { Extension, Verb }

/// <summary>One right-click menu addition: a shell extension (a CLSID) or a static verb (a <c>shell\&lt;verb&gt;</c> key).</summary>
public sealed record ShellEntry
{
    public required ShellEntryKind Kind { get; init; }
    /// <summary>Display name ("Scan with ...", "Open with Code").</summary>
    public required string Name { get; init; }
    public Guid? Clsid { get; init; }
    /// <summary>
    /// Verb key paths under the hive, without the hive (e.g. <c>Software\Classes\Directory\shell\git_bash</c>).
    /// One menu entry an app adds for files, folders and the background is one row with several keys.
    /// </summary>
    public IReadOnlyList<string> VerbKeys { get; init; } = Array.Empty<string>();
    public RegistryHive Hive { get; init; }
    public string Scope { get; init; } = "you";
    /// <summary>DLL (extensions) or program (verbs) that runs.</summary>
    public string? FilePath { get; init; }
    /// <summary>Where it shows: "files", "folders", "folder background", "drives".</summary>
    public required IReadOnlyList<string> ShowsOn { get; init; }
    /// <summary>Registry key(s) that add it, as regedit shows them.</summary>
    public required IReadOnlyList<string> Keys { get; init; }
    /// <summary>Blocked (extensions) or LegacyDisable set (verbs): already hidden from the menu.</summary>
    public bool IsDisabled { get; init; }
}

/// <summary>
/// Lists Explorer's right-click menu additions (approved feature 1) from the shell class roots (per-user first, then
/// machine), resolving each extension's CLSID to its DLL. Read-only: keys are only opened for reading.
/// </summary>
public sealed class ContextMenuReader(SpeedEnvironment env, IKnownLocations locations)
{
    /// <summary>(class path, plain-English place) for extension handlers and for verbs.</summary>
    internal static readonly (string Path, string ShowsOn)[] Places =
    {
        ("*", "files"),
        ("AllFilesystemObjects", "files and folders"),
        ("Directory", "folders"),
        ("Folder", "folders"),
        (@"Directory\Background", "folder background"),
        ("Drive", "drives"),
    };

    public IReadOnlyList<ShellEntry> Read(CancellationToken ct)
    {
        var result = new List<ShellEntry>();
        result.AddRange(Extensions(ct));
        result.AddRange(Verbs(ct));
        return result;
    }

    // ------------------------------------------------------------------ shell extensions (shellex\ContextMenuHandlers)

    private IEnumerable<ShellEntry> Extensions(CancellationToken ct)
    {
        var blocked = BlockedClsids();
        var found = new Dictionary<Guid, (ClassRoot Root, string Name, List<string> ShowsOn, List<string> Keys)>();
        foreach (var root in env.ClassRoots)
        {
            foreach (var (place, showsOn) in Places)
            {
                ct.ThrowIfCancellationRequested();
                var keyPath = $@"{root.Path}\{place}\shellex\ContextMenuHandlers";
                var handlers = env.Registry.With(root.Hive, keyPath,
                    k => k.SubKeys().Select(h => (Handler: h, Value: k.Child(h) is { } c ? UsingKey(c, x => x.Text(null)) : null)).ToList(),
                    new List<(string, string?)>());
                foreach (var (handler, value) in handlers)
                {
                    // The handler key's default value is the CLSID; some apps name the key itself after the CLSID.
                    if (!TryClsid(value, out var clsid) && !TryClsid(handler, out clsid)) continue;
                    if (!found.TryGetValue(clsid, out var entry))
                        found[clsid] = entry = (root, handler, new List<string>(), new List<string>());
                    if (!entry.ShowsOn.Contains(showsOn)) entry.ShowsOn.Add(showsOn);
                    entry.Keys.Add(env.Registry.Describe(root.Hive, keyPath + "\\" + handler));
                }
            }
        }

        foreach (var (clsid, e) in found)
        {
            var (name, dll) = ResolveClsid(clsid);
            yield return new ShellEntry
            {
                Kind = ShellEntryKind.Extension,
                Name = env.IndirectString(name) ?? CleanHandlerName(e.Name),
                Clsid = clsid,
                Hive = e.Root.Hive,
                Scope = e.Root.Scope,
                FilePath = dll,
                ShowsOn = e.ShowsOn,
                Keys = e.Keys,
                IsDisabled = blocked.Contains(clsid),
            };
        }
    }

    /// <summary>Name and DLL of a CLSID, from the first class root that has it.</summary>
    internal (string? Name, string? Dll) ResolveClsid(Guid clsid)
    {
        var id = clsid.ToString("B");
        foreach (var root in env.ClassRoots)
        {
            var path = $@"{root.Path}\CLSID\{id}";
            var hit = env.Registry.With<(string?, string?)?>(root.Hive, path, k =>
            {
                string? dll = null;
                using (var inproc = k.Child("InprocServer32")) dll = inproc?.Text(null, expand: true);
                return (k.Text(null), dll is null ? null : CommandLine.NormalizePath(dll, locations.WindowsDir, env.SearchPath));
            }, null);
            if (hit is { } h) return h;
        }
        return (null, null);
    }

    private HashSet<Guid> BlockedClsids()
    {
        var set = new HashSet<Guid>();
        foreach (var (hive, path) in env.BlockedExtensionKeys)
            foreach (var name in env.Registry.With(hive, path, k => k.Values(), Array.Empty<string>()))
                if (Guid.TryParse(name, out var g)) set.Add(g);
        return set;
    }

    // ------------------------------------------------------------------ static verbs (shell\<verb>)

    /// <summary>
    /// Static verbs. The same entry added for files, folders and the folder background (same label, same program,
    /// same root, same state) is merged into one row; hiding it sets LegacyDisable on each of its keys.
    /// </summary>
    private IEnumerable<ShellEntry> Verbs(CancellationToken ct)
    {
        var found = new List<ShellEntry>();
        foreach (var root in env.ClassRoots)
        {
            foreach (var (place, showsOn) in Places)
            {
                ct.ThrowIfCancellationRequested();
                var shellPath = $@"{root.Path}\{place}\shell";
                var verbs = env.Registry.With(root.Hive, shellPath, k => k.SubKeys(), Array.Empty<string>());
                foreach (var verb in verbs)
                {
                    var verbKey = shellPath + "\\" + verb;
                    var entry = env.Registry.With<ShellEntry?>(root.Hive, verbKey, k => ReadVerb(k, root, verb, verbKey, showsOn), null);
                    if (entry is not null) found.Add(entry);
                }
            }
        }
        return found
            .GroupBy(e => (e.Hive, Name: e.Name.ToUpperInvariant(), File: e.FilePath?.ToUpperInvariant(), e.IsDisabled))
            .Select(g => g.First() with
            {
                VerbKeys = g.SelectMany(e => e.VerbKeys).ToList(),
                ShowsOn = g.SelectMany(e => e.ShowsOn).Distinct().ToList(),
                Keys = g.SelectMany(e => e.Keys).ToList(),
            });
    }

    private ShellEntry? ReadVerb(Microsoft.Win32.RegistryKey k, ClassRoot root, string verb, string verbKey, string showsOn)
    {
        // Entries an app keeps only for programs (not shown in the menu) aren't menu items.
        if (k.HasValue("ProgrammaticAccessOnly")) return null;
        string? file = null;
        using (var command = k.Child("command"))
        {
            var line = command?.Text(null, expand: false);
            if (!string.IsNullOrWhiteSpace(line)) file = CommandLine.Parse(line, locations.WindowsDir, env.SearchPath).TargetFile;
            // Modern entries hand the click to a COM object instead of a command line.
            else if (TryClsid(command?.Text("DelegateExecute"), out var delegateClsid)) file = ResolveClsid(delegateClsid).Dll;
        }
        if (file is null && TryClsid(k.Text("ExplorerCommandHandler"), out var handler)) file = ResolveClsid(handler).Dll;
        if (file is null && TryClsid(k.Child("DropTarget") is { } dt ? UsingKey(dt, x => x.Text("CLSID")) : null, out var drop)) file = ResolveClsid(drop).Dll;

        var label = env.IndirectString(k.Text("MUIVerb")) ?? env.IndirectString(k.Text(null)) ?? verb;
        return new ShellEntry
        {
            Kind = ShellEntryKind.Verb,
            Name = label.Replace("&", "", StringComparison.Ordinal),
            VerbKeys = new[] { verbKey },
            Hive = root.Hive,
            Scope = root.Scope,
            FilePath = file,
            ShowsOn = new[] { showsOn },
            Keys = new[] { env.Registry.Describe(root.Hive, verbKey) },
            IsDisabled = k.HasValue("LegacyDisable"),
        };
    }

    private static T UsingKey<T>(Microsoft.Win32.RegistryKey key, Func<Microsoft.Win32.RegistryKey, T> read)
    {
        using (key) return read(key);
    }

    private static bool TryClsid(string? s, out Guid clsid)
    {
        clsid = Guid.Empty;
        return !string.IsNullOrWhiteSpace(s) && s.Trim().StartsWith('{') && Guid.TryParse(s.Trim(), out clsid);
    }

    /// <summary>"{GUID}" or "-MyHandler" key names make poor titles: keep what's readable.</summary>
    private static string CleanHandlerName(string name)
    {
        var n = name.Trim().TrimStart('-', ' ', '.');
        return n.Length == 0 || n.StartsWith('{') ? "Unnamed extension" : n;
    }
}
