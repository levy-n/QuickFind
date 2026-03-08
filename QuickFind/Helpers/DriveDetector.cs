using System.IO;
using System.Text;

namespace QuickFind.Helpers;

internal static class DriveDetector
{
    public static List<DriveInfo> GetFixedNtfsDrives()
    {
        var results = new List<DriveInfo>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                continue;

            if (IsNtfs(drive.RootDirectory.FullName))
                results.Add(drive);
        }

        return results;
    }

    private static bool IsNtfs(string rootPath)
    {
        var fsName = new StringBuilder(64);
        bool ok = NativeMethods.GetVolumeInformation(
            rootPath, null, 0,
            out _, out _, out _,
            fsName, fsName.Capacity);

        return ok && fsName.ToString().Equals("NTFS", StringComparison.OrdinalIgnoreCase);
    }
}
