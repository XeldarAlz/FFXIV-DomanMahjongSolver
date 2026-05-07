namespace Mahjong.Plugin.Game;

/// <summary>
/// Thread-discipline abstraction over Dalamud's framework scheduler. Lets the
/// plugin's domain code request "run this on the framework thread" without
/// taking a hard dependency on <c>Dalamud.Plugin.Services.IFramework</c>.
/// Tests substitute a synchronous fake.
/// </summary>
public interface IFrameworkScheduler
{
    /// <summary>True when the caller is currently on the framework thread.</summary>
    bool IsOnFrameworkThread { get; }

    /// <summary>Run an action on the framework thread; awaits completion.</summary>
    Task RunOnFrameworkThreadAsync(Action action, CancellationToken cancellationToken = default);

    /// <summary>Schedule an action to run after the given delay on the framework thread.</summary>
    Task RunOnTickAsync(TimeSpan delay, Action action, CancellationToken cancellationToken = default);
}
