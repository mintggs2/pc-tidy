using PcTidy.Features.Search.Backends;
using Xunit;

namespace PcTidy.Features.Search.Tests;

/// <summary>
/// SRC-1: Everything's SDK is loaded only from an admin-only folder and only when signed by voidtools. A DLL next to a
/// program in a user folder (where anyone could plant it and register Everything's window class) is never loaded.
/// Read-only checks; the test files live in testbed\secfix-v3\search and are removed afterwards.
/// </summary>
public class EverythingTrustTests
{
    private static string Testbed()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "PcTidy.sln"))) d = d.Parent;
        return Path.Combine(d!.FullName, "testbed", "secfix-v3", "search", Guid.NewGuid().ToString("N")[..8]);
    }

    [Fact]
    public void A_planted_SDK_in_a_user_writable_folder_is_never_loaded()
    {
        var folder = Testbed();
        Directory.CreateDirectory(folder);
        try
        {
            var dll = Path.Combine(folder, EverythingLocator.SdkFileName);
            File.WriteAllText(dll, "not a DLL, and the folder is the user's");
            Assert.NotNull(EverythingSdkTrust.AdminOnlyProblem(dll));
            Assert.Equal(IntPtr.Zero, EverythingSdkTrust.LoadTrusted(dll, out var why));
            Assert.NotNull(why);
            Assert.False(new EverythingBackend(new FixedInstall(dll)).CheckAvailable());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
            var parent = Path.GetDirectoryName(folder)!;
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
    }

    [Fact]
    public void Windows_own_folder_counts_as_admin_only()
        => Assert.Null(EverythingSdkTrust.AdminOnlyProblem(Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts")));

    [Fact]
    public void A_Windows_DLL_in_an_admin_only_folder_still_isnt_voidtools()
    {
        // Validly signed by Microsoft and in a folder only admins can change, but not voidtools: nothing is loaded.
        var dll = Path.Combine(Environment.SystemDirectory, "version.dll");
        Assert.Equal(IntPtr.Zero, EverythingSdkTrust.LoadTrusted(dll, out var why));
        Assert.NotNull(why);
    }

    [Theory]
    [InlineData("CN=voidtools, O=voidtools, L=Somewhere, C=AU", true)]
    [InlineData("CN=\"voidtools PTY LTD\", O=\"voidtools PTY LTD\"", true)]
    [InlineData("CN=Microsoft Windows, O=Microsoft Corporation", false)]
    [InlineData("CN=not voidtools, O=Evil", false)]
    [InlineData("OU=voidtools, CN=Evil", false)]
    [InlineData("CN=voidtoolsX, O=Evil", false)]
    public void Only_voidtools_is_the_publisher(string subject, bool expected)
        => Assert.Equal(expected, EverythingSdkTrust.IsPublisher(subject));

    private sealed class FixedInstall(string sdk) : IEverythingLocator
    {
        public EverythingInstall? Locate() => new(@"X:\Everything\Everything.exe", sdk);
    }
}
