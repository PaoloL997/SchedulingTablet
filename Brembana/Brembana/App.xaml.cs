using System.Collections.Generic;
using System.IO;
using Microsoft.UI.Xaml;

namespace Brembana
{
    public partial class App : Application
    {
        private Window? _window;
        private static bool _crashRestartAttempted;

        public static MainWindow MainWindow { get; private set; } = null!;
        public static BridgeApiClient BridgeClient { get; } = new();
        private static BridgeDataService? _bridgeData;
        public static BridgeDataService BridgeData => _bridgeData ??= new BridgeDataService(BridgeClient);
        public static bool BridgeConnected { get; private set; }
        public static string? LastBridgeError { get; private set; }
        public static string? LoadedEnvPath { get; private set; }
        public static string? GoogleCloudVisionApiKey { get; private set; }
        /// <summary>PIN admin kiosk (da KIOSK_ADMIN_PIN nel .env).</summary>
        public static string? KioskAdminPin { get; private set; }
        /// <summary>Se false, non entra in kiosk all'avvio (KIOSK_ENABLED=false).</summary>
        public static bool KioskEnabled { get; private set; } = true;

        public static BridgeUserDto? CurrentUser => BridgeClient.CurrentUser;
        public static string? CurrentUserStabilimento => BridgeClient.CurrentUser?.Stabilimento;

        public static event Action<string?>? BridgeDisconnected;

        public static void MarkBridgeDisconnected(string? reason = null)
        {
            var wasConnected = BridgeConnected;
            BridgeConnected = false;
            BridgeClient.ClearSession();
            MainWindow.SetLoggedInUser(null);

            if (!string.IsNullOrWhiteSpace(reason))
                LastBridgeError = reason;

            if (wasConnected || !string.IsNullOrWhiteSpace(reason))
                BridgeDisconnected?.Invoke(LastBridgeError);
        }

        public static async Task<bool> TryConnectBridgeAsync()
        {
            LastBridgeError = null;
            try
            {
                var bridgeReady = await ApplyEnvSettingsAsync();
                if (!bridgeReady)
                {
                    if (string.IsNullOrWhiteSpace(LastBridgeError))
                        LastBridgeError = UiStrings.NotConfigured;
                    MarkBridgeDisconnected(LastBridgeError);
                    MainWindow.UpdateStatus(LastBridgeError, error: true);
                    return false;
                }

                MainWindow.UpdateStatus(UiStrings.StatusConnecting);
                await BridgeClient.LoginAsync();
                try
                {
                    await BridgeClient.GetAuthMeAsync();
                }
                catch
                {
                    // Il payload login contiene già stabilimento quando disponibile.
                }

                MainWindow.SetLoggedInUser(BridgeClient.Username);
                await BridgeClient.WaitUntilReadyAsync(
                    TimeSpan.FromMinutes(2),
                    new Progress<string>(msg => MainWindow.UpdateStatus(TranslateBridgeProgress(msg))));
                BridgeConnected = true;
                LastBridgeError = null;
                MainWindow.NotifyBridgeReconnected();
                return true;
            }
            catch (Exception ex)
            {
                LastBridgeError = $"Errore bridge: {ex.Message}";
                MarkBridgeDisconnected(LastBridgeError);
                MainWindow.UpdateStatus(LastBridgeError, error: true);
                return false;
            }
        }
        public static string TrackingDataPath { get; private set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brembana", "Tracking");

        public App()
        {
            InitializeComponent();
            RegisterGlobalExceptionHandlers();
        }

        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            AppLogger.Info("Avvio applicazione.");

            // Carica .env (incluso PIN kiosk) prima della MainWindow quando possibile.
            try
            {
                await ApplyEnvSettingsAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Errore precaricamento .env.", ex);
            }

            MainWindow = new MainWindow();
            _window = MainWindow;
            _window.Activate();

            MainWindow.UpdateStatus(UiStrings.StatusStarting);

            if (await TryConnectBridgeAsync())
            {
                try
                {
                    await BridgeData.SyncDatasetAsync();
                }
                catch (Exception ex)
                {
                    MarkBridgeDisconnected($"Errore bridge: {ex.Message}");
                    MainWindow.UpdateStatus($"Errore bridge: {ex.Message}", error: true);
                }
            }

            await MainWindow.InitializeAsync();
        }

        private void RegisterGlobalExceptionHandlers()
        {
            UnhandledException += (_, e) =>
            {
                AppLogger.Error("UnhandledException WinUI.", e.Exception);
                // Evita chiusura immediata; il processo resta attivo se possibile.
                e.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    AppLogger.Error("AppDomain.UnhandledException.", ex);
                else
                    AppLogger.Error($"AppDomain.UnhandledException: {e.ExceptionObject}");

                // Solo su crash terminante: tenta relaunch immediato.
                if (e.IsTerminating)
                    ScheduleCrashRestart();
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                AppLogger.Error("TaskScheduler.UnobservedTaskException.", e.Exception);
                e.SetObserved();
            };
        }

