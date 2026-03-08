using System.Runtime.InteropServices;
using QuickFind.Helpers;
using static QuickFind.Helpers.NativeMethods;

namespace QuickFind.Core;

public static class MftIndexer
{
    private const int BUFFER_SIZE = 1024 * 1024; // 1MB buffer for fast throughput

    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", "$MFT", "$MFTMirr",
        "$LogFile", "$Volume", "$AttrDef", "$Bitmap", "$Boot",
        "$BadClus", "$Secure", "$UpCase", "$Extend"
    };

    public static bool TryIndex(FileIndex index, string driveRoot,
        IProgress<string>? progress, CancellationToken ct)
    {
        string volumePath = @"\\.\" + driveRoot.TrimEnd('\\');

        progress?.Report($"MFT: Opening {volumePath}...");

        using var handle = CreateFile(
            volumePath,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            progress?.Report($"MFT: Cannot open {volumePath} (error {err}) — falling back");
            return false;
        }

        var enumData = new MFT_ENUM_DATA_V0
        {
            StartFileReferenceNumber = 0,
            LowUsn = 0,
            HighUsn = long.MaxValue
        };

        IntPtr buffer = Marshal.AllocHGlobal(BUFFER_SIZE);
        try
        {
            int fileCount = 0;
            progress?.Report($"MFT: Reading {driveRoot}...");

            while (!ct.IsCancellationRequested)
            {
                bool success = DeviceIoControl(
                    handle,
                    FSCTL_ENUM_USN_DATA,
                    ref enumData,
                    Marshal.SizeOf<MFT_ENUM_DATA_V0>(),
                    buffer,
                    BUFFER_SIZE,
                    out int bytesReturned,
                    IntPtr.Zero);

                if (!success || bytesReturned <= 8)
                    break;

                // First 8 bytes: next StartFileReferenceNumber
                enumData.StartFileReferenceNumber = (ulong)Marshal.ReadInt64(buffer);

                int offset = 8;
                while (offset + 60 <= bytesReturned)
                {
                    uint recordLength = (uint)Marshal.ReadInt32(buffer, offset);
                    if (recordLength == 0) break;

                    ulong frn = (ulong)Marshal.ReadInt64(buffer, offset + 8) & FRN_MASK;
                    ulong parentFrn = (ulong)Marshal.ReadInt64(buffer, offset + 16) & FRN_MASK;
                    uint attributes = (uint)Marshal.ReadInt32(buffer, offset + 52);
                    ushort nameLength = (ushort)Marshal.ReadInt16(buffer, offset + 56);
                    ushort nameOffset = (ushort)Marshal.ReadInt16(buffer, offset + 58);

                    if (nameLength > 0 && offset + nameOffset + nameLength <= bytesReturned)
                    {
                        string fileName = Marshal.PtrToStringUni(
                            IntPtr.Add(buffer, offset + nameOffset), nameLength / 2)!;

                        bool isDir = (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;

                        if (!ShouldSkip(fileName))
                        {
                            index.AddMftEntry(driveRoot, frn, parentFrn, fileName, isDir);
                            fileCount++;

                            if (fileCount % 200_000 == 0)
                                progress?.Report($"MFT {driveRoot} {fileCount:N0} files...");
                        }
                    }

                    offset += (int)recordLength;
                }
            }

            progress?.Report($"MFT {driveRoot} done — {fileCount:N0} files");
            return fileCount > 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool ShouldSkip(string name)
    {
        if (name.Length == 0) return true;
        if (name[0] == '$') return SkipNames.Contains(name);
        return false;
    }
}
