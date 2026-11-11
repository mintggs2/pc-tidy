namespace PcTidy.Catalog.Insights;

/// <summary>The shared runtimes the Insights runtime card can offer to install.</summary>
public enum RuntimeKind { VisualCpp64, VisualCpp32, DirectX, DotNetDesktop8, DotNetDesktop9 }

/// <summary>One runtime PC Tidy may ask winget to install: the exact winget package id and the name people know.</summary>
public sealed record RuntimeInstall(RuntimeKind Kind, string WingetId, string Name);

/// <summary>
/// The allow-list for <c>ToolIds.WingetInstall</c> (v3.1 build list B4): Microsoft's own runtimes, nothing else.
/// The ids were checked against the manifests folder of github.com/microsoft/winget-pkgs (Nov 2026):
/// manifests/m/Microsoft/VCRedist/2015+/x64 and /x86, manifests/m/Microsoft/DirectX,
/// manifests/m/Microsoft/DotNet/DesktopRuntime/8 and /9. Ids are matched exactly (ordinal), so a variant
/// spelling or extra text is refused.
/// </summary>
public static class RuntimeInstalls
{
    public const string VisualCpp64Id = "Microsoft.VCRedist.2015+.x64";
    public const string VisualCpp32Id = "Microsoft.VCRedist.2015+.x86";
    public const string DirectXId = "Microsoft.DirectX";
    public const string DotNetDesktop8Id = "Microsoft.DotNet.DesktopRuntime.8";
    public const string DotNetDesktop9Id = "Microsoft.DotNet.DesktopRuntime.9";

    public static readonly IReadOnlyList<RuntimeInstall> All = new RuntimeInstall[]
    {
        new(RuntimeKind.VisualCpp64, VisualCpp64Id, "Visual C++ 2015-2022 (64-bit)"),
        new(RuntimeKind.VisualCpp32, VisualCpp32Id, "Visual C++ 2015-2022 (32-bit)"),
        new(RuntimeKind.DirectX, DirectXId, "DirectX End-User Runtime"),
        new(RuntimeKind.DotNetDesktop8, DotNetDesktop8Id, ".NET Desktop Runtime 8"),
        new(RuntimeKind.DotNetDesktop9, DotNetDesktop9Id, ".NET Desktop Runtime 9"),
    };

    /// <summary>True only for an id on the list, spelled exactly.</summary>
    public static bool IsAllowed(string? wingetId) => wingetId is not null && All.Any(r => string.Equals(r.WingetId, wingetId, StringComparison.Ordinal));

    public static RuntimeInstall For(RuntimeKind kind) => All.First(r => r.Kind == kind);

    /// <summary>The preview line every install row and plan item carries.</summary>
    public static string PreviewText(string name) => $"Downloads from Microsoft through winget. You accept Microsoft's licence terms for {name}.";
}
