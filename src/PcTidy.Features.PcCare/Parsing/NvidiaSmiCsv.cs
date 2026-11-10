using System.Globalization;
using PcTidy.Features.PcCare.Model;

namespace PcTidy.Features.PcCare.Parsing;

/// <summary>
/// Parses <c>nvidia-smi --query-gpu=name,temperature.gpu,fan.speed --format=csv,noheader,nounits</c>:
/// one "name, temp, fan" line per GPU. Numbers are invariant; "[N/A]" fans become null.
/// </summary>
public static class NvidiaSmiCsv
{
    public static IReadOnlyList<GpuReading> Parse(string? text)
    {
        var list = new List<GpuReading>();
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (parts.Length < 2) continue;
            // The name may itself contain commas: temperature and fan are always the last two fields.
            int fanAt = parts.Length - 1, tempAt = parts.Length >= 3 ? parts.Length - 2 : 1;
            if (!int.TryParse(parts[tempAt].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var temp) || temp is < 0 or > 150) continue;
            int? fan = parts.Length >= 3 && int.TryParse(parts[fanAt].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : null;
            var name = string.Join(",", parts.Take(tempAt)).Trim();
            if (name.Length > 0) list.Add(new GpuReading(name, temp, fan));
        }
        return list;
    }
}
