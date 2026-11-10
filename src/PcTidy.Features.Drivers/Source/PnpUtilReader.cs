using System.Diagnostics;
using System.Text;

namespace PcTidy.Features.Drivers.Source;

/// <summary>
/// Runs pnputil for READ-ONLY enumerations. Only the exact argument lists below can ever be run (there is no way to pass
/// anything else in), so no pnputil command that adds, deletes, exports, removes, disables or restarts anything is possible
/// from this class. Driver changes are only ever described as PlanItems and carried out by PcTidy.Platform.
/// </summary>
internal static class PnpUtilReader
{
    public enum Query { DriverPackages, Devices, DisconnectedWithProperties, Disconnected, Problems }

    private static readonly IReadOnlyDictionary<Query, string[]> Commands = new Dictionary<Query, string[]>
    {
        [Query.DriverPackages] = new[] { "/enum-drivers", "/devices", "/format", "xml" },
        [Query.Devices] = new[] { "/enum-devices", "/drivers", "/format", "xml" },
        [Query.DisconnectedWithProperties] = new[] { "/enum-devices", "/disconnected", "/properties", "/format", "xml" },
        [Query.Disconnected] = new[] { "/enum-devices", "/disconnected", "/format", "xml" },
        [Query.Problems] = new[] { "/enum-devices", "/problem", "/deviceids", "/format", "xml" },
    };

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    static PnpUtilReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The XML text, or null when pnputil is missing, fails, times out or doesn't print XML (older Windows builds).</summary>
    public static async Task<string?> ReadXmlAsync(Query query, CancellationToken ct)
    {
        var exe = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
        if (!File.Exists(exe)) return null;

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, // boundary-ok: no shell, the fixed read-only command runs directly
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
        };
        foreach (var a in Commands[query]) psi.ArgumentList.Add(a);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        Process? process = null;
        try
        {
            process = Process.Start(psi); // boundary-ok: read-only pnputil enumeration from the fixed list above
            if (process is null) return null;
            using var buffer = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(buffer, timeout.Token);
            var drainErrors = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(copy, drainErrors, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0) return null;
            var text = Decode(buffer.ToArray());
            var start = text.IndexOf('<');
            return start >= 0 ? text[start..] : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // timed out: treat as not available
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
        finally
        {
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            }
            process?.Dispose();
        }
    }

    /// <summary>pnputil's XML is normally UTF-8; fall back to the console's OEM code page if the bytes aren't valid UTF-8.</summary>
    private static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            var oem = System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            try { return Encoding.GetEncoding(oem).GetString(bytes); }
            catch (ArgumentException) { return Encoding.Latin1.GetString(bytes); }
        }
    }
}
