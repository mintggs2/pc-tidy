using PcTidy.Catalog.Drivers;
using PcTidy.Features.Drivers.Model;
using PcTidy.Features.Drivers.Parsing;

namespace PcTidy.Features.Drivers.Tests;

/// <summary>Parsing invented sample and hand-written pnputil XML, and surviving bad input.</summary>
public class PnpUtilXmlTests
{
    private static string Read(string folder, string file) => File.ReadAllText(Path.Combine(Fixtures.Folder(folder), file));

    [Fact]
    public void Sample_driver_store_parses_every_package()
    {
        var packages = PnpUtilXml.ParseDriverPackages(Read("sample", "drivers.xml"));

        Assert.Equal(20, packages.Count);
        Assert.All(packages, p =>
        {
            Assert.True(DriverClassRules.IsOemInf(p.PublishedName), p.PublishedName);
            Assert.NotEmpty(p.OriginalName);
            Assert.NotEmpty(p.Provider);
            Assert.NotEmpty(p.ClassName);
            Assert.NotNull(p.Version.Version);
            Assert.NotNull(p.Version.Date);
        });
        Assert.Equal(13, packages.Count(p => p.Devices.Count > 0));
        Assert.Equal(2, packages.Count(p => p.OriginalName == "fabrikam-gfx.inf"));
        var gpu = Assert.Single(packages, p => p.OriginalName == "fabrikam-gfx.inf" && p.Devices.Count > 0);
        Assert.Equal("Display", gpu.ClassName);
        Assert.Equal(new Version(41, 3, 120, 15), gpu.Version.Version);
        Assert.Contains(gpu.Devices, d => d.Description == "Fabrikam Graphics Adapter" && d.Status == "Started");
    }

    [Fact]
    public void Sample_driver_store_edge_cases_parse_as_labelled()
    {
        var packages = PnpUtilXml.ParseDriverPackages(Read("sample", "drivers.xml")).ToDictionary(p => p.PublishedName);

        // Two INFs for one device: the extension INF lists the GPU instance too.
        Assert.Equal(packages["oem4.inf"].Devices.Single().InstanceId, packages["oem17.inf"].Devices.Single().InstanceId);
        // Provider text is trimmed; case is kept as written.
        Assert.Equal("Contoso Ltd.", packages["oem31.inf"].Provider);
        Assert.Equal("FABRIKAM, INC.", packages["oem9.inf"].Provider);
        // Same version, different date.
        Assert.Equal(packages["oem2.inf"].Version.Version, packages["oem31.inf"].Version.Version);
        Assert.NotEqual(packages["oem2.inf"].Version.Date, packages["oem31.inf"].Version.Date);
        // Date formats: M/d/yyyy and yyyy/MM/dd; a two-part version.
        Assert.Equal(new DateTime(2025, 5, 7), packages["oem12.inf"].Version.Date);
        Assert.Equal(new DateTime(2024, 3, 5), packages["oem25.inf"].Version.Date);
        Assert.Equal(new Version(1, 0), packages["oem38.inf"].Version.Version);
        // One package, a started and a disconnected device.
        Assert.Equal(new[] { "Disconnected", "Started" }, packages["oem38.inf"].Devices.Select(d => d.Status).OrderBy(s => s));
        // A device entry without an instance id is dropped; an empty Devices element gives no devices.
        Assert.Empty(packages["oem15.inf"].Devices);
        Assert.Empty(packages["oem27.inf"].Devices); // DriverName " oem27.inf " was trimmed
        // Unsigned package; escaped and non-ASCII text.
        Assert.False(packages["oem33.inf"].IsSigned);
        Assert.Equal("Lucerne RAID & AHCI Controller", packages["oem11.inf"].Devices.Single().Description);
        Assert.Equal("Proseware 27\" Monitor", packages["oem44.inf"].Devices.Single().Description);
        Assert.Equal("Northwind HD Webcam™", packages["oem41.inf"].Devices.Single().Description);
    }

    [Fact]
    public void Sample_problem_list_has_the_disabled_and_the_failed_device()
    {
        var devices = PnpUtilXml.ParseDevices(Read("sample", "problem.xml"));

        Assert.Equal(3, devices.Count);
        var lan = Assert.Single(devices, d => d.ClassName == "Net");
        Assert.Equal(DeviceState.Disabled, lan.State);
        Assert.Equal(0, lan.ProblemCode);
        var scanner = Assert.Single(devices, d => d.ProblemCode == 10);
        Assert.Equal(DeviceState.Problem, scanner.State);
        Assert.Equal("Image", scanner.ClassName);
        Assert.Equal("oem15.inf", scanner.DriverInf);
        Assert.Contains(@"USB\VID_0F0F&PID_0031", scanner.HardwareIds);
        var noDriver = Assert.Single(devices, d => d.ProblemCode == 28); // written as hex 0x1C
        Assert.Equal(DeviceState.Problem, noDriver.State);
        Assert.Empty(noDriver.DriverInf);
        Assert.Empty(noDriver.HardwareIds);
        Assert.Contains(@"ACPI\LTW0099", noDriver.CompatibleIds);
    }

