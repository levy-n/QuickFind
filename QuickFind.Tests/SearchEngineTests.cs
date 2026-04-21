using QuickFind.Core;

namespace QuickFind.Tests;

public class SearchEngineTests
{
    // ── ScoreName ──────────────────────────────────────────────────────

    [Fact]
    public void ScoreName_ExactMatch_Returns100()
    {
        Assert.Equal(100, SearchEngine.ScoreName("readme", "readme"));
    }

    [Fact]
    public void ScoreName_IsCaseInsensitive()
    {
        Assert.Equal(100, SearchEngine.ScoreName("README", "readme"));
        Assert.Equal(100, SearchEngine.ScoreName("readme", "README"));
    }

    [Fact]
    public void ScoreName_ExactMatchWithoutExtension_Returns95()
    {
        Assert.Equal(95, SearchEngine.ScoreName("readme.md", "readme"));
        Assert.Equal(95, SearchEngine.ScoreName("config.json", "config"));
    }

    [Fact]
    public void ScoreName_StartsWith_Returns80()
    {
        Assert.Equal(80, SearchEngine.ScoreName("readme_old.md", "readme"));
        Assert.Equal(80, SearchEngine.ScoreName("ConfigManager.cs", "config"));
    }

    [Fact]
    public void ScoreName_Contains_Returns60()
    {
        Assert.Equal(60, SearchEngine.ScoreName("old-readme-v2.md", "readme"));
    }

    [Fact]
    public void ScoreName_NoMatch_Returns0()
    {
        Assert.Equal(0, SearchEngine.ScoreName("hello.txt", "world"));
    }

    [Fact]
    public void ScoreName_LongExtensionDoesNotTriggerRule95()
    {
        // A 10-char "extension" is almost certainly a match inside the
        // filename body, not a real file extension — should score lower
        // than exact-minus-ext (95) so "foo" vs "foo.verylongextension"
        // falls through to 80 (starts-with) rather than 95.
        int score = SearchEngine.ScoreName("foo.verylongextension", "foo");
        Assert.Equal(80, score);
    }

    // ── ParseSizeFilter ────────────────────────────────────────────────

    [Fact]
    public void ParseSizeFilter_NoFilter_ReturnsDefaults()
    {
        var (name, min, max) = SearchEngine.ParseSizeFilter("readme");
        Assert.Equal("readme", name);
        Assert.Equal(0L, min);
        Assert.Equal(long.MaxValue, max);
    }

    [Theory]
    [InlineData(">100KB", 100L * 1024)]
    [InlineData(">100MB", 100L * 1024 * 1024)]
    [InlineData(">1GB", 1L * 1024 * 1024 * 1024)]
    [InlineData(">2TB", 2L * 1024 * 1024 * 1024 * 1024)]
    public void ParseSizeFilter_ParsesMinSizeUnits(string query, long expectedMin)
    {
        var (_, min, _) = SearchEngine.ParseSizeFilter(query);
        Assert.Equal(expectedMin, min);
    }

    [Fact]
    public void ParseSizeFilter_ParsesMaxSize()
    {
        var (_, _, max) = SearchEngine.ParseSizeFilter("<500MB");
        Assert.Equal(500L * 1024 * 1024, max);
    }

    [Fact]
    public void ParseSizeFilter_CombinesNameAndSize()
    {
        var (name, min, _) = SearchEngine.ParseSizeFilter("video >100MB");
        Assert.Equal("video", name.Trim());
        Assert.Equal(100L * 1024 * 1024, min);
    }

    [Fact]
    public void ParseSizeFilter_IsCaseInsensitiveOnUnits()
    {
        var (_, min, _) = SearchEngine.ParseSizeFilter(">50mb");
        Assert.Equal(50L * 1024 * 1024, min);
    }

    [Fact]
    public void ParseSizeFilter_SupportsDecimalValues()
    {
        var (_, min, _) = SearchEngine.ParseSizeFilter(">1.5GB");
        Assert.Equal((long)(1.5 * 1024 * 1024 * 1024), min);
    }
}
