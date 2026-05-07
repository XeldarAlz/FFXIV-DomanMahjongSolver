namespace Mahjong.Plugin.Game.Tests;

public class ResultTests
{
    [Fact]
    public void Success_carries_value_and_reports_IsSuccess()
    {
        var r = Result<int, ReadError>.Success(42);
        Assert.True(r.IsSuccess);
        Assert.Equal(42, r.Value);
    }

    [Fact]
    public void Failure_carries_error_and_reports_not_IsSuccess()
    {
        var r = Result<int, ReadError>.Failure(ReadError.AddonMissing);
        Assert.False(r.IsSuccess);
        Assert.Equal(ReadError.AddonMissing, r.Error);
    }

    [Fact]
    public void Accessing_Value_on_failure_throws()
    {
        var r = Result<int, ReadError>.Failure(ReadError.NotVisible);
        Assert.Throws<InvalidOperationException>(() => r.Value);
    }

    [Fact]
    public void Accessing_Error_on_success_throws()
    {
        var r = Result<int, ReadError>.Success(7);
        Assert.Throws<InvalidOperationException>(() => r.Error);
    }

    [Fact]
    public void Match_dispatches_to_the_right_branch()
    {
        var success = Result<int, ReadError>.Success(10);
        var failure = Result<int, ReadError>.Failure(ReadError.VariantMismatch);

        string s = success.Match(v => $"got {v}", e => $"err {e}");
        string f = failure.Match(v => $"got {v}", e => $"err {e}");

        Assert.Equal("got 10", s);
        Assert.Equal("err VariantMismatch", f);
    }

    [Theory]
    [InlineData(ReadError.AddonMissing)]
    [InlineData(ReadError.NotVisible)]
    [InlineData(ReadError.VariantMismatch)]
    [InlineData(ReadError.ProbeFailed)]
    [InlineData(ReadError.ProbeTimeout)]
    [InlineData(ReadError.Unexpected)]
    public void All_ReadError_codes_round_trip_through_Result(ReadError code)
    {
        var r = Result<string, ReadError>.Failure(code);
        Assert.Equal(code, r.Error);
    }
}
