using System.Text;
using PcTidy.Features.Speed.Common;
using PcTidy.Features.Speed.Startup;

namespace PcTidy.Features.Speed.Tests;

public class StartupApprovedTests
{
    [Fact]
    public void No_value_means_on()
    {
        Assert.True(StartupApprovedState.IsEnabled(null));
        Assert.True(StartupApprovedState.IsEnabled(Array.Empty<byte>()));
        Assert.Null(StartupApprovedState.DisabledAtUtc(null));
    }

    [Theory]
    [InlineData((byte)0x02, true)]
    [InlineData((byte)0x06, true)]
    [InlineData((byte)0x03, false)]
    [InlineData((byte)0x01, false)]
    [InlineData((byte)0x07, false)]
    public void First_byte_even_is_on_odd_is_off(byte first, bool on)
    {
        var value = new byte[12];
        value[0] = first;
        Assert.Equal(on, StartupApprovedState.IsEnabled(value));
    }

    [Fact]
    public void Off_value_carries_the_time_it_was_switched_off()
    {
        var when = new DateTime(2025, 3, 14, 9, 26, 53, DateTimeKind.Utc);
        var value = new byte[12];
        value[0] = 0x03;
        BitConverter.GetBytes(when.ToFileTimeUtc()).CopyTo(value, 4);
        Assert.Equal(when, StartupApprovedState.DisabledAtUtc(value));

        value[0] = 0x02; // on: no "switched off" time even if bytes are there
        Assert.Null(StartupApprovedState.DisabledAtUtc(value));
    }

    [Fact]
    public void Off_value_without_a_time_has_no_date()
    {
        var value = new byte[12];
        value[0] = 0x03;
        Assert.False(StartupApprovedState.IsEnabled(value));
        Assert.Null(StartupApprovedState.DisabledAtUtc(value));
    }
}

public class CommandLineTests
{
    private const string Win = @"X:\Windows";

    [Fact]
    public void Quoted_path_with_arguments()
    {
        var p = CommandLine.Parse("\"C:\\Program Files\\Tool Co\\tool.exe\" --minimized /tray", Win);
        Assert.Equal(@"C:\Program Files\Tool Co\tool.exe", p.Executable);
        Assert.Equal("--minimized /tray", p.Arguments);
        Assert.Equal(p.Executable, p.TargetFile);
    }

    [Fact]
    public void Unquoted_path_with_spaces_keeps_the_whole_program_path()
    {
        var p = CommandLine.Parse(@"C:\Program Files\Tool Co\tool.exe -min", Win);
        Assert.Equal(@"C:\Program Files\Tool Co\tool.exe", p.Executable);
        Assert.Equal("-min", p.Arguments);
    }

    [Fact]
    public void Updater_style_command_stops_at_the_first_program()
    {
        var p = CommandLine.Parse(@"C:\Users\tester\AppData\Local\Chat\Update.exe --processStart Chat.exe", Win);
        Assert.Equal(@"C:\Users\tester\AppData\Local\Chat\Update.exe", p.Executable);
        Assert.Equal("--processStart Chat.exe", p.Arguments);
    }

    [Fact]
    public void Bare_program_name_is_found_in_System32()
    {
        var p = CommandLine.Parse("ctfmon.exe", Win);
        Assert.Equal(@"X:\Windows\System32\ctfmon.exe", p.Executable);
        var q = CommandLine.Parse("ctfmon", Win);
        Assert.Equal(@"X:\Windows\System32\ctfmon.exe", q.Executable);
    }

    [Fact]
    public void Rundll32_targets_the_dll_it_loads()
    {
        var p = CommandLine.Parse("rundll32.exe \"C:\\Program Files\\Vendor\\helper.dll\",StartIt /quiet", Win);
        Assert.Equal(@"X:\Windows\System32\rundll32.exe", p.Executable);
        Assert.Equal(@"C:\Program Files\Vendor\helper.dll", p.TargetFile);

        var q = CommandLine.Parse(@"C:\Windows\System32\rundll32.exe C:\Vendor\helper.dll,Entry", Win);
        Assert.Equal(@"C:\Vendor\helper.dll", q.TargetFile);
    }

