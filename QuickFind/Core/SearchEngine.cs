using System.IO;
using System.Text.RegularExpressions;

namespace QuickFind.Core;

public sealed class SearchEngine
{
    private readonly FileIndex _index;
    private CancellationTokenSource? _cts;
    private readonly object _lock = new();

    public SearchEngine(FileIndex index) => _index = index;

    public record SearchResult(int EntryIndex, string Name, bool IsDirectory, int Score, long Size = 0);

    public string? DriveFilter { get; set; }

    public Task<List<SearchResult>> SearchAsync(string query, bool searchContents, CancellationToken externalCt = default)
    {
        CancellationTokenSource newCts;

        lock (_lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            newCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            _cts = newCts;
        }

        var ct = newCts.Token;
        return Task.Run(() => Search(query, searchContents, ct), ct);
    }

    private List<SearchResult> Search(string query, bool searchContents, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<SearchResult>();

        // Parse size filters from query
        var (nameQuery, minSize, maxSize) = ParseSizeFilter(query);
        bool hasSizeFilter = minSize > 0 || maxSize < long.MaxValue;

        // Pure size search (no name filter)
        if (hasSizeFilter && string.IsNullOrWhiteSpace(nameQuery))
            return SearchBySize(minSize, maxSize, ct);

        var results = new List<SearchResult>();
        string trimmedQuery = nameQuery.Trim();

        // Streaming scan — no giant snapshot allocation. Scoring uses
        // OrdinalIgnoreCase comparisons so we avoid a ToLowerInvariant
        // allocation for every one of the millions of entries.
        _index.ScanForSearch(DriveFilter, ct, (idx, name, isDir, storedSize) =>
        {
            int score = ScoreName(name, trimmedQuery);
            if (score <= 0) return true;

            if (hasSizeFilter)
            {
                long size = storedSize > 0 ? storedSize : ResolveSizeSafe(idx);
                if (size >= minSize && size <= maxSize)
                    results.Add(new SearchResult(idx, name, isDir, score, size));
            }
            else
            {
                results.Add(new SearchResult(idx, name, isDir, score, storedSize));
            }
            return true;
        });

        // Content search (optional, only for text files)
        if (searchContents && !hasSizeFilter && results.Count < 50)
        {
            SearchContents(trimmedQuery, results, ct);
        }

        if (hasSizeFilter)
            results.Sort((a, b) => b.Size.CompareTo(a.Size)); // largest first
        else
            results.Sort((a, b) => b.Score.CompareTo(a.Score));

        if (results.Count > 100)
            results.RemoveRange(100, results.Count - 100);

        return results;
    }

    private List<SearchResult> SearchBySize(long minSize, long maxSize, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        var snapshot = _index.GetSnapshot(DriveFilter);

        // For stored sizes (fallback indexer), filter directly
        // For unknown sizes (MFT, size=0), resolve dynamically
        var lockObj = new object();

        Parallel.ForEach(snapshot,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            (item) =>
            {
                var (idx, name, isDir, storedSize) = item;
                if (isDir) return; // skip directories for size search

                long size = storedSize > 0 ? storedSize : ResolveSizeSafe(idx);
                if (size <= 0) return;

                if (size >= minSize && size <= maxSize)
                {
                    lock (lockObj)
                    {
                        results.Add(new SearchResult(idx, name, isDir, 50, size));
                    }
                }
            });

        results.Sort((a, b) => b.Size.CompareTo(a.Size));

        if (results.Count > 200)
            results.RemoveRange(200, results.Count - 200);

        return results;
    }

    private long ResolveSizeSafe(int entryIndex)
    {
        try
        {
            string fullPath = _index.ResolvePath(entryIndex);
            if (string.IsNullOrEmpty(fullPath)) return 0;
            var fi = new FileInfo(fullPath);
            if (!fi.Exists) return 0;
            long size = fi.Length;
            // Write the resolved size back into the index so the next
            // size query on this entry is O(1). This is the pragmatic
            // workaround for USN not carrying file sizes — size searches
            // stay slow on a cold cache but speed up on repeat usage.
            _index.TrySetEntrySize(entryIndex, size);
            return size;
        }
        catch { return 0; }
    }

