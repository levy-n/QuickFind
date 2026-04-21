using System.IO;
using System.IO.Compression;
using QuickFind.Core;

namespace QuickFind.Tests;

// Exercises the SaveTo/LoadFrom round-trip on FileIndex directly —
// IndexPersistence static routes to %LOCALAPPDATA% which isn't ideal to
// touch from tests. The GZip format is tested with a stream round-trip.
public class IndexPersistenceTests
{
    [Fact]
    public void SaveLoad_RoundTrip_PreservesAllEntries()
    {
        var original = new FileIndex();
        original.AddMftEntry(@"C:\", frn: 5, parentFrn: 5, name: ".", isDirectory: true);
        original.AddMftEntry(@"C:\", frn: 10, parentFrn: 5, name: "Users", isDirectory: true);
        original.AddMftEntry(@"C:\", frn: 11, parentFrn: 10, name: "readme.md", isDirectory: false);
        original.AddFallbackEntry("local.txt", @"D:\Data", isDirectory: false, size: 1234);

        byte[] payload;
        using (var ms = new MemoryStream())
        {
            using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                original.SaveTo(writer);
            }
            payload = ms.ToArray();
        }

        var loaded = new FileIndex();
        using (var ms = new MemoryStream(payload))
        using (var reader = new BinaryReader(ms))
        {
            loaded.LoadFrom(reader, version: 2);
        }

        Assert.Equal(original.Count, loaded.Count);

        // Names preserved and MFT parent chain still resolves.
        Assert.Equal(@"C:\Users\readme.md", loaded.ResolvePath(2));
        Assert.Equal(@"D:\Data\local.txt", loaded.ResolvePath(3));
        Assert.Equal(1234, loaded.GetEntrySize(3));
    }

    [Fact]
    public void GZipRoundTrip_WithBufferedStream_LoadsIdentically()
    {
        // Mirrors the real IndexPersistence v3 format: GZip body wrapped
        // in BufferedStream (the perf fix from earlier this sprint).
        var original = new FileIndex();
        for (int i = 0; i < 500; i++)
            original.AddFallbackEntry($"file{i}.txt", @"C:\Data", false, i * 17);

        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var buffered = new BufferedStream(gz, 64 * 1024))
        using (var writer = new BinaryWriter(buffered))
        {
            original.SaveTo(writer);
        }

        ms.Position = 0;
        var loaded = new FileIndex();
        using (var gz = new GZipStream(ms, CompressionMode.Decompress))
        using (var buffered = new BufferedStream(gz, 64 * 1024))
        using (var reader = new BinaryReader(buffered))
        {
            loaded.LoadFrom(reader, version: 2);
        }

        Assert.Equal(500, loaded.Count);
        Assert.Equal(@"C:\Data\file42.txt", loaded.ResolvePath(42));
        Assert.Equal(42 * 17, loaded.GetEntrySize(42));
    }
}