    [Fact]
    public void Script_host_targets_the_script()
    {
        var p = CommandLine.Parse("wscript.exe //B \"C:\\Scripts\\start me.vbs\"", Win);
        Assert.Equal(@"C:\Scripts\start me.vbs", p.TargetFile);
    }

    [Fact]
    public void Environment_variables_and_service_prefixes_are_resolved()
    {
        var expected = Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "app.exe");
        Assert.Equal(expected, CommandLine.Parse("%TEMP%\\app.exe -x", Win).Executable);
        Assert.Equal(@"X:\Windows\System32\svc.exe", CommandLine.Parse(@"\SystemRoot\System32\svc.exe", Win).Executable);
        Assert.Equal(@"X:\Windows\system32\svc.exe", CommandLine.Parse(@"system32\svc.exe -k", Win).Executable);
        Assert.Equal(@"C:\Drivers\thing.exe", CommandLine.Parse(@"\??\C:\Drivers\thing.exe", Win).Executable);
    }

    [Fact]
    public void Empty_command_has_no_program()
    {
        Assert.Null(CommandLine.Parse("   ", Win).Executable);
        Assert.Null(CommandLine.Parse(null, Win).TargetFile);
    }
}

public class ShortcutReaderTests
{
    [Fact]
    public void Reads_target_and_arguments_from_link_info()
    {
        var bytes = LinkBuilder.Build(@"C:\Program Files\Tool Co\tool.exe", "--background");
        var info = ShortcutReader.Parse(bytes);
        Assert.NotNull(info);
        Assert.Equal(@"C:\Program Files\Tool Co\tool.exe", info!.TargetPath);
        Assert.Equal("--background", info.Arguments);
    }

    [Fact]
    public void Reads_a_shortcut_file_from_the_sandbox()
    {
        using var sb = new SpeedSandbox();
        var lnk = Path.Combine(sb.Root, "Tool.lnk");
        File.WriteAllBytes(lnk, LinkBuilder.Build(@"C:\Tools\tool.exe", null));
        var info = ShortcutReader.Read(lnk);
        Assert.Equal(@"C:\Tools\tool.exe", info!.TargetPath);
        Assert.Null(info.Arguments);
    }

    [Fact]
    public void Garbage_is_not_a_shortcut()
    {
        Assert.Null(ShortcutReader.Parse(Encoding.ASCII.GetBytes("not a shortcut at all, just text that is long enough to pass the size check......")));
        Assert.Null(ShortcutReader.Parse(new byte[10]));
    }
}

/// <summary>Writes minimal MS-SHLLINK files: header + LinkInfo (local base path) + optional Unicode arguments.</summary>
internal static class LinkBuilder
{
    public static byte[] Build(string target, string? arguments)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        uint flags = 0x2 | 0x80 | (arguments is null ? 0u : 0x20u); // HasLinkInfo | IsUnicode | HasArguments
        w.Write(0x4C);
        w.Write(new Guid("00021401-0000-0000-C000-000000000046").ToByteArray());
        w.Write(flags);
        w.Write(new byte[0x4C - 24]); // attributes, times, size, icon, show, hotkey, reserved

        var basePath = Encoding.Latin1.GetBytes(target + "\0");
        const int headerSize = 0x1C;
        var volumeId = new byte[0x11]; // minimal VolumeID: size 0x11, type, serial, label offset, empty label
        BitConverter.GetBytes(0x11).CopyTo(volumeId, 0);
        BitConverter.GetBytes(3).CopyTo(volumeId, 4);
        BitConverter.GetBytes(0x10).CopyTo(volumeId, 12);
        var volumeOffset = headerSize;
        var baseOffset = volumeOffset + volumeId.Length;
        var suffixOffset = baseOffset + basePath.Length;
        var size = suffixOffset + 1;
        w.Write(size);
        w.Write(headerSize);
        w.Write(1); // VolumeIDAndLocalBasePath
        w.Write(volumeOffset);
        w.Write(baseOffset);
        w.Write(0); // no network part
        w.Write(suffixOffset);
        w.Write(volumeId);
        w.Write(basePath);
        w.Write((byte)0); // empty suffix

        if (arguments is not null)
        {
            w.Write((ushort)arguments.Length);
            w.Write(Encoding.Unicode.GetBytes(arguments));
        }
        w.Write(0); // terminal block
        return ms.ToArray();
    }
}