    [Fact]
    public void Sample_disconnected_devices_parse_but_their_localized_dates_are_not_trusted()
    {
        var devices = PnpUtilXml.ParseDevices(Read("sample", "disconnected.xml"));

        Assert.Equal(14, devices.Count);
        Assert.All(devices, d => Assert.Equal(DeviceState.Disconnected, d.State));
        Assert.All(devices, d => Assert.False(d.IsPresent));
        // The sample's dates are regional text ("8/2/2026 10:00:00", "03/11/2026 10:00:00", "04.11.2026 09:05:42"), as pnputil
        // prints them: they only come typed from Windows (GetDeviceFactsAsync).
        Assert.All(devices, d => Assert.Null(d.LastSeenUtc));
        Assert.Contains(devices, d => d.HardwareIds.Count > 0);
        // Hardware ids from the DEVPKEY_Device_HardwareIds property when there is no HardwareIds element.
        var pad = Assert.Single(devices, d => d.Description == "Wingtip Game Controller");
        Assert.Equal(new[] { @"HID\VID_3C3C&PID_0003&REV_0200", @"HID\VID_3C3C&PID_0003" }, pad.HardwareIds);
        // An empty HardwareIds element and no driver.
        var reader = Assert.Single(devices, d => d.ClassName == "SmartCardReader");
        Assert.Empty(reader.HardwareIds);
        Assert.Empty(reader.DriverInf);
    }

    [Fact]
    public void Matching_drivers_show_installed_and_outranked()
    {
        var devices = PnpUtilXml.ParseDevices(Read("scenario", "devices.xml"));
        var gpu = Assert.Single(devices, d => d.ClassName == "Display");

        Assert.Equal(3, gpu.MatchingDrivers.Count);
        Assert.Equal("oem342.inf", gpu.InstalledDriver!.PublishedName);
        Assert.Contains(gpu.MatchingDrivers, m => m.PublishedName == "oem117.inf" && !m.IsInstalled);
    }

    [Fact]
    public void Hex_problem_codes_are_read()
    {
        var devices = PnpUtilXml.ParseDevices(Read("scenario", "devices.xml"));
        Assert.Contains(devices, d => d.ProblemCode == 28 && d.DriverInf.Length == 0);
    }

    [Theory]
    [InlineData("11/10/2023 3.1.0.120", 2023, 11, 10, "3.1.0.120")]
    [InlineData("06/21/2006 10.0.26100.1234", 2006, 6, 21, "10.0.26100.1234")]
    [InlineData("4.7.0.2", null, null, null, "4.7.0.2")]
    [InlineData("1/2/2024 1.0", 2024, 1, 2, "1.0")]
    public void Driver_versions_split_into_date_and_version(string text, int? y, int? m, int? d, string version)
    {
        var v = PnpUtilXml.ParseDriverVersion(text);
        Assert.Equal(Version.Parse(version), v.Version);
        Assert.Equal(y is null ? null : new DateTime(y.Value, m!.Value, d!.Value), v.Date);
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("99/99/9999 x.y")]
    public void Unreadable_versions_are_unknown_not_errors(string text)
    {
        var v = PnpUtilXml.ParseDriverVersion(text);
        Assert.Null(v.Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Invalid option: /format")]
    [InlineData("<PnpUtil><Driver DriverName=\"oem1.inf\">")]
    [InlineData("<Other><Driver DriverName=\"oem1.inf\"/></Other>")]
    [InlineData("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><PnpUtil>&e;</PnpUtil>")]
    public void Bad_or_missing_output_gives_empty_lists(string? xml)
    {
        Assert.Empty(PnpUtilXml.ParseDriverPackages(xml));
        Assert.Empty(PnpUtilXml.ParseDevices(xml));
    }

    [Fact]
    public void Text_before_the_xml_is_skipped()
    {
        var xml = "\uFEFFsome banner\r\n" + Read("scenario", "problem.xml");
        Assert.Equal(3, PnpUtilXml.ParseDevices(xml).Count);
    }

    [Theory]
    [InlineData("2026-08-02 10:00:00")]
    [InlineData("2026-08-02T10:00:00")]
    [InlineData("2026-08-02T10:00:00Z")]
    public void Iso_filetime_text_is_read(string text) => Assert.NotNull(PnpUtilXml.ParseFileTimeText(text));

    /// <summary>pnputil prints dates in the regional format: "03/11/2026" is 3 Nov on en-GB and 11 Mar on en-US. Never guessed.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("gestern")]
    [InlineData("8/2/2026 10:00:00")]
    [InlineData("11/4/2024 19:05:42")]
    [InlineData("03/11/2026 10:00:00")]
    [InlineData("04.11.2024 19:05:42")]
    [InlineData("2026/10/21 13:37")]
    public void Localized_or_unreadable_filetime_text_is_unknown(string text) => Assert.Null(PnpUtilXml.ParseFileTimeText(text));
}
