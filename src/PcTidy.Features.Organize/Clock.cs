namespace PcTidy.Features.Organize;

/// <summary>Current time, injectable so quiet periods and ages can be tested deterministically.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTime UtcNow => DateTime.UtcNow;
}
