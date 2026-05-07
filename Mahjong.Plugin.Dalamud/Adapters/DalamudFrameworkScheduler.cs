using Dalamud.Plugin.Services;

namespace Mahjong.Plugin.Dalamud.Adapters;

/// <summary>
/// Bridges <see cref="IFrameworkScheduler"/> onto Dalamud's
/// <see cref="IFramework"/>. Plugin domain code requests "run on the
/// framework thread" through this interface without taking a hard dependency
/// on Dalamud — tests substitute a synchronous fake that runs callbacks
/// inline.
/// </summary>
internal sealed class DalamudFrameworkScheduler : IFrameworkScheduler
{
    private readonly IFramework framework;

    public DalamudFrameworkScheduler(IFramework framework)
    {
        ArgumentNullException.ThrowIfNull(framework);
        this.framework = framework;
    }

    public bool IsOnFrameworkThread => framework.IsInFrameworkUpdateThread;

    public Task RunOnFrameworkThreadAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        return framework.RunOnFrameworkThread(action);
    }

    public Task RunOnTickAsync(TimeSpan delay, Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        return framework.RunOnTick(action, delay, cancellationToken: cancellationToken);
    }
}
