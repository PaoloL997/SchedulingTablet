using Windows.Networking.Connectivity;

namespace Brembana;

public sealed class NetworkConnectivityService : IDisposable
{
    private readonly Timer _pollTimer;
    private bool _disposed;

    public event Action<bool>? ConnectivityChanged;

    public bool IsOnline { get; private set; }

    public NetworkConnectivityService()
    {
        IsOnline = CheckOnline();
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        _pollTimer = new Timer(_ => Update(), null, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4));
    }

    private void OnNetworkStatusChanged(object sender) => Update();

    private void Update()
    {
        if (_disposed) return;

        var online = CheckOnline();
        if (online == IsOnline) return;

        IsOnline = online;
        ConnectivityChanged?.Invoke(online);
    }

    private static bool CheckOnline()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile == null) return false;

            var level = profile.GetNetworkConnectivityLevel();
            return level is NetworkConnectivityLevel.InternetAccess
                or NetworkConnectivityLevel.ConstrainedInternetAccess;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
        _pollTimer.Dispose();
    }
}
