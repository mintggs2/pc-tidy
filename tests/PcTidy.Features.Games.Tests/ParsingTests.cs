using System.Text;
using PcTidy.Features.Games.Clips;
using PcTidy.Features.Games.Common;
using PcTidy.Features.Games.Optimize;
using Xunit;

namespace PcTidy.Features.Games.Tests;

public class ParsingTests
{
    private const string LocalConfig =
        "\"UserLocalConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n\t\t\t\t\"apps\"\n\t\t\t\t{\n" +
        "\t\t\t\t\t\"1234560\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LastPlayed\"\t\t\"1700000000\"\n\t\t\t\t\t}\n" +
        "\t\t\t\t\t\"440\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LaunchOptions\"\t\t\"-high -novid\"\n\t\t\t\t\t}\n" +
        "\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n";

    // ---------------- VDF ----------------

    [Fact]
    public void Vdf_reads_launch_options_case_insensitively()
    {
        Assert.Equal("-high -novid", VdfText.GetValue(LocalConfig, SteamUserConfig.AppPath("440"), "launchoptions"));
        Assert.Null(VdfText.GetValue(LocalConfig, SteamUserConfig.AppPath("1234560"), "LaunchOptions"));
        Assert.Null(VdfText.GetValue(LocalConfig, SteamUserConfig.AppPath("999"), "LaunchOptions"));
        // Some Steam versions spell it "Apps".
        Assert.Equal("-high -novid", VdfText.GetValue(LocalConfig.Replace("\"apps\"", "\"Apps\""), SteamUserConfig.AppPath("440"), "LaunchOptions"));
    }

    [Fact]
    public void Vdf_replaces_an_existing_value_and_nothing_else()
    {
        var updated = VdfText.WithValue(LocalConfig, SteamUserConfig.AppPath("440"), "LaunchOptions", "-novid");
        Assert.Equal(LocalConfig.Replace("\"-high -novid\"", "\"-novid\""), updated);
    }

    [Fact]
    public void Vdf_adds_the_value_to_an_existing_app_block_with_steams_indentation()
    {
        var updated = VdfText.WithValue(LocalConfig, SteamUserConfig.AppPath("1234560"), "LaunchOptions", "-window-mode exclusive");
        var expected = LocalConfig.Replace(
            "\t\t\t\t\t\t\"LastPlayed\"\t\t\"1700000000\"\n\t\t\t\t\t}\n",
            "\t\t\t\t\t\t\"LastPlayed\"\t\t\"1700000000\"\n\t\t\t\t\t\t\"LaunchOptions\"\t\t\"-window-mode exclusive\"\n\t\t\t\t\t}\n");
        Assert.Equal(expected, updated);
    }

    [Fact]
    public void Vdf_creates_the_app_block_when_steam_has_none_yet()
    {
        var updated = VdfText.WithValue(LocalConfig, SteamUserConfig.AppPath("730"), "LaunchOptions", "+fps_max 0");
        var expected = LocalConfig.Replace(
            "\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n",
            "\t\t\t\t\t\"730\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LaunchOptions\"\t\t\"+fps_max 0\"\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n");
        Assert.Equal(expected, updated);
        Assert.Equal("+fps_max 0", VdfText.GetValue(updated!, SteamUserConfig.AppPath("730"), "LaunchOptions"));
    }

    [Fact]
    public void Vdf_escapes_quotes_and_backslashes_keeps_crlf_and_refuses_missing_structure()
    {
        var crlf = LocalConfig.Replace("\n", "\r\n");
        var updated = VdfText.WithValue(crlf, SteamUserConfig.AppPath("440"), "LaunchOptions", "-x \"a b\" C:\\dir");
        Assert.Contains("\"-x \\\"a b\\\" C:\\\\dir\"", updated);
        Assert.DoesNotContain("\r\r", updated);
        Assert.Equal(crlf.Split("\r\n").Length, updated!.Split("\r\n").Length);
        Assert.Equal("-x \"a b\" C:\\dir", VdfText.GetValue(updated, SteamUserConfig.AppPath("440"), "LaunchOptions"));

        Assert.Null(VdfText.WithValue("\"Other\"\n{\n}\n", SteamUserConfig.AppPath("440"), "LaunchOptions", "-novid"));
        Assert.Null(VdfText.WithValue(LocalConfig[..60], SteamUserConfig.AppPath("440"), "LaunchOptions", "-novid")); // truncated file
    }

    // ---------------- launch options ----------------

    [Theory]
    [InlineData("", "-novid", "-novid")]
    [InlineData("-high", "-novid", "-high -novid")]
    [InlineData("-novid  ", "-novid", "-novid  ")]
    [InlineData("+fps_max 400", "+fps_max 0", "+fps_max 400")]
    [InlineData("gamemoderun %command% -high", "-novid", "gamemoderun %command% -high -novid")]
    public void Launch_options_add_keeps_what_the_user_typed(string line, string option, string expected)
        => Assert.Equal(expected, LaunchOptionLine.Add(line, option));

