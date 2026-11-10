using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using PcTidy.Features.Drivers.Model;

namespace PcTidy.Features.Drivers.Parsing;

/// <summary>
/// Parses pnputil's XML output (/format xml). Element and attribute names are not localized, but values
/// (dates, descriptions) are treated defensively: anything unreadable becomes "unknown", never an exception.
/// </summary>
public static class PnpUtilXml
{
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        CheckCharacters = false,
        IgnoreComments = true,
    };

    /// <summary>Parses `pnputil /enum-drivers /devices /format xml`. Null or unreadable XML gives an empty list.</summary>
    public static IReadOnlyList<DriverPackage> ParseDriverPackages(string? xml)
    {
        var root = Load(xml);
        if (root is null) return Array.Empty<DriverPackage>();
        var list = new List<DriverPackage>();
        foreach (var d in root.Elements("Driver"))
        {
            var name = Attr(d, "DriverName");
            if (string.IsNullOrWhiteSpace(name)) continue;
            list.Add(new DriverPackage
            {
                PublishedName = name.Trim(),
                OriginalName = Text(d, "OriginalName"),
                Provider = Text(d, "ProviderName"),
                ClassName = Text(d, "ClassName"),
                ClassGuid = Text(d, "ClassGuid"),
                Version = ParseDriverVersion(Text(d, "DriverVersion")),
                Signer = Text(d, "SignerName"),
                Devices = d.Element("Devices")?.Elements("Device")
                    .Select(x => new BoundDevice(Attr(x, "InstanceId"), Text(x, "DeviceDescription"), Text(x, "Status")))
                    .Where(x => x.InstanceId.Length > 0)
                    .ToList() ?? (IReadOnlyList<BoundDevice>)Array.Empty<BoundDevice>(),
            });
        }
        return list;
    }

    /// <summary>
    /// Parses `pnputil /enum-devices ... /format xml` (with or without /drivers, /deviceids, /properties).
    /// </summary>
    public static IReadOnlyList<DeviceRecord> ParseDevices(string? xml)
    {
        var root = Load(xml);
        if (root is null) return Array.Empty<DeviceRecord>();
        var list = new List<DeviceRecord>();
        foreach (var d in root.Elements("Device"))
        {
            var id = Attr(d, "InstanceId");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var props = ReadProperties(d);
            var hardwareIds = Values(d.Element("HardwareIds"), "HardwareId");
            if (hardwareIds.Count == 0 && props.TryGetValue("DEVPKEY_Device_HardwareIds", out var hw)) hardwareIds = hw;
            var compatibleIds = Values(d.Element("CompatibleIds"), "CompatibleId");
            if (compatibleIds.Count == 0 && props.TryGetValue("DEVPKEY_Device_CompatibleIds", out var cid)) compatibleIds = cid;

            list.Add(new DeviceRecord
            {
                InstanceId = id.Trim(),
                Description = Text(d, "DeviceDescription"),
                ClassName = Text(d, "ClassName"),
                ClassGuid = Text(d, "ClassGuid"),
                Manufacturer = Text(d, "ManufacturerName"),
                State = ParseState(Text(d, "Status")),
                ProblemCode = ParseProblemCode(d.Element("ProblemCode")?.Value),
                DriverInf = Text(d, "DriverName"),
                ExtensionInfs = Values(d.Element("ExtensionDriverNames"), "DriverName"),
                HardwareIds = hardwareIds,
                CompatibleIds = compatibleIds,
                MatchingDrivers = d.Element("MatchingDrivers")?.Elements("DriverName").Select(ParseMatching)
                    .Where(m => m.PublishedName.Length > 0).ToList() ?? (IReadOnlyList<MatchingDriver>)Array.Empty<MatchingDriver>(),
                LastArrivalUtc = Date(props, "DEVPKEY_Device_LastArrivalDate"),
                LastRemovalUtc = Date(props, "DEVPKEY_Device_LastRemovalDate"),
                InstallDateUtc = Date(props, "DEVPKEY_Device_InstallDate"),
            });
        }
        return list;
    }

    private static MatchingDriver ParseMatching(XElement m) => new()
    {
        PublishedName = Attr(m, "DriverName").Trim(),
        OriginalName = Text(m, "OriginalName"),
        Provider = Text(m, "ProviderName"),
        ClassName = Text(m, "ClassName"),
        Version = ParseDriverVersion(Text(m, "DriverVersion")),
        Signer = Text(m, "SignerName"),
        Status = Text(m, "Status"),
    };

    /// <summary>"01/15/2024 1.2.3.4" → date + version. Either part may be missing or unreadable.</summary>
    public static DriverVersionInfo ParseDriverVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return DriverVersionInfo.Unknown;
        var raw = text.Trim();
        DateTime? date = null;
        Version? version = null;
        foreach (var part in raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (version is null && part.Count(c => c == '.') >= 1 && Version.TryParse(part, out var v)) { version = v; continue; }
            date ??= ParseInfDate(part);
        }
        return new DriverVersionInfo(date, version, raw);
    }

    /// <summary>
    /// INF DriverVer dates are written month/day/year. pnputil may print them in the regional format, so the date is only
    /// shown to the user; no decision depends on it (<see cref="Analysis.StoreCleanupRules.Compare"/> uses the version only).
    /// </summary>
    internal static DateTime? ParseInfDate(string text)
    {
        string[] formats = { "MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-dd", "yyyy/MM/dd" };
        return DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>
    /// FILETIME property values as pnputil prints them. pnputil writes these in the user's regional date format
    /// ("11/4/2026" on en-US, "04/11/2026" on en-GB, "04.11.2026" on de-DE), so only ISO 8601 text, which can't be
    /// misread, is accepted; anything else gives null. The real dates come typed from Windows instead
    /// (<see cref="Source.IDriverSource.GetDeviceFactsAsync"/>). An unknown date means "maybe recent": never offered.
    /// </summary>
    internal static DateTime? ParseFileTimeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string[] formats = { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFFK", "yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-dd" };
        return DateTime.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }

    /// <summary>
    /// pnputil's status word. It's translated on non-English Windows, so an unknown word gives <see cref="DeviceState.Unknown"/>;
    /// presence then comes from Windows itself (<see cref="Source.DeviceFacts.IsPresent"/>).
    /// </summary>
    internal static DeviceState ParseState(string status) => status.Trim().ToLowerInvariant() switch
    {
        "started" => DeviceState.Started,
        "disabled" => DeviceState.Disabled,
        "problem" => DeviceState.Problem,
        "disconnected" => DeviceState.Disconnected,
        "stopped" => DeviceState.Stopped,
        _ => DeviceState.Unknown,
    };

    /// <summary>Problem code text is decimal ("10") or hex ("0xA").</summary>
    internal static int ParseProblemCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(t.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)) return hex;
        return int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) ? dec : 0;
    }

    // ------------------------------------------------------------------ helpers

    private static XElement? Load(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        var start = xml.IndexOf('<');
        if (start < 0) return null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(start == 0 ? xml : xml[start..]), ReaderSettings);
            var doc = XDocument.Load(reader);
            return doc.Root is { Name.LocalName: "PnpUtil" } root ? root : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string Attr(XElement e, string name) => e.Attribute(name)?.Value ?? "";

    private static string Text(XElement e, string name) => e.Element(name)?.Value.Trim() ?? "";

    private static IReadOnlyList<string> Values(XElement? parent, string child)
        => parent?.Elements(child).Select(x => x.Value.Trim()).Where(v => v.Length > 0).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();

    private static Dictionary<string, IReadOnlyList<string>> ReadProperties(XElement device)
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var props = device.Element("Properties");
        if (props is null) return map;
        foreach (var p in props.Elements("Property"))
        {
            var key = Attr(p, "Key");
            if (key.Length == 0 || map.ContainsKey(key)) continue;
            map[key] = p.Elements("Value").Select(v => v.Value.Trim()).Where(v => v.Length > 0).ToList();
        }
        return map;
    }

    private static DateTime? Date(Dictionary<string, IReadOnlyList<string>> props, string key)
        => props.TryGetValue(key, out var v) && v.Count > 0 ? ParseFileTimeText(v[0]) : null;
}
