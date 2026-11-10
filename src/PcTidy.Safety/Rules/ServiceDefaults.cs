using PcTidy.Catalog.Services;
using PcTidy.Core.Model;

namespace PcTidy.Safety.Rules;

/// <summary>
/// Windows 10/11 default start types for the protected services the bad-tweak finder may switch BACK ON.
/// A protected service may only ever be set to one of its defaults (or Automatic, which can't weaken anything);
/// never Manual/Delayed when the default is Automatic, never Disabled. The table itself lives in
/// <see cref="ProtectedServices"/>, shared with the service handler so the two walls can't disagree (SVC-1).
/// </summary>
public static class ServiceDefaults
{
    /// <summary>True if setting protected service <paramref name="name"/> to <paramref name="start"/> is a restore to a Windows default.</summary>
    public static bool IsDefaultFor(string name, ServiceStart start) => ProtectedServices.IsDefaultFor(name, start);

    public static IReadOnlyCollection<string> KnownServices => ProtectedServices.KnownDefaults;
}
