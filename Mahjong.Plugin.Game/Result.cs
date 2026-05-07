namespace Mahjong.Plugin.Game;

/// <summary>
/// Sum type for fallible operations. Either <see cref="Success"/> with a
/// value or <see cref="Failure"/> with a typed error code. Replaces nullable
/// returns at every plugin boundary so callers can match on a known set of
/// failure reasons instead of guessing why null came back.
///
/// <example>
/// <code>
/// var result = addonReader.Read();
/// if (result.IsSuccess)
///     Process(result.Value);
/// else
///     log.Warn($"read failed: {result.Error}");
/// </code>
/// </example>
/// </summary>
public readonly struct Result<TValue, TError> where TError : struct
{
    private readonly TValue? value;
    private readonly TError error;

    public bool IsSuccess { get; }

    public TValue Value => IsSuccess
        ? value!
        : throw new InvalidOperationException("Result is in failure state — check IsSuccess first.");

    public TError Error => IsSuccess
        ? throw new InvalidOperationException("Result is in success state — check IsSuccess first.")
        : error;

    private Result(TValue value)
    {
        this.value = value;
        error = default;
        IsSuccess = true;
    }

    private Result(TError error)
    {
        value = default;
        this.error = error;
        IsSuccess = false;
    }

    public static Result<TValue, TError> Success(TValue value) => new(value);
    public static Result<TValue, TError> Failure(TError error) => new(error);

    /// <summary>Pattern-match on the result without unboxing.</summary>
    public T Match<T>(Func<TValue, T> onSuccess, Func<TError, T> onFailure) =>
        IsSuccess ? onSuccess(Value) : onFailure(Error);

    public override string ToString() => IsSuccess
        ? $"Success({value})"
        : $"Failure({error})";
}
