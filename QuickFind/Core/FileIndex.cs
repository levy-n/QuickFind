using System.IO;

namespace QuickFind.Core;

public sealed class FileIndex
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<ulong, int>> _frnMaps = new();
    private readonly List<FileEntry> _entries = new();
    private long _generation;

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    // Incremented every time the index is cleared. Callers holding a snapshot
    // can compare against this to detect that their indices are now stale.
    public long Generation
    {
        get { lock (_lock) return _generation; }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _frnMaps.Clear();
            _generation++;
        }
    }

    public void AddMftEntry(string driveRoot, ulong frn, ulong parentFrn, string name, bool isDirectory, long size = 0)
    {
        lock (_lock)
        {
            int idx = _entries.Count;
            _entries.Add(new FileEntry(name, driveRoot, parentFrn, isDirectory, null, size));

            if (!_frnMaps.TryGetValue(driveRoot, out var map))
            {
                map = new Dictionary<ulong, int>();
                _frnMaps[driveRoot] = map;
            }
            map[frn] = idx;
        }
    }

    public void AddFallbackEntry(string name, string directory, bool isDirectory, long size = 0)
    {
        lock (_lock)
        {
            _entries.Add(new FileEntry(name, "", 0, isDirectory, directory, size));
        }
    }

    public string ResolvePath(int entryIndex)
    {
        lock (_lock)
        {
            if ((uint)entryIndex >= (uint)_entries.Count)
                return string.Empty; // stale index (e.g. from pre-Clear snapshot)

            var entry = _entries[entryIndex];

            // Fallback entry: path already resolved
            if (entry.CachedDirectory != null)
                return Path.Combine(entry.CachedDirectory, entry.Name);

            // MFT entry: walk parent chain
            var parts = new Stack<string>();
            int currentIdx = entryIndex;
            var current = entry;
            int depth = 0;

            while (depth++ < 256)
            {
                // Skip "." — the NTFS root directory's own name, which would
                // otherwise produce paths like "C:\.\Users\..."
                if (current.Name != "." && current.Name != "..")
                    parts.Push(current.Name);

                if (!_frnMaps.TryGetValue(current.DriveRoot, out var map))
                    break;
                if (!map.TryGetValue(current.ParentFrn, out int parentIdx))
                    break;
                if (parentIdx == currentIdx)
                    break; // root self-reference
                if ((uint)parentIdx >= (uint)_entries.Count)
                    break;

                currentIdx = parentIdx;
                current = _entries[parentIdx];
            }

            return Path.Combine(current.DriveRoot, Path.Combine(parts.ToArray()));
        }
    }

    public string ResolveDirectory(int entryIndex)
    {
        lock (_lock)
        {
            if ((uint)entryIndex >= (uint)_entries.Count)
                return string.Empty;

            var entry = _entries[entryIndex];
            if (entry.CachedDirectory != null)
                return entry.CachedDirectory;

            string fullPath = ResolvePath(entryIndex);
            return Path.GetDirectoryName(fullPath) ?? entry.DriveRoot;
        }
    }

    public void EnumerateForSearch(Action<int, string, bool> callback)
    {
        lock (_lock)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                callback(i, e.Name, e.IsDirectory);
            }
        }
    }

    public List<(int Index, string Name, bool IsDirectory, long Size)> GetSnapshot(string? driveFilter = null)
    {
        lock (_lock)
        {
            var result = new List<(int, string, bool, long)>(_entries.Count);
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (driveFilter != null)
                {
                    // MFT entries have DriveRoot, fallback entries have CachedDirectory
                    string root = e.DriveRoot.Length > 0 ? e.DriveRoot : e.CachedDirectory ?? "";
                    if (!root.StartsWith(driveFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                result.Add((i, e.Name, e.IsDirectory, e.Size));
            }
            return result;
        }
    }

    public List<string> GetIndexedDrives()
    {
        lock (_lock)
        {
            var drives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dr in _frnMaps.Keys)
                drives.Add(dr);
            // Also check fallback entries
            foreach (var e in _entries)
            {
                if (e.CachedDirectory != null && e.CachedDirectory.Length >= 3)
                    drives.Add(e.CachedDirectory[..3]); // e.g. "C:\"
            }
            return drives.OrderBy(d => d).ToList();
        }
    }

    public long GetEntrySize(int entryIndex)
    {
        lock (_lock)
        {
            if ((uint)entryIndex >= (uint)_entries.Count)
                return 0;
            return _entries[entryIndex].Size;
        }
    }

    // Binary persistence
    public void SaveTo(BinaryWriter writer)
    {
        lock (_lock)
        {
            writer.Write(_entries.Count);
            writer.Write(_frnMaps.Count);

            foreach (var (driveRoot, map) in _frnMaps)
            {
                writer.Write(driveRoot);
                writer.Write(map.Count);
                foreach (var (frn, idx) in map)
                {
                    writer.Write(frn);
                    writer.Write(idx);
                }
            }

            foreach (var entry in _entries)
            {
                writer.Write(entry.Name);
                writer.Write(entry.DriveRoot);
                writer.Write(entry.ParentFrn);
                writer.Write(entry.IsDirectory);
                writer.Write(entry.CachedDirectory ?? "");
                writer.Write(entry.Size);
            }
        }
    }

    public void LoadFrom(BinaryReader reader, int version = 2)
    {
        lock (_lock)
        {
            _entries.Clear();
            _frnMaps.Clear();

            int entryCount = reader.ReadInt32();
            int driveCount = reader.ReadInt32();

            for (int d = 0; d < driveCount; d++)
            {
                string driveRoot = reader.ReadString();
                int mapCount = reader.ReadInt32();
                var map = new Dictionary<ulong, int>(mapCount);
                for (int m = 0; m < mapCount; m++)
                {
                    ulong frn = reader.ReadUInt64();
                    int idx = reader.ReadInt32();
                    map[frn] = idx;
                }
                _frnMaps[driveRoot] = map;
            }

            _entries.Capacity = entryCount;
            for (int i = 0; i < entryCount; i++)
            {
                string name = reader.ReadString();
                string driveRoot = reader.ReadString();
                ulong parentFrn = reader.ReadUInt64();
                bool isDir = reader.ReadBoolean();
                string cached = reader.ReadString();
                long size = version >= 2 ? reader.ReadInt64() : 0;
                _entries.Add(new FileEntry(name, driveRoot, parentFrn, isDir, cached.Length > 0 ? cached : null, size));
            }
        }
    }
}

internal record FileEntry(string Name, string DriveRoot, ulong ParentFrn, bool IsDirectory, string? CachedDirectory, long Size);
