using PcTidy.Core.Model;

namespace PcTidy.Engine;

/// <summary>PC Tidy chose not to do something for safety (online-only file, links inside, already quarantined...). Nothing changed.</summary>
public sealed class SafetyRefusalException : IOException
{
    public SafetyRefusalException(string message) : base(message) { }
}

/// <summary>
/// A cross-drive quarantine copied and verified everything, but some originals stayed in place
/// (changed after the copy, or in use). Nothing is lost: the full copy stays in quarantine (<see cref="Entry"/>).
/// </summary>
public sealed class PartialQuarantineException : IOException
{
    public QuarantineEntry Entry { get; }
    public IReadOnlyList<string> LeftBehind { get; }

    /// <summary>Plain sentence about what stayed at the original place.</summary>
    public string Note { get; }

    public PartialQuarantineException(QuarantineEntry entry, IReadOnlyList<string> leftBehind, string note)
        : base("A full copy is in quarantine. " + note)
    {
        Note = note;
        Entry = entry;
        LeftBehind = leftBehind;
    }
}

/// <summary>The quarantined copy no longer matches what was stored, so it wasn't restored automatically.</summary>
public sealed class QuarantineIntegrityException : IOException
{
    public QuarantineIntegrityException(string message) : base(message) { }
}
