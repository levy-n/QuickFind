using System.IO;

namespace QuickFind.Core;

public static class IndexPersistence
{
    private const int FILE_VERSION = 2;

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
            using var reader = new BinaryReader(fs);

            int version = reader.ReadInt32();
            if (version < 1 || version > FILE_VERSION) return false;

            index.LoadFrom(reader, version);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Save(FileIndex index)
    {
        string path = GetIndexPath();
        string metaPath = GetMetaPath();

        try
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
            using var writer = new BinaryWriter(fs);

            writer.Write(FILE_VERSION);
            index.SaveTo(writer);

            // Write metadata
            File.WriteAllText(metaPath, DateTime.Now.ToString("o"));
        }
        catch { /* best effort */ }
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
