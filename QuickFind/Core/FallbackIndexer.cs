using System.IO;

namespace QuickFind.Core;

public static class FallbackIndexer
{
    public static void Index(FileIndex index, string driveRoot,
        IProgress<string>? progress, CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        int count = 0;
        progress?.Report($"Indexing {driveRoot} (standard mode)...");

        // Enumerate directories first (fast, no I/O per entry)
        try
        {
            foreach (var path in Directory.EnumerateDirectories(driveRoot, "*", options))
            {
                ct.ThrowIfCancellationRequested();

                string name = Path.GetFileName(path);
                if (string.IsNullOrEmpty(name)) continue;

                string? dir = Path.GetDirectoryName(path);
                if (dir == null) continue;

                if (IsSystemFolder(name)) continue;

                index.AddFallbackEntry(name, dir, isDirectory: true, size: 0);
                count++;

                if (count % 50_000 == 0)
                    progress?.Report($"Indexing {driveRoot}... {count:N0} entries");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* drive error */ }

        // Enumerate files (no GetAttributes or FileInfo — just names)
        try
        {
            foreach (var path in Directory.EnumerateFiles(driveRoot, "*", options))
            {
                ct.ThrowIfCancellationRequested();

                string name = Path.GetFileName(path);
                if (string.IsNullOrEmpty(name)) continue;

                string? dir = Path.GetDirectoryName(path);
                if (dir == null) continue;

                if (IsSystemPath(dir)) continue;

                index.AddFallbackEntry(name, dir, isDirectory: false, size: 0);
                count++;

                if (count % 50_000 == 0)
                    progress?.Report($"Indexing {driveRoot}... {count:N0} entries");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* drive error */ }

        progress?.Report($"Indexed {driveRoot} — {count:N0} entries");
    }

    private static bool IsSystemFolder(string name)
    {
        return name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSystemPath(string dir)
    {
        return dir.Contains("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
               dir.Contains("System Volume Information", StringComparison.OrdinalIgnoreCase);
    }
}
