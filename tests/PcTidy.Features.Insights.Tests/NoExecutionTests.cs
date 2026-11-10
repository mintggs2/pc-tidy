using System.Reflection;
using System.Text.RegularExpressions;
using PcTidy.Features.Insights.Scripts;
using Xunit;

namespace PcTidy.Features.Insights.Tests;

/// <summary>
/// Proof that "Scan this script" can't run what it reads: neither its source nor its compiled IL references any API that
/// starts a process, hosts PowerShell, loads code or calls native functions.
/// </summary>
public class NoExecutionTests
{
    private static readonly string[] ForbiddenNamespaces =
    {
        "System.Management.Automation", "Microsoft.PowerShell", "System.Reflection.Emit", "Microsoft.CSharp", "System.Management",
    };

    private static readonly string[] ForbiddenTypes =
    {
        "System.Diagnostics.Process", "System.Diagnostics.ProcessStartInfo", "System.Activator", "System.Reflection.Assembly", "System.AppDomain",
        "Microsoft.Win32.Registry", "Microsoft.Win32.RegistryKey", "System.Runtime.InteropServices.Marshal", "System.Runtime.InteropServices.NativeLibrary",
    };

    [Fact]
    public void Script_analyzer_IL_calls_no_process_powershell_or_native_api()
    {
        var asm = typeof(ScriptAnalyzer).Assembly;
        var module = asm.ManifestModule;
        // The analyzer's own types plus the regex source generator's helpers they use.
        var types = asm.GetTypes().Where(t => (t.Namespace ?? "").StartsWith("PcTidy.Features.Insights.Scripts", StringComparison.Ordinal)
                                               || (t.Namespace ?? "").StartsWith("System.Text.RegularExpressions.Generated", StringComparison.Ordinal)).ToList();
        Assert.Contains(typeof(ScriptAnalyzer), types);
        var called = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in types)
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                         .Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)))
            {
                Assert.False(method.Attributes.HasFlag(MethodAttributes.PinvokeImpl), $"{type.Name}.{method.Name} is a native call");
                var il = method.GetMethodBody()?.GetILAsByteArray();
                if (il is null) continue;
                for (int i = 0; i + 4 < il.Length; i++)
                {
                    // call, callvirt, newobj, ldftn: each followed by a 4-byte metadata token.
                    if (il[i] is not (0x28 or 0x6F or 0x73) && !(il[i] == 0xFE && i + 5 < il.Length && il[i + 1] == 0x06)) continue;
                    int at = il[i] == 0xFE ? i + 2 : i + 1;
                    int token = BitConverter.ToInt32(il, at);
                    try
                    {
                        var target = module.ResolveMethod(token, type.IsGenericType ? type.GetGenericArguments() : null, method.IsGenericMethod ? method.GetGenericArguments() : null);
                        if (target?.DeclaringType is { } dt) called.Add(dt.FullName ?? dt.Name);
                    }
                    catch (Exception ex) when (ex is ArgumentException or BadImageFormatException or TypeLoadException) { /* not a token */ }
                }
            }
        Assert.NotEmpty(called);
        foreach (var t in called)
        {
            Assert.DoesNotContain(t, ForbiddenTypes);
            Assert.False(ForbiddenNamespaces.Any(ns => t.StartsWith(ns, StringComparison.Ordinal)), $"The script analyzer calls into {t}");
        }
    }

    [Fact]
    public void Script_analyzer_source_has_no_execution_api()
    {
        var dir = FindSource("Scripts");
        var forbidden = new Regex(@"\bProcess\.Start\b|\bProcessStartInfo\b|new\s+Process\b|System\.Diagnostics\s*;|Management\.Automation|PowerShell\.Create|RunspaceFactory|DllImport|LibraryImport|Activator\.|Assembly\.Load|Microsoft\.Win32");
        var hits = Directory.EnumerateFiles(dir, "*.cs")
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, i, line)))
            .Where(x => forbidden.IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();
        Assert.True(hits.Count == 0, "Execution APIs in the script analyzer:\n" + string.Join("\n", hits));
    }

    private static string FindSource(string sub)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "PcTidy.sln"))) d = d.Parent;
        return Path.Combine(d!.FullName, "src", "PcTidy.Features.Insights", sub);
    }
}
