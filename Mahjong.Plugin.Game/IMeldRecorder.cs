namespace Mahjong.Plugin.Game;

/// <summary>
/// Tracks our own open melds for the current round. The addon's on-disk meld
/// struct isn't yet mapped, so we record each meld as the auto-play (or
/// hooked manual click) accepts a call prompt. Reset on round-end.
/// </summary>
public interface IMeldRecorder
{
    IReadOnlyList<Meld> Current { get; }

    void Record(Meld meld);

    /// <summary>Reset the recorder if the closed-hand count proves a new round started.</summary>
    void ResetIfRoundEnded(int closedHandCount);
}
