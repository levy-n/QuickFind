using System.IO;
using System.IO.Compression;

namespace QuickFind.Core;

public static class IndexPersistence
{
    // File versions:
    //   1 = uncompressed, no file size field
    //   2 = uncompressed, adds file size
    //   3 = GZip-compressed body, adds file size
    // Readers auto-detect compression by checking the first bytes after the
    // version header, so older index files continue to load transparently.
    private const int FILE_VERSION = 3;

    private static string GetIndexPath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuickFind");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "index.dat");
    }

    private static string GetMetaPath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuickFind");
        return Path.Combine(dir, "index.meta");
    }

    public static bool TryLoad(FileIndex index)
    {
        string path = GetIndexPath();
        if (!File.Exists(path)) return false;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);

            // Read version header as raw bytes — we construct the body reader
            // separately so we don't have two BinaryReaders sharing one stream.
            Span<byte> header = stackalloc byte[4];
            if (fs.Read(header) != 4)
            {
                Logger.Warn($"IndexPersistence.TryLoad: truncated header");
                return false;
            }
            int version = BitConverter.ToInt32(header);
            if (version < 1 || version > FILE_VERSION)
            {
                Logger.Warn($"IndexPersistence.TryLoad: unsupported version {version}");
                return false;
            }

            // v3+ = body is GZip-compressed; v1/v2 = raw.
            // BufferedStream wrapping GZipStream is critical: BinaryReader
            // performs millions of tiny reads (ReadString, ReadInt32, …)
            // and GZipStream handles each one as a separate decompression
            // call. Without a 64 KB buffer in front, loading a 3 M-entry
            // index takes minutes instead of seconds.
            if (version >= 3)
            {
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                using var buffered = new BufferedStream(gz, 64 * 1024);
                using var reader = new BinaryReader(buffered);
                index.LoadFrom(reader, version);
            }
            else
            {
                using var reader = new BinaryReader(fs);
                index.LoadFrom(reader, version);
            }

            Logger.Info($"IndexPersistence.TryLoad: loaded {index.Count:N0} entries (v{version}) from {path}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"IndexPersistence.TryLoad failed for {path}", ex);
            return false;
        }
    }

    public static void Save(FileIndex index)
    {
        string path = GetIndexPath();
        string metaPath = GetMetaPath();
        string tempPath = path + ".tmp";

        try
        {
            // Write to a temp file first, then atomically rename. Prevents
            // a corrupt index from being left behind if we crash mid-write.
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
            {
                // Version header as raw bytes (4 bytes, little-endian)
                Span<byte> header = stackalloc byte[4];
                BitConverter.TryWriteBytes(header, FILE_VERSION);
                fs.Write(header);

                using var gz = new GZipStream(fs, CompressionLevel.Fastest);
                using var buffered = new BufferedStream(gz, 64 * 1024);
                using var writer = new BinaryWriter(buffered);
                index.SaveTo(writer);
            }

            if (File.Exists(path)) File.Delete(path);
            File.Move(tempPath, path);

            File.WriteAllText(metaPath, DateTime.Now.ToString("o"));
            var info = new FileInfo(path);
            Logger.Info($"IndexPersistence.Save: wrote {index.Count:N0} entries to {path} ({info.Length / (1024.0 * 1024):F1} MB compressed)");
        }
        catch (Exception ex)
        {
            Logger.Warn($"IndexPersistence.Save failed for {path}", ex);
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    public static DateTime? GetLastIndexDate()
    {
        string metaPath = GetMetaPath();
        if (!File.Exists(metaPath)) return null;

        try
        {
            string text = File.ReadAllText(metaPath).Trim();
            return DateTime.Parse(text);
        }
        catch { return null; }
    }
}
