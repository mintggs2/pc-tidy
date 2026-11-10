using System.Windows.Media;
using PcTidy.Core;
using PcTidy.Features.SpaceMap.Index;
using PcTidy.Features.SpaceMap.Treemap;

namespace PcTidy.Features.SpaceMap.View;

/// <summary>Frozen brushes for the type colours (shared by every row).</summary>
public static class CategoryBrushes
{
    private static readonly Brush[] ByCategory = new TreemapPalette().Categories.Select(Make).ToArray();
    public static readonly Brush Protected = Make(0xFF5A6068);
    public static readonly Brush Merged = Make(new TreemapPalette().Merged);

    public static Brush Of(FileCategory c) => ByCategory[(int)c < ByCategory.Length ? (int)c : 0];

    private static Brush Make(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }
}

/// <summary>Shared text helpers for the page.</summary>
internal static class SpaceText
{
    public static string Files(long n) => n == 1 ? "1 file" : $"{n:N0} files";

    public static string Ago(DateTime utc, DateTime nowUtc)
    {
        var days = (nowUtc - utc).TotalDays;
        if (utc.Year < 1980) return "a long time ago";
        if (days < 1) return "today";
        if (days < 2) return "yesterday";
        if (days < 60) return $"{(int)days} days ago";
        if (days < 700) return $"{(int)(days / 30.4)} months ago";
        return $"{Math.Max(2, (int)Math.Round(days / 365.25))} years ago";
    }
}

/// <summary>
/// A row in the folder list (one child of the folder shown in the map). One size per row: the size on disk is in
/// the selection bar. The lock is only for truly protected areas; a folder that merely holds some protected
/// folders (Users, AppData) gets a hint instead, because most of what's inside can still be tidied.
/// </summary>
public sealed record FolderRow(int Node, string Name, string SizeText, string Detail, double Fraction, Brush Swatch, bool IsProtected, bool IsFolder)
{
    public const string ProtectedInsideHint = "some protected folders inside";

    public static FolderRow From(SpaceIndex ix, int node, long largest)
    {
        var flags = ix.Flags(node);
        bool isProtected = (flags & (SpaceNodeFlags.Protected | SpaceNodeFlags.Pseudo)) != 0;
        bool isDir = ix.IsDirectory(node);
        string detail = (flags & SpaceNodeFlags.Pseudo) != 0 ? "Page file, hibernation, restore points, unreadable folders"
            : (flags & SpaceNodeFlags.Link) != 0 ? "Link to another place (not followed)"
            : (flags & SpaceNodeFlags.CloudPlaceholder) != 0 ? "Online-only: takes no space on this PC"
            : (flags & SpaceNodeFlags.Protected) != 0 ? "Protected, never cleaned"
            : isDir ? SpaceText.Files(ix.FileCount(node))
            : FileKind(ix.ExtensionName(ix.ExtensionId(node)));
        if (!isProtected && (flags & SpaceNodeFlags.ContainsProtected) != 0) detail += " · " + ProtectedInsideHint;
        if ((flags & SpaceNodeFlags.LongPath) != 0) detail += " · path over 260 characters";
        return new FolderRow(node, ix.Name(node), Sizes.Format(ix.Size(node)), detail,
            largest > 0 ? (double)ix.Size(node) / largest : 0,
            isProtected ? CategoryBrushes.Protected : CategoryBrushes.Of(ix.Category(node)), isProtected, isDir);
    }

    private static string FileKind(string extension) => extension.Length == 0 ? "File" : $"{extension.ToUpperInvariant()} file";
}

/// <summary>"N smaller items" tail of a long folder list.</summary>
public sealed record MoreRow(string Text);

public sealed record TypeRow(string Name, string SizeText, string Detail, double Fraction, Brush Swatch, FileCategory Category);

/// <summary>A file in Top files / Big and old. <see cref="MoveLabel"/> is null when no other drive has room.</summary>
public sealed record FileRow(int Node, string Name, string Folder, string SizeText, string Changed, Brush Swatch, bool IsProtected, string? MoveLabel)
{
    public System.Windows.Visibility MoveVisibility => MoveLabel is null || IsProtected ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public System.Windows.Visibility QuarantineVisibility => IsProtected ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
}

/// <summary>One step of the breadcrumb above the map.</summary>
public sealed record Crumb(int Node, string Name, bool IsFirst);
