using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>A local user profile as Windows lists it (Win32_UserProfile).</summary>
public sealed record UserProfileFacts(string Sid, string? LocalPath, bool Loaded, bool Special);

/// <summary>Local user profiles. Real: WMI + DeleteProfile (userenv.dll). Tests: a fake.</summary>
public interface IUserProfileControl
{
    bool TouchesRealSystem { get; }
    /// <summary>SID of the account this process runs as.</summary>
    string? CurrentUserSid { get; }
    UserProfileFacts? Query(string sid);
    void Delete(string sid);
}

public sealed class WindowsUserProfileControl : IUserProfileControl
{
    public bool TouchesRealSystem => true;

    public string? CurrentUserSid
    {
        get { try { using var id = WindowsIdentity.GetCurrent(); return id.User?.Value; } catch { return null; } }
    }

    public UserProfileFacts? Query(string sid)
    {
        // The SID was checked against a strict pattern, so it is safe inside the WQL string.
        using var searcher = new ManagementObjectSearcher(@"root\cimv2", $"SELECT SID, LocalPath, Loaded, Special FROM Win32_UserProfile WHERE SID = '{sid}'");
        using var results = searcher.Get();
        foreach (ManagementObject mo in results)
        {
            using (mo)
            {
                var loaded = mo["Loaded"] is true;
                // A hive loaded under HKEY_USERS also counts (someone is signed in, or a service uses it).
                using (var hive = Microsoft.Win32.Registry.Users.OpenSubKey(sid)) loaded |= hive is not null;
                return new UserProfileFacts(sid, mo["LocalPath"] as string, loaded, mo["Special"] is true);
            }
        }
        return null;
    }

    public void Delete(string sid)
    {
        if (!DeleteProfileW(sid, null, null))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 5) throw new UnauthorizedAccessException();
            throw new Win32Exception(error);
        }
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteProfileW(string sidString, string? profilePath, string? computerName);
}

/// <summary>
/// UserProfileDelete. Args: "sid" (an ordinary account SID, S-1-5-21-...). Uses Windows' own DeleteProfile, which removes
/// the profile folder and its registry entry. NOT reversible. Refused for your own profile, any loaded profile
/// (someone signed in, or a service using it) and Windows' special profiles.
/// </summary>
public sealed partial class UserProfileDeleteHandler(IUserProfileControl profiles) : SystemActionHandler
{
    [GeneratedRegex(@"^S-1-5-21(-\d{1,10}){3,4}$", RegexOptions.CultureInvariant)] private static partial Regex UserSid();

    public UserProfileDeleteHandler() : this(new WindowsUserProfileControl()) { }

    protected override ActionKind Kind => ActionKind.UserProfileDelete;
    protected override bool TouchesRealSystem => profiles.TouchesRealSystem;
    protected override bool NeedsElevation(PlanItem item) => true;

    private static string Sid(PlanItem item) => item.Arg("sid").Trim();

    protected override string? Validate(PlanItem item)
    {
        if (!UserSid().IsMatch(Sid(item))) return "This step doesn't name a valid user profile.";
        return item.Reversible ? "Removing a profile can't be undone, so the step must say so." : null;
    }

    protected override string Describe(PlanItem item) => $"Would remove the user profile {Sid(item)} with Windows' DeleteProfile";

    protected override Task<StepRecord> RunAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var sid = Sid(step.Item);
        if (profiles.CurrentUserSid is not { } me || me.Equals(sid, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(step.Skipped("This is your own profile, so PC Tidy never removes it."));
        var facts = profiles.Query(sid);
        if (facts is null) return Task.FromResult(step.Skipped("This profile is already gone."));
        if (facts.Special) return Task.FromResult(step.Skipped("Windows uses this profile, so PC Tidy never removes it."));
        if (facts.Loaded) return Task.FromResult(step.Skipped("This profile is in use right now (someone is signed in, or a service uses it), so it was left alone."));

        profiles.Delete(sid);
        return Task.FromResult(profiles.Query(sid) is null
            ? step.Done($"Removed the leftover profile{(facts.LocalPath is { } p ? " " + p : "")}.")
            : step.Failed("Windows didn't remove the whole profile."));
    }

    protected override Task<bool> CheckCompletedAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
        => Task.FromResult(profiles.Query(Sid(step.Item)) is null);
}
