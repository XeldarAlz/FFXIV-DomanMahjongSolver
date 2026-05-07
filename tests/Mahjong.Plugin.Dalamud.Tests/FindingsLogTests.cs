using Mahjong.Plugin.Dalamud.Logging;
using Mahjong.Plugin.Dalamud.Tests.Stubs;

namespace Mahjong.Plugin.Dalamud.Tests;

public class FindingsLogTests
{
    [Fact]
    public void Throws_on_null_directory()
    {
        using var tmp = new TempDir();
        using var errors = new ErrorSink(tmp.Path);
        Assert.Throws<ArgumentNullException>(() => new FindingsLog(null!, errors));
    }

    [Fact]
    public void Throws_on_empty_directory()
    {
        using var tmp = new TempDir();
        using var errors = new ErrorSink(tmp.Path);
        Assert.Throws<ArgumentException>(() => new FindingsLog("", errors));
    }

    [Fact]
    public void Throws_on_null_error_sink()
    {
        using var tmp = new TempDir();
        Assert.Throws<ArgumentNullException>(() => new FindingsLog(tmp.Path, null!));
    }

    [Fact]
    public void Constructor_creates_the_findings_subdirectory()
    {
        using var tmp = new TempDir();
        using var errors = new ErrorSink(tmp.Path);
        using var sink = new FindingsLog(tmp.Path, errors);

        Assert.True(Directory.Exists(sink.FindingsDir));
        Assert.Equal(Path.Combine(tmp.Path, "findings"), sink.FindingsDir);
    }

    [Fact]
    public void Record_with_data_writes_an_ndjson_line()
    {
        using var tmp = new TempDir();
        using var errors = new ErrorSink(tmp.Path);
        using var sink = new FindingsLog(tmp.Path, errors);

        sink.Record("variant_match", new Dictionary<string, object?>
        {
            ["addon"] = "Emj",
            ["count"] = 3,
        });

        var files = Directory.GetFiles(sink.FindingsDir, "findings-*.ndjson");
        Assert.Single(files);
        var contents = File.ReadAllText(files[0]);
        Assert.Contains("\"kind\":\"variant_match\"", contents);
        Assert.Contains("\"addon\":\"Emj\"", contents);
        Assert.Contains("\"count\":3", contents);
    }

    [Fact]
    public void Record_with_note_writes_an_ndjson_line()
    {
        using var tmp = new TempDir();
        using var errors = new ErrorSink(tmp.Path);
        using var sink = new FindingsLog(tmp.Path, errors);

        sink.Record("sigscan_hit", "found at 0xDEADBEEF");

        var files = Directory.GetFiles(sink.FindingsDir, "findings-*.ndjson");
        var contents = File.ReadAllText(files[0]);
        Assert.Contains("\"kind\":\"sigscan_hit\"", contents);
        Assert.Contains("\"note\":\"found at 0xDEADBEEF\"", contents);
    }

    [Fact]
    public void Empty_kind_is_dropped()
    {
        using var tmp = new TempDir();
        using var errors = new ErrorSink(tmp.Path);
        using var sink = new FindingsLog(tmp.Path, errors);

        sink.Record("", new Dictionary<string, object?>());
        sink.Record(null!, "note");

        var files = Directory.GetFiles(sink.FindingsDir, "findings-*.ndjson");
        Assert.Empty(files);
    }

    [Fact]
    public void Records_after_dispose_are_dropped()
    {
        using var tmp = new TempDir();
        using var errors = new ErrorSink(tmp.Path);
        var sink = new FindingsLog(tmp.Path, errors);
        sink.Dispose();

        sink.Record("kind", "note");

        var files = Directory.GetFiles(sink.FindingsDir, "findings-*.ndjson");
        Assert.Empty(files);
    }

    [Fact]
    public void Sequence_numbers_are_monotonic_per_instance()
    {
        using var tmp = new TempDir();
        using var errors = new ErrorSink(tmp.Path);
        using var sink = new FindingsLog(tmp.Path, errors);

        sink.Record("a", "1");
        sink.Record("b", "2");
        sink.Record("c", "3");

        var files = Directory.GetFiles(sink.FindingsDir, "findings-*.ndjson");
        var lines = File.ReadAllLines(files[0]);
        Assert.Equal(3, lines.Length);
        Assert.Contains("\"seq\":1", lines[0]);
        Assert.Contains("\"seq\":2", lines[1]);
        Assert.Contains("\"seq\":3", lines[2]);
    }
}
