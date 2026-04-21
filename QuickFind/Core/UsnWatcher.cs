using System.IO;
using System.Runtime.InteropServices;
using QuickFind.Helpers;
using static QuickFind.Helpers.NativeMethods;

namespace QuickFind.Core;

// Watches the NTFS USN change journal and applies incremental updates
// to the FileIndex. One watcher runs per drive in its own background
// thread, polling every few seconds. Requires admin (like MftIndexer).
//
// Cursor state — the latest processed USN per drive — is persisted to
// %LOCALAPPDATA%\QuickFind\usn.state so we can pick up from where we
// left off across app restarts. If the stored JournalID no longer
// matches (journal was recreated) we fall back to the journal's
// FirstUsn and skip ahead — missing changes will show up on the next
// full reindex.
public sealed class UsnWatcher : IDisposable
{
    private const int BUFFER_SIZE = 512 * 1024;
    private const int POLL_INTERVAL_MS = 5000;
    // Windows error codes for "no journal on this volume". When we see
    // these there's no point retrying — the journal simply isn't active
    // on the drive. Creating one requires fsutil and write access; not
    // our call.
    private const int ERROR_JOURNAL_DELETE_IN_PROGRESS = 1178;
    private const int ERROR_JOURNAL_NOT_ACTIVE = 1179;
    private const int ERROR_JOURNAL_ENTRY_DELETED = 1181;

    private readonly FileIndex _index;
    private readonly Action? _onChange;
    private readonly List<Thread> _threads = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _stateFile;

    // Per-drive cursor: JournalID + last processed NextUsn.
    private readonly Dictionary<string, (ulong JournalId, long NextUsn)> _cursor = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateLock = new();

    public UsnWatcher(FileIndex index, Action? onChange = null)
    {
        _index = index;
        _onChange = onChange;
        _stateFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuickFind", "usn.state");
        LoadState();
    }

    public void Start(IEnumerable<string> driveRoots)
    {
        foreach (var drive in driveRoots)
        {
            var d = drive;
            var t = new Thread(() => WatchLoop(d))
            {
                IsBackground = true,
                Name = $"QuickFind-USN-{d.TrimEnd('\\')}"
            };
            _threads.Add(t);
            t.Start();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var t in _threads)
        {
            try { t.Join(1000); } catch { }
        }
        SaveState();
        _cts.Dispose();
    }

    // ── Watch loop ────────────────────────────────────────────────────

    private void WatchLoop(string driveRoot)
    {
        string volumePath = @"\\.\" + driveRoot.TrimEnd('\\');
        Logger.Info($"UsnWatcher: starting on {driveRoot}");

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    using var handle = CreateFile(
                        volumePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

                    if (handle.IsInvalid)
                    {
                        Logger.Warn($"UsnWatcher: cannot open {volumePath} (err={Marshal.GetLastWin32Error()}), retrying later");
                        WaitOrCancel(30000);
                        continue;
                    }

                    if (!QueryJournal(handle, out var journal))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err == ERROR_JOURNAL_NOT_ACTIVE || err == ERROR_JOURNAL_DELETE_IN_PROGRESS)
                        {
                            Logger.Info($"UsnWatcher: {driveRoot} has no USN journal (err={err}); watcher will not run for this drive");
                            return; // Permanent — nothing we can do, no point looping.
                        }
                        Logger.Warn($"UsnWatcher: FSCTL_QUERY_USN_JOURNAL failed on {driveRoot} (err={err})");
                        WaitOrCancel(30000);
                        continue;
                    }

                    long startUsn = GetOrInitCursor(driveRoot, journal);

