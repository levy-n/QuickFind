using QuickFind.Core;

namespace QuickFind.Tests;

public class FileIndexTests
{
    [Fact]
    public void Empty_Index_HasZeroCount()
    {
        var idx = new FileIndex();
        Assert.Equal(0, idx.Count);
    }

    [Fact]
    public void AddMftEntry_IncrementsCount()
    {
        var idx = new FileIndex();
        idx.AddMftEntry(@"C:\", frn: 1, parentFrn: 0, name: "foo.txt", isDirectory: false);
        Assert.Equal(1, idx.Count);
    }

    [Fact]
    public void AddFallbackEntry_StoresDirectory()
    {
        var idx = new FileIndex();
        idx.AddFallbackEntry("foo.txt", @"C:\Users\Test", isDirectory: false, size: 42);
        Assert.Equal(1, idx.Count);
        // Fallback entries resolve directly from the cached directory.
        Assert.Equal(@"C:\Users\Test\foo.txt", idx.ResolvePath(0));
    }

    [Fact]
    public void ResolvePath_OutOfRange_ReturnsEmpty()
    {
        // Bounds-check safety so pre-Clear snapshots can't crash us.
        var idx = new FileIndex();
        Assert.Equal(string.Empty, idx.ResolvePath(9999));
        Assert.Equal(string.Empty, idx.ResolvePath(-1));
    }

    [Fact]
    public void ResolveDirectory_OutOfRange_ReturnsEmpty()
    {
        var idx = new FileIndex();
        Assert.Equal(string.Empty, idx.ResolveDirectory(9999));
    }

    [Fact]
    public void GetEntrySize_OutOfRange_ReturnsZero()
    {
        var idx = new FileIndex();
        Assert.Equal(0L, idx.GetEntrySize(9999));
    }

    [Fact]
    public void Clear_IncrementsGeneration()
    {
        var idx = new FileIndex();
        long before = idx.Generation;
        idx.AddFallbackEntry("foo", @"C:\", false);
        idx.Clear();
        Assert.True(idx.Generation > before);
        Assert.Equal(0, idx.Count);
    }

    [Fact]
    public void MftEntry_PathResolutionWalksParentChain()
    {
        var idx = new FileIndex();
        // Root entry — self-referential parent, name "." (should be skipped
        // so we don't produce "C:\.\Users\..."). FRN 5 is the NTFS root.
        idx.AddMftEntry(@"C:\", frn: 5, parentFrn: 5, name: ".", isDirectory: true);
        idx.AddMftEntry(@"C:\", frn: 100, parentFrn: 5, name: "Users", isDirectory: true);
        idx.AddMftEntry(@"C:\", frn: 101, parentFrn: 100, name: "Nati", isDirectory: true);
        idx.AddMftEntry(@"C:\", frn: 102, parentFrn: 101, name: "readme.md", isDirectory: false);

        string path = idx.ResolvePath(3);
        Assert.Equal(@"C:\Users\Nati\readme.md", path);
    }

    [Fact]
    public void UsnRemove_TombstonesEntryAndExcludesFromScan()
    {
        var idx = new FileIndex();
        idx.AddMftEntry(@"C:\", frn: 10, parentFrn: 5, name: "dying.txt", isDirectory: false);
        Assert.Equal(1, idx.Count);

        idx.UsnRemove(@"C:\", frn: 10);

        // Count is unchanged (tombstoned, not removed), but the entry must
        // not appear in a search scan.
        Assert.Equal(1, idx.Count);

        int seen = 0;
        idx.ScanForSearch(null, default, (i, name, isDir, size) =>
        {
            seen++;
            return true;
        });
        Assert.Equal(0, seen);
    }

    [Fact]
    public void UsnAddOrUpdate_NewEntry_Appends()
    {
        var idx = new FileIndex();
        idx.UsnAddOrUpdate(@"C:\", frn: 20, parentFrn: 5, name: "new.txt", isDirectory: false);
        Assert.Equal(1, idx.Count);
    }

    [Fact]
    public void UsnAddOrUpdate_ExistingFrn_OverwritesName()
    {
        var idx = new FileIndex();
        idx.AddMftEntry(@"C:\", frn: 30, parentFrn: 5, name: "old.txt", isDirectory: false);
        idx.UsnAddOrUpdate(@"C:\", frn: 30, parentFrn: 5, name: "renamed.txt", isDirectory: false);
        Assert.Equal(1, idx.Count);

        string? foundName = null;
        idx.ScanForSearch(null, default, (i, name, isDir, size) =>
        {
            foundName = name;
            return true;
        });
        Assert.Equal("renamed.txt", foundName);
    }

    [Fact]
    public void RemoveByEntryIndex_TombstonesAndDropsFrnMap()
    {
        var idx = new FileIndex();
        idx.AddMftEntry(@"C:\", frn: 40, parentFrn: 5, name: "soon-gone.txt", isDirectory: false);
        idx.RemoveByEntryIndex(0);

        int seen = 0;
        idx.ScanForSearch(null, default, (i, name, isDir, size) =>
        {
            seen++;
            return true;
        });
        Assert.Equal(0, seen);

        // A future USN-add with the same FRN should re-insert cleanly
        // rather than reviving the tombstone.
        idx.UsnAddOrUpdate(@"C:\", frn: 40, parentFrn: 5, name: "recreated.txt", isDirectory: false);
        Assert.Equal(2, idx.Count); // tombstone + new
    }

    [Fact]
    public void DriveFilter_OnlyMatchesRequestedDrive()
    {
        var idx = new FileIndex();
        idx.AddFallbackEntry("on-c.txt", @"C:\Users", false);
        idx.AddFallbackEntry("on-d.txt", @"D:\Data", false);

        var names = new List<string>();
        idx.ScanForSearch(@"C:\", default, (i, name, isDir, size) =>
        {
            names.Add(name);
            return true;
        });

        Assert.Single(names);
        Assert.Equal("on-c.txt", names[0]);
    }
}
