using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PcTidy.Engine.IO;

internal enum MediaKind { Unknown, Hdd, Ssd }

/// <summary>
/// Is a file on a spinning disk or an SSD? Read-only query of the volume's device (IOCTL_STORAGE_QUERY_PROPERTY,
/// StorageDeviceSeekPenaltyProperty): a seek penalty means a hard drive. Overwriting a file in place reliably destroys
/// its old data only on a hard drive; an SSD remaps writes, so shredding there is best-effort.
/// Anything that can't be asked (network, mounted folder, storage spaces, no access) is Unknown.
/// </summary>
internal static class StorageMedia
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDeviceSeekPenaltyProperty = 7;

    public static MediaKind Of(string path)
    {
        try
        {
            var volume = new StringBuilder(300);
            if (!NativeMethods.GetVolumePathName(LongPath.Io(path), volume, volume.Capacity)) return MediaKind.Unknown;
            var root = LongPath.Display(volume.ToString()).TrimEnd('\\');
            if (root.Length != 2 || root[1] != ':' || !char.IsAsciiLetter(root[0])) return MediaKind.Unknown;
            using var device = NativeMethods.CreateFile(@"\\.\" + root, 0, NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                IntPtr.Zero, NativeMethods.OpenExisting, 0, IntPtr.Zero);
            return device.IsInvalid ? MediaKind.Unknown : Query(device);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return MediaKind.Unknown; }
    }

    private static MediaKind Query(SafeFileHandle device)
    {
        var query = new byte[12]; // STORAGE_PROPERTY_QUERY: PropertyId, QueryType = PropertyStandardQuery (0), AdditionalParameters
        BitConverter.GetBytes(StorageDeviceSeekPenaltyProperty).CopyTo(query, 0);
        var result = new byte[12]; // DEVICE_SEEK_PENALTY_DESCRIPTOR: Version, Size, IncursSeekPenalty (BOOLEAN)
        if (!NativeMethods.DeviceIoControl(device, IoctlStorageQueryProperty, query, query.Length, result, result.Length, out var got, IntPtr.Zero) || got < 9)
            return MediaKind.Unknown;
        return result[8] != 0 ? MediaKind.Hdd : MediaKind.Ssd;
    }

    /// <summary>For the step message ("" for a hard drive, where overwriting works as intended).</summary>
    public static string Note(MediaKind kind) => kind switch
    {
        MediaKind.Hdd => "",
        MediaKind.Ssd => " It's on an SSD, which can keep old copies of data internally, so shredding there is best-effort.",
        _ => " PC Tidy couldn't tell what kind of drive it's on, so shredding is best-effort.",
    };
}
