using Gpm.Core.Import;

namespace Gpm.Core.Tests;

public class CsvMappingTests
{
    [Fact]
    public void Parse_reads_mappings_after_header()
    {
        var map = CsvMapping.Parse(
        [
            "source,target",
            "org-a/repo-1,org-b/repo-1",
            "org-a/repo-2,org-b/renamed",
        ]);

        Assert.Equal(2, map.Count);
        Assert.Equal("org-b/repo-1", map["org-a/repo-1"]);
        Assert.Equal("org-b/renamed", map["org-a/repo-2"]);
    }

    [Fact]
    public void Parse_trims_whitespace_and_skips_empty_lines()
    {
        var map = CsvMapping.Parse(
        [
            "  Source , Target ",
            "",
            "  alice , bob ",
            "   ",
        ]);

        var pair = Assert.Single(map);
        Assert.Equal("alice", pair.Key);
        Assert.Equal("bob", pair.Value);
    }

    [Fact]
    public void Parse_lookup_is_case_insensitive()
    {
        var map = CsvMapping.Parse(["source,target", "Org-A/Repo,org-b/repo"]);

        Assert.True(map.ContainsKey("org-a/repo"));
        Assert.Equal("org-b/repo", map["ORG-A/REPO"]);
    }

    [Fact]
    public void Parse_last_duplicate_wins()
    {
        var map = CsvMapping.Parse(["source,target", "a,b", "a,c"]);

        Assert.Equal("c", Assert.Single(map).Value);
    }

    [Theory]
    [InlineData("org-a/repo,org-b/repo")] // data before the header
    [InlineData("from,to")] // wrong header names
    public void Parse_requires_source_target_header(string firstLine)
    {
        Assert.Throws<FormatException>(() => CsvMapping.Parse([firstLine, "a,b"]));
    }

    [Fact]
    public void Parse_throws_when_file_is_empty()
    {
        Assert.Throws<FormatException>(() => CsvMapping.Parse([]));
    }

    [Theory]
    [InlineData("only-one-column")]
    [InlineData("a,b,c")]
    public void Parse_throws_on_wrong_column_count(string line)
    {
        Assert.Throws<FormatException>(() => CsvMapping.Parse(["source,target", line]));
    }

    [Fact]
    public void Parse_throws_on_empty_source_with_target()
    {
        Assert.Throws<FormatException>(() => CsvMapping.Parse(["source,target", ",b"]));
    }

    [Fact]
    public void Parse_ignores_rows_with_empty_target()
    {
        // Rows with a blank target are unfilled template rows, not errors.
        var map = CsvMapping.Parse(
        [
            "source,target",
            "org-a/repo-1,",
            "org-a/repo-2,org-b/repo-2",
            ",",
        ]);

        var pair = Assert.Single(map);
        Assert.Equal("org-a/repo-2", pair.Key);
        Assert.Equal("org-b/repo-2", pair.Value);
    }

    [Fact]
    public void Load_parses_a_csv_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "gpm-mapping-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path, "source,target\norg-a/repo,org-b/repo\n");
        try
        {
            var map = CsvMapping.Load(path);
            Assert.Equal("org-b/repo", map["org-a/repo"]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