    [Theory]
    [InlineData("-high -novid", "-high", false, "-novid")]
    [InlineData("-novid -threads 8 -console", "-threads", true, "-novid -console")]
    [InlineData("-novid -cpuCount=8", "-cpuCount", true, "-novid")]
    [InlineData("-HIGH", "-high", false, "")]
    [InlineData("-high %command% -novid", "-high", false, "-high %command% -novid")] // before %command% is a wrapper: untouched
    [InlineData("-threads -novid", "-threads", true, "-novid")]
    public void Launch_options_remove_takes_the_value_word_too(string line, string token, bool takesValue, string expected)
        => Assert.Equal(expected, LaunchOptionLine.Remove(line, token, takesValue));

    // ---------------- INI ----------------

    [Fact]
    public void Ini_set_changes_one_line_and_keeps_the_rest_byte_for_byte()
    {
        var ini = "[/Script/Engine.GameUserSettings]\r\nbUseVSync=True\r\nFrameRateLimit=0.000000\r\n\r\n[ScalabilityGroups]\r\nsg.ShadowQuality=2\r\n";
        Assert.Equal("True", IniText.Get(ini, "/script/engine.gameusersettings", "busevsync"));
        Assert.Equal(ini.Replace("bUseVSync=True", "bUseVSync=False"), IniText.Set(ini, "/Script/Engine.GameUserSettings", "bUseVSync", "False"));
    }

    [Fact]
    public void Ini_set_adds_missing_keys_and_sections()
    {
        var ini = "[/Script/Engine.GameUserSettings]\nFrameRateLimit=0.000000\n\n[Other]\nA=1\n";
        Assert.Equal("[/Script/Engine.GameUserSettings]\nFrameRateLimit=0.000000\nbUseVSync=False\n\n[Other]\nA=1\n",
            IniText.Set(ini, "/Script/Engine.GameUserSettings", "bUseVSync", "False"));
        Assert.Equal("[Other]\nA=1\n\n[New]\nK=V\n", IniText.Set("[Other]\nA=1\n", "New", "K", "V"));
        Assert.Equal("[New]\nK=V\n", IniText.Set("", "New", "K", "V"));
    }

    // ---------------- capture settings ----------------

    [Fact]
    public void Nvidia_DefaultPathW_is_decoded_from_utf16_binary()
    {
        var path = @"E:\Clips\NVIDIA";
        var bytes = Encoding.Unicode.GetBytes(path + "\0").Concat(new byte[] { 0xCD, 0xCD, 0x00 }).ToArray(); // trailing junk, odd length
        Assert.Equal(path, NvidiaCapturePath.Decode(bytes));
        Assert.Equal(@"C:\Users\tester\Videos", NvidiaCapturePath.Decode(Encoding.Unicode.GetBytes(@"C:\Users\tester\Videos")));
        Assert.Null(NvidiaCapturePath.Decode(null));
        Assert.Null(NvidiaCapturePath.Decode(new byte[] { 0 }));
        Assert.Null(NvidiaCapturePath.Decode(Encoding.Unicode.GetBytes("relative\\folder\0")));
        Assert.Null(NvidiaCapturePath.Decode(Encoding.Unicode.GetBytes("\0\0\0")));
    }

    [Fact]
    public void Obs_basic_ini_gives_simple_and_advanced_recording_folders()
    {
        var ini = "[General]\nName=Untitled\n\n[Output]\nMode=Advanced\n\n[SimpleOutput]\nFilePath=C:/Users/tester/Videos\n\n[AdvOut]\nRecFilePath=D:/Recordings/OBS\n";
        Assert.Equal(new[] { @"C:\Users\tester\Videos", @"D:\Recordings\OBS" }, ObsConfig.RecordingFolders(ini));
        Assert.Empty(ObsConfig.RecordingFolders("[SimpleOutput]\nFilePath=\n"));
    }

    [Fact]
    public void DHash_distance_is_small_for_similar_images_and_large_for_different_ones()
    {
        static byte[] Gradient(int w, int h, bool flip)
        {
            var p = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var v = (byte)((flip ? w - 1 - x : x) * 255 / (w - 1));
                    var i = (y * w + x) * 4;
                    p[i] = p[i + 1] = p[i + 2] = v; p[i + 3] = 255;
                }
            return p;
        }
        var a = DHash.FromBgra(Gradient(96, 54, false), 96, 54);
        var b = DHash.FromBgra(Gradient(160, 90, false), 160, 90);
        var c = DHash.FromBgra(Gradient(96, 54, true), 96, 54);
        Assert.True(DHash.Distance(a, b) <= 4);
        Assert.True(DHash.Distance(a, c) > 40);
    }
}