        private static void ScheduleCrashRestart()
        {
            if (_crashRestartAttempted)
                return;
            _crashRestartAttempted = true;

            // Best-effort: Task Scheduler riavvierà comunque dopo login;
            // qui si tenta un relaunch immediato lato applicazione.
            try
            {
                AdminMenuHelper.TryRestartAfterCrash();
            }
            catch (Exception ex)
            {
                AppLogger.Error("ScheduleCrashRestart fallito.", ex);
            }
        }

        private static async Task<bool> ApplyEnvSettingsAsync()
        {
            try
            {
                var envPath = FindEnvFile();
                LoadedEnvPath = envPath;
                if (envPath == null)
                {
                    LastBridgeError = $".env non trovato accanto a {AppContext.BaseDirectory}";
                    return false;
                }

                var env = ParseEnvFile(envPath);
                if (env.Count == 0)
                {
                    LastBridgeError = $".env vuoto o non valido: {envPath}";
                    return false;
                }

                var settingsService = new SettingsService();
                var settings = await settingsService.GetSettingsAsync();
                bool changed = false;

                if (env.TryGetValue("GOOGLE_CLOUD_VISION_API_KEY", out var visionApiKey) && !string.IsNullOrWhiteSpace(visionApiKey))
                    GoogleCloudVisionApiKey = visionApiKey.Trim();

                // PIN admin kiosk: non hardcodato nel codice.
                if (env.TryGetValue("KIOSK_ADMIN_PIN", out var adminPin) && !string.IsNullOrWhiteSpace(adminPin))
                    KioskAdminPin = adminPin.Trim();

                if (env.TryGetValue("KIOSK_ENABLED", out var kioskEnabledRaw) && !string.IsNullOrWhiteSpace(kioskEnabledRaw))
                {
                    KioskEnabled = kioskEnabledRaw.Trim() is not ("0" or "false" or "False" or "FALSE" or "no" or "No");
                }

                var baseUrl = "";
                if (env.TryGetValue("BRIDGE_BASE_URL", out var bridgeUrl) && !string.IsNullOrWhiteSpace(bridgeUrl))
                    baseUrl = bridgeUrl.Trim();
                else if (!string.IsNullOrWhiteSpace(settings.BackendUrl))
                    baseUrl = settings.BackendUrl.Trim();

                var username = env.TryGetValue("BRIDGE_USERNAME", out var u) ? u.Trim() : "";
                var password = env.TryGetValue("BRIDGE_PASSWORD", out var p) ? p.Trim() : "";

                if (string.IsNullOrWhiteSpace(baseUrl))
                {
                    LastBridgeError = "BRIDGE_BASE_URL mancante nel .env";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                {
                    LastBridgeError = "BRIDGE_USERNAME o BRIDGE_PASSWORD mancanti nel .env";
                    return false;
                }

                if (settings.BackendUrl != baseUrl)
                {
                    settings.BackendUrl = baseUrl;
                    changed = true;
                }

                BridgeClient.Configure(baseUrl, username, password);

                if (env.TryGetValue("TRACKING_DATA_PATH", out var trackingPath) && !string.IsNullOrWhiteSpace(trackingPath))
                    TrackingDataPath = trackingPath.Trim();

                if (changed)
                    await settingsService.SaveSettingsAsync(settings);

                Directory.CreateDirectory(TrackingDataPath);
                return true;
            }
            catch (Exception ex)
            {
                LastBridgeError = $"Errore lettura .env: {ex.Message}";
                return false;
            }
        }

        private static string? FindEnvFile()
        {
            var candidates = new List<string>();
            void AddCandidate(string? path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                var full = Path.GetFullPath(path);
                if (!candidates.Contains(full, StringComparer.OrdinalIgnoreCase))
                    candidates.Add(full);
            }

            AddCandidate(Path.Combine(AppContext.BaseDirectory, ".env"));

            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                var processDir = Path.GetDirectoryName(processPath);
                AddCandidate(Path.Combine(processDir ?? "", ".env"));
            }

            var assemblyDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            AddCandidate(Path.Combine(assemblyDir ?? "", ".env"));

            foreach (var path in candidates)
            {
                if (File.Exists(path)) return path;
            }

            return null;
        }

        private static Dictionary<string, string> ParseEnvFile(string path)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
                var eq = trimmed.IndexOf('=');
                if (eq <= 0) continue;
                var key = trimmed[..eq].Trim();
                var value = trimmed[(eq + 1)..].Trim();
                if (!string.IsNullOrEmpty(key)) result[key] = value;
            }
            return result;
        }

        private static string TranslateBridgeProgress(string message) => message switch
        {
            "Loading dataset from server..." => "Caricamento dataset dal server...",
            "Server write-back in progress..." => "Scrittura sul server in corso...",
            _ => message
        };
    }
}
