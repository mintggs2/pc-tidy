using System.Text.RegularExpressions;

namespace PcTidy.Catalog.Drivers;

/// <summary>
/// A motherboard or PC maker's official support site. <see cref="UrlTemplate"/> may contain {model}
/// (URL-escaped board/product name); when it doesn't, the page is the maker's own support search.
/// </summary>
public sealed record BoardVendor(string Id, string Name, string ManufacturerPattern, string UrlTemplate);

/// <summary>Where a board's drivers come from: a maker's page, or a plain web search when the maker is unknown.</summary>
public sealed record BoardSupportLink(string Url, string VendorName, bool IsMakerSite, string? Model);

/// <summary>
/// Motherboard / PC maker support pages from Win32_BaseBoard (Manufacturer + Product). Patterns are the makers'
/// official sites; a board revision matters, so the user is reminded to pick the right one on the page.
/// Unknown makers fall back to a web search for "maker model support drivers" (no driver database, no download).
/// </summary>
public static class BoardVendors
{
    public static readonly IReadOnlyList<BoardVendor> All = new BoardVendor[]
    {
        new("asus", "ASUS", @"\bASUS", "https://www.asus.com/support/download-center/"),
        new("gigabyte", "GIGABYTE", @"\bGIGA-?BYTE\b", "https://www.gigabyte.com/Search?kw={model}"),
        new("msi", "MSI", @"\bMicro-Star\b|^MSI\b", "https://www.msi.com/search/{model}"),
        new("asrock", "ASRock", @"\bASRock\b", "https://www.asrock.com/support/index.asp"),
        new("dell", "Dell", @"^Dell\b", "https://www.dell.com/support/home/"),
        new("hp", "HP", @"^(HP|Hewlett[- ]Packard)\b", "https://support.hp.com/drivers"),
        new("lenovo", "Lenovo", @"^LENOVO\b", "https://pcsupport.lenovo.com/"),
    };

    /// <summary>Values firmware puts in when the maker didn't fill the field in: treated as unknown.</summary>
    private static readonly Regex Placeholder = new(
        @"^(to be filled by o\.?e\.?m\.?|default string|base board( product name)?|system product name|not applicable|none|n/?a|x\.x|0+|unknown|oem)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when a firmware string is missing or a well-known placeholder.</summary>
    public static bool IsPlaceholder(string? value)
        => string.IsNullOrWhiteSpace(value) || Placeholder.IsMatch(value.Trim());

    /// <summary>
    /// The support link for a board, or null when neither the maker nor the model is known.
    /// </summary>
    public static BoardSupportLink? SupportLinkFor(string? manufacturer, string? product)
    {
        var maker = IsPlaceholder(manufacturer) ? null : manufacturer!.Trim();
        var model = IsPlaceholder(product) ? null : product!.Trim();
        if (maker is null && model is null) return null;

        var vendor = maker is null ? null
            : All.FirstOrDefault(v => Regex.IsMatch(maker, v.ManufacturerPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        if (vendor is not null && (model is not null || !vendor.UrlTemplate.Contains("{model}", StringComparison.Ordinal)))
            return new BoardSupportLink(vendor.UrlTemplate.Replace("{model}", Uri.EscapeDataString(model ?? ""), StringComparison.Ordinal),
                vendor.Name, IsMakerSite: true, model);

        var query = string.Join(' ', new[] { maker, model, "support drivers" }.Where(s => !string.IsNullOrEmpty(s)));
        return new BoardSupportLink("https://www.bing.com/search?q=" + Uri.EscapeDataString(query),
            vendor?.Name ?? maker ?? "your PC maker", IsMakerSite: false, model);
    }
}
