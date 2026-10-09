namespace Brembana;

public enum BridgeHealthState
{
    Healthy,
    SessionExpired,
    Unreachable
}

public sealed class BridgeHealthResult
{
    public BridgeHealthState State { get; init; }
    public string? Detail { get; init; }

    public bool IsHealthy => State == BridgeHealthState.Healthy;

    public bool RequiresReconnect => State is BridgeHealthState.SessionExpired
        or BridgeHealthState.Unreachable;
}

public sealed class BridgeSessionWatchdog : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly BridgeApiClient _client;
    private readonly Func<bool> _isNetworkOnline;
    private readonly Timer _pollTimer;
    private bool _disposed;
    private BridgeHealthResult _lastResult = new() { State = BridgeHealthState.Healthy };

    public event Action<BridgeHealthResult>? HealthChanged;

    public BridgeHealthResult LastResult => _lastResult;

    public BridgeSessionWatchdog(BridgeApiClient client, Func<bool> isNetworkOnline)
    {
        _client = client;
        _isNetworkOnline = isNetworkOnline;
        _pollTimer = new Timer(_ => _ = CheckAsync(), null, PollInterval, PollInterval);
    }

    public async Task CheckNowAsync() => await CheckAsync();

    private async Task CheckAsync()
    {
        if (_disposed || !App.BridgeConnected)
            return;

        try
        {
            var result = await EvaluateHealthAsync();
            if (ResultsEquivalent(_lastResult, result))
                return;

            _lastResult = result;
            HealthChanged?.Invoke(result);
        }
        catch
        {
            // Ignora errori del watchdog: il prossimo ciclo riproverà.
        }
    }

    internal async Task<BridgeHealthResult> EvaluateHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!App.BridgeConnected)
            return new BridgeHealthResult { State = BridgeHealthState.SessionExpired };

        if (!_isNetworkOnline())
        {
            return new BridgeHealthResult
            {
                State = BridgeHealthState.Unreachable,
                Detail = UiStrings.NoNetworkTitle
            };
        }

        // Il profilo utente è l'unica chiamata che verifica insieme la raggiungibilità
        // del server e la validità della sessione Django del tablet.
        try
        {
            await _client.GetAuthMeAsync(cancellationToken);
            return new BridgeHealthResult { State = BridgeHealthState.Healthy };
        }
        catch (BridgeApiException ex) when (BridgeApiClient.IsSessionLost(ex.StatusCode))
        {
            return new BridgeHealthResult
            {
                State = BridgeHealthState.SessionExpired,
                Detail = UiStrings.BridgeDisconnectedMessage
            };
        }
        catch (BridgeApiException ex)
        {
            return new BridgeHealthResult
            {
                State = BridgeHealthState.Unreachable,
                Detail = ex.Message
            };
        }
        catch (Exception ex)
        {
            return new BridgeHealthResult
            {
                State = BridgeHealthState.Unreachable,
                Detail = ex.Message
            };
        }
    }

    public void ResetHealthyState()
    {
        _lastResult = new BridgeHealthResult { State = BridgeHealthState.Healthy };
    }

    private static bool ResultsEquivalent(BridgeHealthResult current, BridgeHealthResult next)
        => current.State == next.State
           && string.Equals(current.Detail, next.Detail, StringComparison.Ordinal);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Dispose();
    }
}