    internal static (string nameQuery, long minSize, long maxSize) ParseSizeFilter(string query)
    {
        long minSize = 0;
        long maxSize = long.MaxValue;
        string remaining = query;

        var pattern = new Regex(@"([<>])\s*(\d+(?:\.\d+)?)\s*(KB|MB|GB|TB|B)?\b", RegexOptions.IgnoreCase);
        var matches = pattern.Matches(query);

        foreach (Match match in matches)
        {
            string op = match.Groups[1].Value;
            double value = double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            string unit = match.Groups[3].Success ? match.Groups[3].Value.ToUpperInvariant() : "B";

            long bytes = unit switch
            {
                "KB" => (long)(value * 1024),
                "MB" => (long)(value * 1024 * 1024),
                "GB" => (long)(value * 1024L * 1024 * 1024),
                "TB" => (long)(value * 1024L * 1024 * 1024 * 1024),
                _ => (long)value
            };

            if (op == ">") minSize = bytes;
            else if (op == "<") maxSize = bytes;

            remaining = remaining.Replace(match.Value, "").Trim();
        }

        return (remaining, minSize, maxSize);
    }

    internal static int ScoreName(string name, string query)
    {
        // OrdinalIgnoreCase avoids allocating a lowercased copy of every
        // filename on every keystroke — a hot-path hit when scoring 3M+
        // entries. Ordinal is right for file names (no locale folding).

        if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase))
            return 100;

        // Exact match without extension (e.g. "readme" matches "readme.md")
        int dotIdx = name.LastIndexOf('.');
        if (dotIdx > 0 &&
            name.Length - dotIdx <= 8 && // sanity: real extensions are short
            MemoryExtensions.Equals(
                name.AsSpan(0, dotIdx), query.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return 95;

        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 80;

        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 60;

        return 0;
    }

    private void SearchContents(string query, List<SearchResult> results, CancellationToken ct)
    {
        var textExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".cs", ".js", ".ts", ".json", ".xml", ".yaml", ".yml",
            ".html", ".css", ".py", ".java", ".cpp", ".c", ".h", ".log", ".cfg",
            ".ini", ".csv", ".sql", ".sh", ".bat", ".ps1", ".config"
        };

        var existingIndices = new HashSet<int>(results.Select(r => r.EntryIndex));
        // SearchBySize-style content scan still benefits from a snapshot
        // because Parallel.ForEach partitions work across threads.
        var snapshot = _index.GetSnapshot(DriveFilter);

        Parallel.ForEach(snapshot,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            (item) =>
            {
                var (idx, name, isDir, _) = item;
                if (isDir || existingIndices.Contains(idx)) return;

                string ext = Path.GetExtension(name);
                if (!textExtensions.Contains(ext)) return;

                try
                {
                    string fullPath = _index.ResolvePath(idx);
                    if (string.IsNullOrEmpty(fullPath)) return;
                    var fi = new FileInfo(fullPath);
                    if (!fi.Exists || fi.Length > 10 * 1024 * 1024) return;

                    // UTF-8 with BOM detection — falls through to UTF-8 assumption
                    // for BOM-less files. This correctly handles Hebrew / Cyrillic /
                    // CJK content, which the default (ANSI) encoding would mangle.
                    using var reader = new StreamReader(fullPath,
                        System.Text.Encoding.UTF8,
                        detectEncodingFromByteOrderMarks: true);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (line.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            lock (results)
                            {
                                results.Add(new SearchResult(idx, name, isDir, 40));
                            }
                            return;
                        }
                    }
                }
                catch { /* file inaccessible */ }
            });
    }
}
