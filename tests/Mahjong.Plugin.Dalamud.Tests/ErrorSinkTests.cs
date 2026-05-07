using Mahjong.Plugin.Dalamud.Logging;
using Mahjong.Plugin.Dalamud.Tests.Stubs;

namespace Mahjong.Plugin.Dalamud.Tests;

public class ErrorSinkTests
{
    [Fact]
    public void Throws_on_null_directory()
    {
        // ArgumentException.ThrowIfNullOrEmpty raises ArgumentNullException
        // for null and ArgumentException for empty — both inherit from
        // ArgumentException, but the precise type differs.
        Assert.Throws<ArgumentNullException>(() => new ErrorSink(null!));
    }

    [Fact]
    public void Throws_on_empty_directory()
    {
        Assert.Throws<ArgumentException>(() => new ErrorSink(""));
    }

    [Fact]
    public void Constructor_creates_the_errors_subdirectory()
    {
        using var tmp = new TempDir();
        using var sink = new ErrorSink(tmp.Path);
        Assert.True(Directory.Exists(sink.ErrorsDir));
        Assert.Equal(Path.Combine(tmp.Path, "errors"), sink.ErrorsDir);
    }

    [Fact]
    public void RecordException_appends_an_ndjson_line()
    {
        using var tmp = new TempDir();
        using var sink = new ErrorSink(tmp.Path);

        sink.RecordException("TestContext", new InvalidOperationException("boom"));

        var files = Directory.GetFiles(sink.ErrorsDir, "errors-*.ndjson");
        Assert.Single(files);
        var contents = File.ReadAllText(files[0]);
        Assert.Contains("\"sev\":\"error\"", contents);
        Assert.Contains("\"ctx\":\"TestContext\"", contents);
        Assert.Contains("\"msg\":\"boom\"", contents);
        Assert.Contains("\"ex\":\"System.InvalidOperationException\"", contents);
    }

    [Fact]
    public void RecordWarning_appends_a_warn_severity_line()
    {
        using var tmp = new TempDir();
        using var sink = new ErrorSink(tmp.Path);

        sink.RecordWarning("Probe", "sigscan returned 0 hits");

        var files = Directory.GetFiles(sink.ErrorsDir, "errors-*.ndjson");
        var contents = File.ReadAllText(files[0]);
        Assert.Contains("\"sev\":\"warn\"", contents);
        Assert.Contains("\"msg\":\"sigscan returned 0 hits\"", contents);
        Assert.DoesNotContain("\"ex\":", contents); // no exception type for warnings
    }

    [Fact]
    public void RecordException_is_a_noop_for_null_exception()
    {
        using var tmp = new TempDir();
        using var sink = new ErrorSink(tmp.Path);

        sink.RecordException("ctx", null!);

        var files = Directory.GetFiles(sink.ErrorsDir, "errors-*.ndjson");
        Assert.Empty(files);
    }

    [Fact]
    public void Multiple_records_increment_the_sequence_number()
    {
        using var tmp = new TempDir();
        using var sink = new ErrorSink(tmp.Path);

        sink.RecordWarning("a", "first");
        sink.RecordWarning("b", "second");
        sink.RecordWarning("c", "third");

        var files = Directory.GetFiles(sink.ErrorsDir, "errors-*.ndjson");
        var lines = File.ReadAllLines(files[0]);
        Assert.Equal(3, lines.Length);
        Assert.Contains("\"seq\":1", lines[0]);
        Assert.Contains("\"seq\":2", lines[1]);
        Assert.Contains("\"seq\":3", lines[2]);
    }

    [Fact]
    public void Records_after_dispose_are_dropped()
    {
        using var tmp = new TempDir();
        var sink = new ErrorSink(tmp.Path);
        sink.Dispose();

        sink.RecordException("ctx", new Exception("after dispose"));
        sink.RecordWarning("ctx", "after dispose");

        var files = Directory.GetFiles(sink.ErrorsDir, "errors-*.ndjson");
        Assert.Empty(files);
    }

    [Fact]
    public void Inner_exception_message_is_captured()
    {
        using var tmp = new TempDir();
        using var sink = new ErrorSink(tmp.Path);

        var inner = new ArgumentNullException("paramName", "the inner");
        var outer = new InvalidOperationException("the outer", inner);
        sink.RecordException("ctx", outer);

        var files = Directory.GetFiles(sink.ErrorsDir, "errors-*.ndjson");
        var contents = File.ReadAllText(files[0]);
        Assert.Contains("\"inner\":", contents);
        Assert.Contains("the inner", contents);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        using var tmp = new TempDir();
        var sink = new ErrorSink(tmp.Path);
        sink.Dispose();
        sink.Dispose();
    }
}
