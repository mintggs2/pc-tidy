using System.Diagnostics;

namespace PcTidy.Features.Cleanup.Tests;

/// <summary>Test helper: makes a directory junction inside the testbed (junctions need no admin rights).</summary>
internal static class Junction
{
    public static bool TryCreate(string link, string target)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            p.WaitForExit(10_000);
            return Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }
}