                    ReadJournalLoop(handle, driveRoot, journal.UsnJournalID, startUsn);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"UsnWatcher: {driveRoot} iteration failed", ex);
                    WaitOrCancel(10000);
                }
            }
        }
        finally
        {
            Logger.Info($"UsnWatcher: stopped on {driveRoot}");
        }
    }

    private static bool QueryJournal(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out USN_JOURNAL_DATA_V0 journal)
    {
        return DeviceIoControl(
            handle, FSCTL_QUERY_USN_JOURNAL,
            IntPtr.Zero, 0,
            out journal, Marshal.SizeOf<USN_JOURNAL_DATA_V0>(),
            out _, IntPtr.Zero);
    }

    private long GetOrInitCursor(string driveRoot, USN_JOURNAL_DATA_V0 journal)
    {
        lock (_stateLock)
        {
            if (_cursor.TryGetValue(driveRoot, out var existing) &&
                existing.JournalId == journal.UsnJournalID &&
                existing.NextUsn >= journal.FirstUsn)
            {
                return existing.NextUsn;
            }
            // Journal recreated or cursor too old — start from current tail.
            // We intentionally skip past historical changes; the initial
            // MFT index already captured current state. Only future changes
            // are tracked incrementally.
            _cursor[driveRoot] = (journal.UsnJournalID, journal.NextUsn);
            Logger.Info($"UsnWatcher: {driveRoot} starting cursor at NextUsn={journal.NextUsn} (journalId={journal.UsnJournalID})");
            return journal.NextUsn;
        }
    }

    private void ReadJournalLoop(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string driveRoot, ulong journalId, long startUsn)
    {
        IntPtr buffer = Marshal.AllocHGlobal(BUFFER_SIZE);
        try
        {
            long currentUsn = startUsn;

            while (!_cts.IsCancellationRequested)
            {
                var readData = new READ_USN_JOURNAL_DATA_V0
                {
                    StartUsn = currentUsn,
                    ReasonMask = USN_REASON_FILE_CREATE
                               | USN_REASON_FILE_DELETE
                               | USN_REASON_RENAME_NEW_NAME,
                    ReturnOnlyOnClose = 1, // only fully-finalised changes
                    Timeout = 0,
                    BytesToWaitFor = 0,
                    UsnJournalID = journalId
                };

                bool ok = DeviceIoControl(
                    handle, FSCTL_READ_USN_JOURNAL,
                    ref readData, Marshal.SizeOf<READ_USN_JOURNAL_DATA_V0>(),
                    buffer, BUFFER_SIZE,
                    out int bytesReturned, IntPtr.Zero);

                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    Logger.Warn($"UsnWatcher: FSCTL_READ_USN_JOURNAL {driveRoot} failed (err={err}) — restarting");
                    WaitOrCancel(POLL_INTERVAL_MS);
                    return; // bubble up to outer reconnect loop
                }

                if (bytesReturned <= 8)
                {
                    // No new records — persist cursor and sleep.
                    SaveCursor(driveRoot, journalId, currentUsn);
                    WaitOrCancel(POLL_INTERVAL_MS);
                    continue;
                }

                long nextUsn = Marshal.ReadInt64(buffer);
                int applied = ApplyRecords(buffer, 8, bytesReturned, driveRoot);
                currentUsn = nextUsn;

                if (applied > 0)
                {
                    _onChange?.Invoke();
                }
            }

            SaveCursor(driveRoot, journalId, currentUsn);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private int ApplyRecords(IntPtr buffer, int startOffset, int bytesReturned, string driveRoot)
    {
        int offset = startOffset;
        int applied = 0;

        while (offset + 60 <= bytesReturned)
        {
            uint recordLength = (uint)Marshal.ReadInt32(buffer, offset);
            if (recordLength == 0) break;

            // USN_RECORD_V2 layout (same field offsets as FSCTL_ENUM_USN_DATA):
            //   +0  DWORD  RecordLength
            //   +4  WORD   MajorVersion / +6 MinorVersion
            //   +8  QWORD  FileReferenceNumber
            //   +16 QWORD  ParentFileReferenceNumber
            //   +24 QWORD  Usn
            //   +32 FILETIME TimeStamp (8 bytes)
            //   +40 DWORD  Reason
            //   +44 DWORD  SourceInfo
            //   +48 DWORD  SecurityId
            //   +52 DWORD  FileAttributes
            //   +56 WORD   FileNameLength
            //   +58 WORD   FileNameOffset
            //   +60 WCHAR[] FileName
            ulong frn = (ulong)Marshal.ReadInt64(buffer, offset + 8) & FRN_MASK;
            ulong parentFrn = (ulong)Marshal.ReadInt64(buffer, offset + 16) & FRN_MASK;
            uint reason = (uint)Marshal.ReadInt32(buffer, offset + 40);
            uint attributes = (uint)Marshal.ReadInt32(buffer, offset + 52);
            ushort nameLength = (ushort)Marshal.ReadInt16(buffer, offset + 56);
            ushort nameOffset = (ushort)Marshal.ReadInt16(buffer, offset + 58);

            if (nameLength > 0 && offset + nameOffset + nameLength <= bytesReturned)
            {
                string fileName = Marshal.PtrToStringUni(
                    IntPtr.Add(buffer, offset + nameOffset), nameLength / 2) ?? string.Empty;

                bool isDir = (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;

                // DELETE wins over CREATE/RENAME if both are set in the
                // close record, because the final state is "gone".
                if ((reason & USN_REASON_FILE_DELETE) != 0)
                {
                    _index.UsnRemove(driveRoot, frn);
                    applied++;
                }
                else if ((reason & (USN_REASON_FILE_CREATE | USN_REASON_RENAME_NEW_NAME)) != 0)
                {
                    if (!ShouldSkip(fileName))
                    {
                        _index.UsnAddOrUpdate(driveRoot, frn, parentFrn, fileName, isDir);
                        applied++;
                    }
                }
            }

            offset += (int)recordLength;
        }

        return applied;
    }

    private static bool ShouldSkip(string name)
    {
        if (name.Length == 0) return true;
        // Skip NTFS metadata files — they shouldn't appear in the journal
        // with CLOSE, but be defensive.
        return name[0] == '$' && (
            name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("$MFT", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("$LogFile", StringComparison.OrdinalIgnoreCase));
    }

    private void WaitOrCancel(int ms)
    {
        try { _cts.Token.WaitHandle.WaitOne(ms); }
        catch { }
    }

    // ── Cursor persistence ─────────────────────────────────────────────

    private void SaveCursor(string driveRoot, ulong journalId, long nextUsn)
    {
        lock (_stateLock)
        {
            _cursor[driveRoot] = (journalId, nextUsn);
        }
    }

    public void SaveState()
    {
        lock (_stateLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_stateFile)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                using var fs = File.Create(_stateFile);
                using var writer = new BinaryWriter(fs);
                writer.Write((int)1); // state file version
                writer.Write(_cursor.Count);
                foreach (var (drive, state) in _cursor)
                {
                    writer.Write(drive);
                    writer.Write(state.JournalId);
                    writer.Write(state.NextUsn);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("UsnWatcher.SaveState failed", ex);
            }
        }
    }

    private void LoadState()
    {
        if (!File.Exists(_stateFile)) return;
        try
        {
            using var fs = File.OpenRead(_stateFile);
            using var reader = new BinaryReader(fs);
            int version = reader.ReadInt32();
            if (version != 1) return;
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string drive = reader.ReadString();
                ulong journalId = reader.ReadUInt64();
                long nextUsn = reader.ReadInt64();
                _cursor[drive] = (journalId, nextUsn);
            }
            Logger.Info($"UsnWatcher: loaded cursors for {count} drive(s)");
        }
        catch (Exception ex)
        {
            Logger.Warn("UsnWatcher.LoadState failed", ex);
        }
    }
}
