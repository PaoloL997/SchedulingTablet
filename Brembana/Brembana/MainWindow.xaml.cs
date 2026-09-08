using System.ComponentModel.DataAnnotations;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Windows.Data.Pdf;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using WinRT.Interop;
using System.IO;

#pragma warning disable CS8601

namespace Brembana
{
    public sealed partial class MainWindow : Window
    {
        private enum ConnectionOverlayMode
        {
            Hidden,
            NetworkOffline,
            BridgeUnavailable
        }

        private readonly SettingsService _settingsService;
        private readonly NetworkConnectivityService _networkService = new();
        private readonly BridgeSessionWatchdog _bridgeWatchdog;
        private BridgeHealthResult? _bridgeHealthIssue;
        private ConnectionOverlayMode _overlayMode = ConnectionOverlayMode.Hidden;
        private IComponentVisionService? _visionService;
        private JobOrderTracker? _currentTracker;
        private AttivitaDto? _sessionAttivita;
        private bool _isOutgoing = true;
        private TaskCompletionSource? _cameraSessionTcs;
        private CancellationTokenSource? _cameraSessionCts;
        private readonly SemaphoreSlim _cameraResourceLock = new(1, 1);
        private MediaCapture? _cameraMediaCapture;
        private MediaPlayer? _cameraMediaPlayer;
        private LowLagPhotoCapture? _lowLagPhotoCapture;
        private ImageEncodingProperties? _activePhotoEncoding;
        private Action? _cameraSessionRefreshCallback;
        private bool _cameraInventoryMode;
        private InventoryActionMode _inventoryActionMode;
        private CancellationTokenSource? _cameraKeepAliveCts;
        private Task? _cameraKeepAliveTask;
        private double _cameraScanGuideBorderThickness = 3;
        private readonly KioskModeService _kioskMode;
        private AdminAccessHelper? _adminBrandAccess;
        private bool _adminMenuOpen;

        public event Action? DatasetRefreshed;

        public IReadOnlyList<string> ActivityLabels { get; private set; } = Array.Empty<string>();
        public bool IsOutgoing => _isOutgoing;
        internal bool HasActiveSession => _currentTracker != null;
        internal string? SessionActivityName => _currentTracker?.JobOrderName;
        internal AttivitaDto? CurrentSessionActivity => _sessionAttivita;
        internal int SelectedRegisterCount =>
            _currentTracker?.TrackedComponents.Count(c => c.IsSelectedForRegister) ?? 0;
        public bool HasOpenAI => _visionService != null;
        internal string VisionProviderName => "Google Vision";

        public MainWindow()
        {
            InitializeComponent();
            var appWindow = this.AppWindow;
            appWindow.SetIcon("Media/LogoBIcon.ico");

            // Modalità kiosk tablet: fullscreen, no title bar, topmost, chiusura protetta.
            _kioskMode = new KioskModeService(this);
            if (App.KioskEnabled)
                _kioskMode.EnterKioskMode();
            else
                AppLogger.Info("Kiosk disabilitato (KIOSK_ENABLED=false).");
            _adminBrandAccess = new AdminAccessHelper(AdminBrandHotspot, TryOpenAdminMenuAsync);

            _settingsService = new SettingsService();
            _bridgeWatchdog = new BridgeSessionWatchdog(App.BridgeClient, () => _networkService.IsOnline);
            Activated += MainWindow_Activated;
            Closed += (_, _) =>
            {
                _networkService.Dispose();
                _bridgeWatchdog.Dispose();
            };
            _networkService.ConnectivityChanged += OnNetworkConnectivityChanged;
            _bridgeWatchdog.HealthChanged += OnBridgeHealthChanged;
            App.BridgeDisconnected += OnBridgeDisconnected;
            ConnectionOverlayLoadingText.Text = UiStrings.BridgeReloadingMessage;
            CameraCaptureOverlay.SizeChanged += (_, _) => PositionCameraScanGuide();
        }

        /// <summary>
        /// Sblocco nascosto: long-press logo/brand → PIN → menu admin.
        /// </summary>
        internal async Task TryOpenAdminMenuAsync()
        {
            if (_adminMenuOpen || Content?.XamlRoot == null)
                return;

            _adminMenuOpen = true;
            try
            {
                var pinOk = await AdminMenuHelper.PromptPinAsync(Content.XamlRoot, App.KioskAdminPin);
                if (!pinOk)
                    return;

                await AdminMenuHelper.ShowAdminMenuAsync(
                    Content.XamlRoot,
                    _kioskMode,
                    closeApplication: () => AdminMenuHelper.CloseApplication(_kioskMode),
                    restartApplication: () => AdminMenuHelper.RestartApplication(_kioskMode));
            }
            finally
            {
                _adminMenuOpen = false;
            }
        }

        internal void NotifyBridgeReconnected()
        {
            _bridgeHealthIssue = null;
            _bridgeWatchdog.ResetHealthyState();
            DispatcherQueue.TryEnqueue(UpdateConnectionOverlay);
        }

        private void OnBridgeDisconnected(string? reason)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _bridgeHealthIssue = new BridgeHealthResult
                {
                    State = BridgeHealthState.SessionExpired,
                    Detail = reason
                };
                UpdateConnectionOverlay();
            });
        }

        private void OnBridgeHealthChanged(BridgeHealthResult result)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (result.IsHealthy)
                {
                    _bridgeHealthIssue = null;
                    UpdateConnectionOverlay();
                    return;
                }

                _bridgeHealthIssue = result;
                if (result.RequiresReconnect)
                    App.MarkBridgeDisconnected(result.Detail);

                UpdateConnectionOverlay();
            });
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            UpdateConnectionOverlay();
        }

        private void OnNetworkConnectivityChanged(bool isOnline)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                UpdateConnectionOverlay();
                if (isOnline && !App.BridgeConnected)
                    await RefreshDatasetAsync();
            });
        }

        private void UpdateConnectionOverlay()
        {
            if (!_networkService.IsOnline)
            {
                ShowConnectionOverlay(
                    ConnectionOverlayMode.NetworkOffline,
                    UiStrings.NoNetworkTitle,
                    UiStrings.NoNetworkMessage,
                    showReloadButton: false);
                return;
            }

            if (!App.BridgeConnected || _bridgeHealthIssue is { IsHealthy: false })
            {
                var (title, message) = GetBridgeOverlayContent(_bridgeHealthIssue);
                ShowConnectionOverlay(
                    ConnectionOverlayMode.BridgeUnavailable,
                    title,
                    message,
                    showReloadButton: true);
                return;
            }

            HideConnectionOverlay();
        }

        private static (string Title, string Message) GetBridgeOverlayContent(BridgeHealthResult? health)
        {
            if (health == null)
                return (UiStrings.BridgeDisconnectedTitle, UiStrings.BridgeDisconnectedMessage);

            var detail = string.IsNullOrWhiteSpace(health.Detail) ? null : health.Detail.Trim();
            return health.State switch
            {
                BridgeHealthState.DatasetLoading => (
                    UiStrings.BridgeDatasetLoadingTitle,
                    detail ?? UiStrings.BridgeDatasetLoadingMessage),
                BridgeHealthState.DatasetWriting => (
                    UiStrings.BridgeDatasetWritingTitle,
                    detail ?? UiStrings.BridgeDatasetWritingMessage),
                BridgeHealthState.DatasetError => (
                    UiStrings.BridgeDatasetErrorTitle,
                    detail ?? UiStrings.BridgeDatasetErrorMessage),
                BridgeHealthState.SessionExpired => (
                    UiStrings.BridgeDisconnectedTitle,
                    detail ?? UiStrings.BridgeDisconnectedMessage),
                _ => (
                    UiStrings.BridgeDisconnectedTitle,
                    detail ?? UiStrings.BridgeDisconnectedMessage)
            };
        }

        private void ShowConnectionOverlay(
            ConnectionOverlayMode mode,
            string title,
            string message,
            bool showReloadButton)
        {
            var wasHidden = _overlayMode == ConnectionOverlayMode.Hidden;
            _overlayMode = mode;

            if (mode == ConnectionOverlayMode.BridgeUnavailable && wasHidden)
                CancelCameraSession();

            ConnectionOverlayTitle.Text = title;
            ConnectionOverlayMessage.Text = message;
            ConnectionOverlayReloadButton.Visibility = showReloadButton
                ? Visibility.Visible
                : Visibility.Collapsed;
            ConnectionOverlayIcon.Glyph = mode == ConnectionOverlayMode.NetworkOffline
                ? "\uE701"
                : "\uE946";

            OfflineOverlay.Visibility = Visibility.Visible;
            AppContentRoot.Opacity = 0.35;
            AppContentRoot.IsHitTestVisible = false;
            CameraCaptureOverlay.IsHitTestVisible = false;

            UpdateStatus(title, error: true);
        }

        private void HideConnectionOverlay()
        {
            _overlayMode = ConnectionOverlayMode.Hidden;
            OfflineOverlay.Visibility = Visibility.Collapsed;
            AppContentRoot.Opacity = 1;
            AppContentRoot.IsHitTestVisible = true;
            CameraCaptureOverlay.IsHitTestVisible = true;
        }

        private void SetConnectionOverlayLoading(bool isLoading)
        {
            ConnectionOverlayContentPanel.Visibility = isLoading
                ? Visibility.Collapsed
                : Visibility.Visible;
            ConnectionOverlayLoadingPanel.Visibility = isLoading
                ? Visibility.Visible
                : Visibility.Collapsed;
            ConnectionOverlayLoadingRing.IsActive = isLoading;
            ConnectionOverlayReloadButton.IsEnabled = !isLoading;
        }

        private async void ConnectionOverlayReload_Click(object sender, RoutedEventArgs e)
        {
            SetConnectionOverlayLoading(true);
            try
            {
                await RefreshDatasetAsync();
                if (App.BridgeConnected)
                {
                    _bridgeHealthIssue = null;
                    _bridgeWatchdog.ResetHealthyState();
                    await _bridgeWatchdog.CheckNowAsync();
                }
                UpdateConnectionOverlay();
            }
            finally
            {
                SetConnectionOverlayLoading(false);
            }
        }

        public async Task InitializeAsync()
        {
            var settings = await _settingsService.GetSettingsAsync();
            _visionService = ComponentVisionServiceFactory.Create(App.GoogleCloudVisionApiKey);
            await LoadActivitiesAsync();
            ClearWarehouseSession();
            NavigateToHome();
        }

        public void NavigateToHome()
        {
            CancelCameraSession();
            ClearWarehouseSession();
            ContentFrame.Navigate(typeof(HomePage));
        }

        public void NavigateToInventoryScan(InventoryActionMode mode)
            => ContentFrame.Navigate(typeof(InventoryScanPage), mode);

        public void NavigateToActivities() => ContentFrame.Navigate(typeof(ActivitiesPage));

        /// <summary>
        /// Hub inventario. A differenza di <see cref="NavigateToHome"/> non azzera la
        /// sessione di magazzino: entrare in consultazione non è un "abbandona tutto".
        /// </summary>
        public void NavigateToInventory()
        {
            CancelCameraSession();
            ContentFrame.Navigate(typeof(InventoryHubPage));
        }

        public void NavigateToAccessories() => ContentFrame.Navigate(typeof(AccessoriesPage));

        public void UpdateStatus(string message, bool ready = false, bool error = false)
        {
            StatusText.Text = message;
            StatusDot.Fill = error
                ? ThemeHelper.Brush("BrembanaRedBrush")
                : ready
                    ? ThemeHelper.Brush("BrembanaGreenBrush")
                    : ThemeHelper.Brush("BrembanaAmberBrush");
        }

        public void SetLoggedInUser(string? username)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                LoggedInUserPanel.Visibility = Visibility.Collapsed;
                return;
            }

            var name = username.Trim();
            LoggedInUserText.Text = name;
            UserAvatarText.Text = name[..1].ToUpperInvariant();
            LoggedInUserPanel.Visibility = Visibility.Visible;
        }

        private static string GetTrackingFilePath(string jobOrder)
        {
            Directory.CreateDirectory(App.TrackingDataPath);
            var safe = string.Concat(jobOrder.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
            return Path.Combine(App.TrackingDataPath, $"{safe}.json");
        }

        private static void SaveTrackingToFile(JobOrderTracker tracker)
        {
            try
            {
                var data = new JobTrackingData { JobOrder = tracker.JobOrderName, Updated = DateTime.Now };
                foreach (var comp in tracker.TrackedComponents)
                {
                    if (comp.IsOut)
                    {
                        data.Out[comp.ResourceName] = comp.OutgoingScanTime ?? DateTime.Now;
                    }
                    else if (comp.IncomingScanTime.HasValue)
                    {
                        data.In[comp.ResourceName] = comp.IncomingScanTime.Value;
                        if (comp.OutgoingScanTime.HasValue)
                            data.Out[comp.ResourceName] = comp.OutgoingScanTime.Value;
                    }
                }
                File.WriteAllText(GetTrackingFilePath(tracker.JobOrderName),
                    JsonConvert.SerializeObject(data, Formatting.Indented));
            }
            catch { }
        }

        private static void LoadTrackingFromFile(string jobOrder, JobOrderTracker tracker)
        {
            try
            {
                var path = GetTrackingFilePath(jobOrder);
                if (!File.Exists(path)) return;
                var data = JsonConvert.DeserializeObject<JobTrackingData>(File.ReadAllText(path));
                if (data == null) return;
                foreach (var comp in tracker.TrackedComponents)
                {
                    bool hasIn  = data.In.TryGetValue(comp.ResourceName, out var inTime);
                    bool hasOut = data.Out.TryGetValue(comp.ResourceName, out var outTime);
                    if (hasIn)
                    {
                        comp.IsOut = false;
                        comp.IncomingScanTime = inTime;
                        comp.OutgoingScanTime = hasOut ? outTime : inTime;
                    }
                    else if (hasOut)
                    {
                        comp.IsOut = true;
                        comp.OutgoingScanTime = outTime;
                    }
                }
            }
            catch { }
        }

        private static void DeleteTrackingFile(string jobOrder)
        {
            try
            {
                var path = GetTrackingFilePath(jobOrder);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        public async Task LoadActivitiesAsync(bool tryReconnect = false)
        {
            try
            {
                if (!App.BridgeConnected)
                {
                    if (!tryReconnect)
                    {
                        ShowToast(App.LastBridgeError ?? "Server non connesso. Controlla BRIDGE_* nel file .env.", InfoBarSeverity.Error);
                        UpdateStatus(App.LastBridgeError ?? "Errore: server non connesso", error: true);
                        ActivityLabels = Array.Empty<string>();
                        return;
                    }

                    if (!await App.TryConnectBridgeAsync())
                    {
                        ShowToast(App.LastBridgeError ?? "Server non connesso. Controlla BRIDGE_* nel file .env.", InfoBarSeverity.Error);
                        ActivityLabels = Array.Empty<string>();
                        return;
                    }
                }

                await App.BridgeData.SyncDatasetAsync();
                ActivityLabels = App.BridgeData.GetJobOrderLabels();

                UpdateStatus(UiStrings.StatusAvailable, ready: true);
            }
            catch (Exception ex)
            {
                App.MarkBridgeDisconnected(ex.Message);
                ShowToast($"Errore caricamento: {ex.Message}", InfoBarSeverity.Error);
                UpdateStatus($"Errore: {ex.Message}", error: true);
                ActivityLabels = Array.Empty<string>();
            }
        }

        public async Task RefreshDatasetAsync()
        {
            var jobOrder = _currentTracker?.JobOrderName;
            UpdateStatus(UiStrings.StatusRefreshing);
            await LoadActivitiesAsync(tryReconnect: true);

            if (!App.BridgeConnected)
            {
                DatasetRefreshed?.Invoke();
                return;
            }

            if (!string.IsNullOrEmpty(jobOrder))
            {
                var attivita = App.BridgeData.FindByActivityId(jobOrder);
                if (attivita != null)
                {
                    var tracker = App.BridgeData.BuildTracker(attivita);
                    LoadTrackingFromFile(jobOrder, tracker);
                    ComputeJobOrderStatus(tracker);
                    _currentTracker = tracker;
                    _sessionAttivita = attivita;
                }
            }

            if (ActivityLabels.Count == 0)
                ShowToast("Nessuna attività aperta sul server (o tutte completate).", InfoBarSeverity.Warning);
            else
                ShowToast(UiStrings.FormatLoadedActivities(ActivityLabels.Count), InfoBarSeverity.Success);

            DatasetRefreshed?.Invoke();
        }

        private async void RefreshDataset_Click(object sender, RoutedEventArgs e)
        {
            BtnRefreshDataset.IsEnabled = false;
            try
            {
                await RefreshDatasetAsync();
            }
            finally
            {
                BtnRefreshDataset.IsEnabled = true;
            }
        }

        public async Task<bool> StartSessionAsync(AttivitaDto attivita)
        {
            _isOutgoing = BridgeDataService.IsOutgoingOperation(attivita);
            var jobOrder = attivita.ActivityId;
            if (string.IsNullOrEmpty(jobOrder)) return false;

            try
            {
                if (!App.BridgeConnected)
                    throw new Exception("Server non connesso.");

                var userPlant = App.CurrentUserStabilimento?.Trim();
                if (string.IsNullOrWhiteSpace(userPlant))
                {
                    ShowToast(UiStrings.NoUserPlantActivities, InfoBarSeverity.Error);
                    return false;
                }

                if (!InventoryActionPolicy.CanStartActivitySession(attivita.Plant, userPlant))
                {
                    ShowToast(UiStrings.ActivityWrongPlant, InfoBarSeverity.Error);
                    return false;
                }

                var tracker = App.BridgeData.BuildTracker(attivita);
                LoadTrackingFromFile(jobOrder, tracker);
                ComputeJobOrderStatus(tracker);
                _currentTracker = tracker;
                _sessionAttivita = attivita;

                if (tracker.TrackedComponents.Count == 0)
                    ShowToast("L'attività non ha accessori assegnati.", InfoBarSeverity.Warning);

                return true;
            }
            catch (Exception ex)
            {
                ShowToast($"Errore caricamento: {ex.Message}", InfoBarSeverity.Error);
                UpdateStatus($"Errore: {ex.Message}", error: true);
                return false;
            }
        }

        public async Task<bool> TryMatchComponentByCodeAsync(string code, bool showToast = true)
        {
            if (_currentTracker == null || string.IsNullOrWhiteSpace(code))
                return false;

            var match = FindComponent(code);
            if (match == null)
            {
                if (showToast)
                    ShowToast($"\"{code}\" non trovato tra gli accessori.", InfoBarSeverity.Warning);
                return false;
            }

            match.IsSelectedForRegister = true;
            if (showToast)
                ShowToast($"{match.ResourceName} selezionato", InfoBarSeverity.Informational);
            return true;
        }

        private async Task<bool> TryMatchScanResultAsync(ComponentScanResult result, bool showToast = true)
        {
            var codes = new List<string>();
            if (!string.IsNullOrWhiteSpace(result.ComponentCode)
                && !result.ComponentCode.Equals("NO_COMPONENT_FOUND", StringComparison.OrdinalIgnoreCase))
            {
                codes.Add(result.ComponentCode.Trim());
            }

            if (result.AllCodes != null)
            {
                foreach (var candidate in result.AllCodes)
                {
                    if (!string.IsNullOrWhiteSpace(candidate)
                        && !codes.Contains(candidate.Trim(), StringComparer.OrdinalIgnoreCase))
                    {
                        codes.Add(candidate.Trim());
                    }
                }
            }

            foreach (var code in codes)
            {
                if (await TryMatchComponentByCodeAsync(code, showToast: false))
                    return true;
            }

            if (showToast && codes.Count > 0)
                ShowToast($"\"{codes[0]}\" non trovato tra gli accessori.", InfoBarSeverity.Warning);

            return false;
        }

        public async Task RunInventoryScanSessionAsync(InventoryActionMode mode)
        {
            _inventoryActionMode = mode;
            await RunCameraSessionCoreAsync(inventoryMode: true);
        }

        public async Task RunCameraScanSessionAsync(Action? onComponentsChanged = null)
            => await RunCameraSessionCoreAsync(inventoryMode: false, onComponentsChanged);

        private async Task RunCameraSessionCoreAsync(bool inventoryMode, Action? onComponentsChanged = null)
        {
            if (_visionService == null)
            {
                ShowToast("Servizio visione non configurato. Controlla GOOGLE_CLOUD_VISION_API_KEY nel file .env.", InfoBarSeverity.Error);
                return;
            }

            _cameraInventoryMode = inventoryMode;
            _cameraSessionRefreshCallback = onComponentsChanged;
            _cameraSessionCts = new CancellationTokenSource();
            _cameraSessionTcs = new TaskCompletionSource();
            ResetCameraOverlayUi();

            if (inventoryMode)
            {
                CameraCancelButton.Content = UiStrings.BackToInventory;
                CameraComponentListHost.Visibility = Visibility.Collapsed;
                ApplyInventoryScanCameraUiScale();
            }
            else
            {
                CameraComponentListHost.Visibility = Visibility.Visible;
                RefreshCameraComponentList();
            }

            CameraCaptureOverlay.Visibility = Visibility.Visible;
            StartCameraKeepAlive();

            var cancelledByUser = false;
            try
            {
                if (!await TryInitializeCameraOverlayAsync(_cameraSessionCts.Token))
                {
                    cancelledByUser = _cameraSessionCts.Token.IsCancellationRequested;
                    if (!cancelledByUser)
                        ShowToast(UiStrings.CameraOpenError, InfoBarSeverity.Error);
                    return;
                }

                await _cameraSessionTcs.Task;
                cancelledByUser = _cameraSessionCts.Token.IsCancellationRequested;
            }
            catch (UnauthorizedAccessException)
            {
                ShowToast(UiStrings.CameraPermissionDenied, InfoBarSeverity.Error);
            }
            catch (OperationCanceledException)
            {
                cancelledByUser = true;
            }
            catch (Exception ex) when (!_cameraSessionCts.Token.IsCancellationRequested)
            {
                ShowToast($"{UiStrings.CameraOpenError} {ex.Message}", InfoBarSeverity.Error);
            }
            finally
            {
                StopCameraKeepAlive();
                _cameraSessionRefreshCallback?.Invoke();
                await HideCameraOverlayAsync();
                _cameraSessionRefreshCallback = null;
                _cameraInventoryMode = false;
                CameraComponentListHost.Visibility = Visibility.Visible;

                if (!inventoryMode && !cancelledByUser && HasActiveSession &&
                    ContentFrame.CurrentSourcePageType != typeof(AccessoriesPage))
                {
                    NavigateToAccessories();
                }
            }
        }

        public async Task<string?> PickAndScanPhotoAsync()
        {
            if (_visionService == null)
            {
                ShowToast("Servizio visione non configurato. Controlla GOOGLE_CLOUD_VISION_API_KEY nel file .env.", InfoBarSeverity.Error);
                return null;
            }

            StorageFile? file;
            try
            {
                file = await PhotoCaptureHelper.GetPhotoWithoutOverlayAsync(WindowNative.GetWindowHandle(this));
            }
            catch (UnauthorizedAccessException)
            {
                ShowToast(UiStrings.CameraPermissionDenied, InfoBarSeverity.Error);
                return null;
            }
            catch (Exception ex)
            {
                ShowToast($"{UiStrings.CameraOpenError} {ex.Message}", InfoBarSeverity.Error);
                return null;
            }

            if (file == null) return null;
            return await ScanPhotoFileAsync(file);
        }

        private async Task<string?> ScanPhotoFileAsync(StorageFile file, bool useSavingOverlay = true, bool showToast = true)
        {
            try
            {
                if (useSavingOverlay)
                {
                    SavingOverlay.Visibility = Visibility.Visible;
                    SavingText.Text = UiStrings.Processing;
                }

                var result = await _visionService!.ScanComponentAsync(file.Path, file.Name);
                if (!result.Success)
                {
                    if (showToast)
                        ShowToast(result.ErrorMessage ?? "Errore scansione foto.", InfoBarSeverity.Error);
                    return null;
                }

                var code = GetBestScanCode(result);
                if (!string.IsNullOrEmpty(code))
                {
                    if (showToast)
                        ShowToast($"Codice estratto: {code}", InfoBarSeverity.Success);
                    return code;
                }

                if (showToast)
                    ShowToast("Nessun codice trovato nella foto.", InfoBarSeverity.Warning);
                return null;
            }
            catch (Exception ex)
            {
                if (showToast)
                    ShowToast($"Errore scansione foto: {ex.Message}", InfoBarSeverity.Error);
                return null;
            }
            finally
            {
                if (useSavingOverlay)
                    SavingOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private static string? GetBestScanCode(ComponentScanResult result)
        {
            if (result.AllCodes != null)
            {
                foreach (var candidate in result.AllCodes)
                {
                    if (!string.IsNullOrWhiteSpace(candidate)
                        && !candidate.Equals("NO_COMPONENT_FOUND", StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate.Trim();
                    }
                }
            }

            if (!string.IsNullOrEmpty(result.ComponentCode)
                && !result.ComponentCode.Equals("NO_COMPONENT_FOUND", StringComparison.OrdinalIgnoreCase))
            {
                return result.ComponentCode.Trim();
            }

            return null;
        }

        private async Task<ComponentScanResult?> ScanPhotoFullAsync(StorageFile file, bool showToast = true)
        {
            try
            {
                var result = await _visionService!.ScanComponentAsync(file.Path, file.Name);
                if (!result.Success)
                {
                    if (showToast)
                        ShowToast(result.ErrorMessage ?? "Errore scansione foto.", InfoBarSeverity.Error);
                    return result;
                }

                if (string.IsNullOrEmpty(result.ComponentCode) || result.ComponentCode == "NO_COMPONENT_FOUND")
                {
                    if (showToast)
                        ShowToast("Nessun codice trovato nella foto.", InfoBarSeverity.Warning);
                    return result;
                }

                if (showToast)
                    ShowToast($"Codice estratto: {result.ComponentCode}", InfoBarSeverity.Success);
                return result;
            }
            catch (Exception ex)
            {
                if (showToast)
                    ShowToast($"Errore scansione foto: {ex.Message}", InfoBarSeverity.Error);
                return null;
            }
        }

        private static bool IsRecognizedScan(ComponentScanResult? scan)
            => scan != null
               && scan.Success
               && !string.IsNullOrEmpty(GetBestScanCode(scan));

        private void UpdateCameraSectionTitle()
        {
            CameraSectionTitleText.Text =
                CameraSectionTitleHelper.ForCameraSession(_cameraInventoryMode, _inventoryActionMode);
            CameraSectionTitleText.Visibility = Visibility.Visible;
        }

        private void ShowCameraRecognitionBanner(string message)
        {
            CameraRecognitionBannerText.Text = message;
            CameraRecognitionBanner.Visibility = Visibility.Visible;
        }

        private void HideCameraRecognitionBanner()
            => CameraRecognitionBanner.Visibility = Visibility.Collapsed;

        private async Task ProcessInventoryScanAsync(ComponentScanResult scan)
        {
            if (!App.BridgeConnected)
            {
                ShowToast(UiStrings.InventoryBridgeRequired, InfoBarSeverity.Error);
                return;
            }

            var isMaintenance = _inventoryActionMode == InventoryActionMode.Maintenance;
            var userPlant = App.CurrentUserStabilimento?.Trim();
            if (!isMaintenance && string.IsNullOrWhiteSpace(userPlant))
            {
                ShowToast(UiStrings.NoUserPlant, InfoBarSeverity.Error);
                return;
            }

            var code = GetBestScanCode(scan) ?? scan.ComponentCode?.Trim() ?? "";
            var matches = App.BridgeData.FindInventoryMatches(code, scan);
            InventoryMatch? match = null;

            if (matches.Count > 1)
            {
                match = await InventoryScanDialogs.PickMatchAsync(Content.XamlRoot, matches);
                if (match == null)
                    return;
            }
            else if (matches.Count == 1)
            {
                match = matches[0];
            }

            var decision = InventoryActionPolicy.Decide(_inventoryActionMode, match, userPlant);
            switch (decision.Kind)
            {
                case InventoryActionKind.CreateNew:
                    await ProcessInventoryCreateAsync(scan, userPlant!);
                    break;
                case InventoryActionKind.AlreadyPresent:
                    await InventoryScanDialogs.ShowFoundAsync(Content.XamlRoot, decision.Match!);
                    break;
                case InventoryActionKind.Move:
                    await ProcessInventoryMoveAsync(decision.Match!, userPlant!);
                    break;
                case InventoryActionKind.EnableMaintenance:
                    await ProcessInventoryMaintenanceAsync(decision.Match!, enable: true);
                    break;
                case InventoryActionKind.DisableMaintenance:
                    await ProcessInventoryMaintenanceAsync(decision.Match!, enable: false);
                    break;
                case InventoryActionKind.UnsupportedType:
                    ShowToast(UiStrings.InventoryMaintenanceUnsupported, InfoBarSeverity.Warning);
                    break;
                default:
                    ShowToast(
                        string.IsNullOrWhiteSpace(code)
                            ? UiStrings.FormatInventoryNotFoundForMove("—")
                            : UiStrings.FormatInventoryNotFoundForMove(code),
                        InfoBarSeverity.Warning);
                    break;
            }
        }

        private async Task ProcessInventoryCreateAsync(ComponentScanResult scan, string userPlant)
        {
            var initialForm = App.BridgeData.BuildCreateForm(scan, userPlant);
            var form = await InventoryScanDialogs.ShowCreateAsync(Content.XamlRoot, initialForm);
            if (form == null)
                return;

            if (string.IsNullOrWhiteSpace(form.Plant))
                form.Plant = userPlant;

            // L'uid può essere stato riscritto a mano nel form dopo la scansione, quindi
            // il controllo fatto sul codice scansionato non basta più. Il server rifiuta
            // i duplicati solo dentro la stessa tabella: questo li intercetta anche tra tipi diversi.
            var existing = App.BridgeData.FindExistingUid(form.Uid, form.Table);
            if (existing != null)
            {
                await InventoryScanDialogs.ShowFoundAsync(Content.XamlRoot, existing);
                return;
            }

            try
            {
                await App.BridgeData.CreateInventoryResourceAsync(form);
                DatasetRefreshed?.Invoke();
                ShowToast(UiStrings.InventoryCreated, InfoBarSeverity.Success);
            }
            catch (BridgeApiException ex) when (ex.StatusCode == 409)
            {
                // Duplicato creato da un altro tablet dopo l'ultima sincronizzazione.
                ShowToast(ex.Message, InfoBarSeverity.Warning);
            }
            catch (Exception ex)
            {
                ShowToast($"Errore creazione componente: {ex.Message}", InfoBarSeverity.Error);
            }
        }

        private async Task ProcessInventoryMoveAsync(InventoryMatch match, string userPlant)
        {
            if (!await InventoryScanDialogs.ShowMoveAsync(Content.XamlRoot, match, userPlant))
                return;

            try
            {
                await App.BridgeData.MoveInventoryResourceAsync(match, userPlant);
                DatasetRefreshed?.Invoke();
                ShowToast(UiStrings.InventoryMoved, InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowToast($"Errore spostamento componente: {ex.Message}", InfoBarSeverity.Error);
            }
        }

        private async Task ProcessInventoryMaintenanceAsync(InventoryMatch match, bool enable)
        {
            if (!await InventoryScanDialogs.ShowMaintenanceAsync(Content.XamlRoot, match, enable))
                return;

            try
            {
                await App.BridgeData.SetMaintenanceAsync(match, enable);
                DatasetRefreshed?.Invoke();
                ShowToast(
                    enable ? UiStrings.InventoryMaintenanceEnabled : UiStrings.InventoryMaintenanceDisabled,
                    InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowToast($"Errore manutenzione: {ex.Message}", InfoBarSeverity.Error);
            }
        }

        public void FillComponentsPanel(StackPanel panel, Action refresh)
        {
            panel.Children.Clear();
            if (_currentTracker == null) return;

            var ordered = _currentTracker.TrackedComponents
                .OrderByDescending(c => c.IsOut)
                .ThenByDescending(c => c.OutgoingScanTime.HasValue || c.IncomingScanTime.HasValue)
                .ThenBy(c => c.ResourceName)
                .ToList();

            if (ordered.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = UiStrings.NoComponents,
                    Opacity = 0.6, FontSize = 14, Margin = new Thickness(0, 16, 0, 0),
                    Foreground = ThemeHelper.Brush("BrembanaTextMutedBrush")
                });
                return;
            }

            for (int i = 0; i < ordered.Count; i += CardLayoutHelper.CardsPerRow)
            {
                var rowCards = new List<FrameworkElement>();
                for (int c = 0; c < CardLayoutHelper.CardsPerRow && i + c < ordered.Count; c++)
                    rowCards.Add(CreateComponentCard(ordered[i + c], refresh));

                panel.Children.Add(CardLayoutHelper.CreateRow(rowCards, panel.ActualWidth));
            }
        }

        private TrackedComponent? FindComponent(string code)
        {
            if (_currentTracker == null || string.IsNullOrWhiteSpace(code))
                return null;

            var normalized = code.Trim();
            return _currentTracker.TrackedComponents.FirstOrDefault(c =>
                ComponentCodesMatch(c.ResourceName, normalized) ||
                ComponentCodesMatch(c.Modello, normalized));
        }

        private static bool ComponentCodesMatch(string candidate, string code)
        {
            var left = candidate.Trim();
            var right = code.Trim();
            if (left.Length == 0 || right.Length == 0)
                return false;

            if (left.Equals(right, StringComparison.OrdinalIgnoreCase))
                return true;

            return left.Contains(right, StringComparison.OrdinalIgnoreCase)
                || right.Contains(left, StringComparison.OrdinalIgnoreCase);
        }

        private static void ApplyComponentState(TrackedComponent comp, bool isOutgoing)
        {
            if (isOutgoing)
            {
                comp.IsOut = true;
                comp.OutgoingScanTime ??= DateTime.Now;
                comp.IncomingScanTime = null;
            }
            else
            {
                comp.IsOut = false;
                comp.IncomingScanTime = DateTime.Now;
                comp.OutgoingScanTime ??= DateTime.Now;
            }
            comp.IsSelectedForRegister = false;
        }

        public async Task<bool> SaveSessionAsync()
        {
            if (_currentTracker == null || !App.BridgeConnected) return false;

            var selected = _currentTracker.TrackedComponents.Where(c => c.IsSelectedForRegister).ToList();
            if (selected.Count == 0)
            {
                ShowToast("Seleziona almeno un accessorio.", InfoBarSeverity.Warning);
                return false;
            }

            try
            {
                SavingOverlay.Visibility = Visibility.Visible;
                SavingText.Text = UiStrings.Saving;
                foreach (var comp in selected)
                    ApplyComponentState(comp, _isOutgoing);

                SaveTrackingToFile(_currentTracker);
                await UpdateActivityStatusesAsync();

                var verb = _isOutgoing ? "uscita" : "rientro";
                ShowToast($"{selected.Count} accessori registrati in {verb}", InfoBarSeverity.Success);

                _currentTracker = null;
                _sessionAttivita = null;
                NavigateToHome();
                return true;
            }
            catch (Exception ex)
            {
                ShowToast($"Errore salvataggio: {ex.Message}", InfoBarSeverity.Error);
                return false;
            }
            finally
            {
                SavingOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private async Task UpdateActivityStatusesAsync()
        {
            if (_currentTracker == null || !App.BridgeConnected) return;

            foreach (var activity in _currentTracker.MatchedActivities)
            {
                var comps = _currentTracker.TrackedComponents
                    .Where(c => activity.AssignedResources.Contains(c.ResourceName, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (comps.Count == 0) continue;

                bool anyOut = comps.Any(c => c.IsOut);
                bool anyScanned = comps.Any(c => c.OutgoingScanTime.HasValue || c.IncomingScanTime.HasValue);
                bool allReturned = comps.All(c => !c.IsOut && (c.OutgoingScanTime.HasValue || c.IncomingScanTime.HasValue));

                if (allReturned && anyScanned && !activity.ProjectStatus.Equals("Finished", StringComparison.OrdinalIgnoreCase))
                {
                    activity.ProjectStatus = "Finished";
                    activity.StatusWasUpdated = true;
                    if (!activity.ActualFinishSet) { activity.ActualFinishSet = true; activity.DateWasUpdated = true; }
                }
                else if ((anyOut || anyScanned) &&
                    !activity.ProjectStatus.Equals("Ongoing", StringComparison.OrdinalIgnoreCase) &&
                    !activity.ProjectStatus.Equals("Finished", StringComparison.OrdinalIgnoreCase))
                {
                    activity.ProjectStatus = "Ongoing";
                    activity.StatusWasUpdated = true;
                    if (!activity.ActualStartSet) { activity.ActualStartSet = true; activity.DateWasUpdated = true; }
                }
            }

            var toSave = _currentTracker.MatchedActivities.Where(a => a.DateWasUpdated || a.StatusWasUpdated).ToList();
            if (toSave.Count > 0)
            {
                await App.BridgeData.SaveActivityDatesAsync(toSave);
                foreach (var act in toSave) { act.DateWasUpdated = false; act.StatusWasUpdated = false; }
            }

            ComputeJobOrderStatus(_currentTracker);

            if (_currentTracker.JobOrderStatus.Equals("Finished", StringComparison.OrdinalIgnoreCase))
                DeleteTrackingFile(_currentTracker.JobOrderName);
        }

        private static void ComputeJobOrderStatus(JobOrderTracker tracker)
        {
            var comps = tracker.TrackedComponents;
            if (comps.Count == 0) { tracker.JobOrderStatus = "Pending"; return; }
            bool anyScanned = comps.Any(c => c.OutgoingScanTime.HasValue || c.IncomingScanTime.HasValue);
            bool anyOut = comps.Any(c => c.IsOut);
            bool allReturned = comps.All(c => !c.IsOut && (c.OutgoingScanTime.HasValue || c.IncomingScanTime.HasValue));
            if (allReturned && anyScanned) tracker.JobOrderStatus = "Finished";
            else if (anyOut || anyScanned) tracker.JobOrderStatus = "Ongoing";
            else tracker.JobOrderStatus = "Pending";
        }

        private Grid CreateComponentCard(TrackedComponent comp, Action refresh)
        {
            bool isSelected = comp.IsSelectedForRegister;
            bool isScanned = comp.OutgoingScanTime.HasValue || comp.IncomingScanTime.HasValue;
            bool isOut = comp.IsOut;

            Color accentColor;
            string statusLabel;

            if (isSelected)
            {
                accentColor = ThemeHelper.GetColor("BrembanaAccentBrush");
                statusLabel = UiStrings.CardStatusSelected;
            }
            else if (isOut)
            {
                accentColor = ThemeHelper.GetColor("BrembanaAmberBrush");
                statusLabel = UiStrings.CardStatusOutgoing;
            }
            else if (isScanned)
            {
                accentColor = ThemeHelper.GetColor("BrembanaGreenBrush");
                statusLabel = UiStrings.CardStatusReturned;
            }
            else
            {
                accentColor = ThemeHelper.GetColor("BrembanaTextMutedBrush");
                statusLabel = UiStrings.CardStatusPending;
            }

            var typeName = UiStrings.FormatComponentTypeName(comp.Categoria, comp.TipoKey);
            var code = string.IsNullOrWhiteSpace(comp.ResourceName) ? "—" : comp.ResourceName.Trim();
            var measure = UiStrings.FormatComponentMeasure(comp.SpecLabel, comp.SpecValue, comp.SpecUnit, comp.SpecDetail);

            var cardContent = new Grid
            {
                Padding = new Thickness(18, 18, 16, 18),
                ColumnSpacing = 12,
                MinHeight = 56,
                VerticalAlignment = VerticalAlignment.Center
            };
            cardContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cardContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var infoPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center
            };

            infoPanel.Children.Add(new TextBlock
            {
                Text = typeName,
                FontSize = 22,
                LineHeight = 28,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = ThemeHelper.Brush("BrembanaTextBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });

            infoPanel.Children.Add(new TextBlock
            {
                Text = code,
                FontSize = 22,
                LineHeight = 28,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(accentColor),
                VerticalAlignment = VerticalAlignment.Center
            });

            if (!string.IsNullOrWhiteSpace(measure))
            {
                infoPanel.Children.Add(new TextBlock
                {
                    Text = "·",
                    FontSize = 20,
                    LineHeight = 28,
                    Foreground = ThemeHelper.Brush("BrembanaTextSubtleBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 0, 0)
                });
                infoPanel.Children.Add(new TextBlock
                {
                    Text = measure,
                    FontSize = 20,
                    LineHeight = 28,
                    Foreground = ThemeHelper.Brush("BrembanaTextMutedBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            Grid.SetColumn(infoPanel, 0);
            cardContent.Children.Add(infoPanel);

            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(25, accentColor.R, accentColor.G, accentColor.B)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 6, 12, 6),
                VerticalAlignment = VerticalAlignment.Center,
                MinHeight = 30
            };
            badge.Child = new TextBlock
            {
                Text = statusLabel,
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(accentColor),
                VerticalAlignment = VerticalAlignment.Center,
                LineHeight = 20
            };
            Grid.SetColumn(badge, 1);
            cardContent.Children.Add(badge);

            var borderAlpha = (byte)(isSelected ? 200 : 100);
            var surface = ThemeHelper.GetColor("BrembanaSurfaceBrush");
            var button = new Button
            {
                Content = cardContent,
                Tag = comp,
                Style = (Style)Application.Current.Resources["BrembanaCardButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                BorderBrush = new SolidColorBrush(Color.FromArgb(borderAlpha, accentColor.R, accentColor.G, accentColor.B)),
                BorderThickness = new Thickness(isSelected ? 2 : 1.5),
                Background = isSelected
                    ? new SolidColorBrush(Color.FromArgb(255,
                        (byte)Math.Min(255, surface.R + (accentColor.R - surface.R) * 0.12),
                        (byte)Math.Min(255, surface.G + (accentColor.G - surface.G) * 0.12),
                        (byte)Math.Min(255, surface.B + (accentColor.B - surface.B) * 0.12)))
                    : ThemeHelper.Brush("BrembanaSurfaceBrush"),
                MinHeight = 88
            };
            button.Click += (_, _) =>
            {
                if (_currentTracker == null) return;
                comp.IsSelectedForRegister = !comp.IsSelectedForRegister;
                refresh();
            };
            ToolTipService.SetToolTip(button, _isOutgoing
                ? UiStrings.CardTooltipOutgoing
                : UiStrings.CardTooltipIncoming);
            return CardHoverHelper.WrapCard(button, accentColor, pinned: isSelected);
        }

        private void RefreshCameraComponentList()
        {
            CameraComponentList.Children.Clear();
            if (_currentTracker == null) return;

            var ordered = _currentTracker.TrackedComponents
                .OrderBy(c => UiStrings.FormatComponentTypeName(c.Categoria, c.TipoKey))
                .ThenBy(c => c.ResourceName)
                .ToList();

            foreach (var comp in ordered)
            {
                var typeName = UiStrings.FormatComponentTypeName(comp.Categoria, comp.TipoKey);
                var code = string.IsNullOrWhiteSpace(comp.ResourceName) ? "—" : comp.ResourceName.Trim();

                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var label = new TextBlock
                {
                    Text = $"{typeName} {code}",
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                    FontSize = 19,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.NoWrap,
                    VerticalAlignment = VerticalAlignment.Center
                };
                label.Shadow = new ThemeShadow();
                row.Children.Add(label);

                if (comp.IsSelectedForRegister)
                {
                    var check = new Border
                    {
                        Width = 40,
                        Height = 40,
                        CornerRadius = new CornerRadius(20),
                        Background = ThemeHelper.Brush("BrembanaGreenBrush"),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new FontIcon
                        {
                            Glyph = "\uE73E",
                            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                            FontSize = 22,
                            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    };
                    check.Shadow = new ThemeShadow();
                    row.Children.Add(check);
                }

                CameraComponentList.Children.Add(row);
            }
        }

        private void ResetCameraOverlayUi()
        {
            ResetCameraOverlayUiScale();
            UpdateCameraSectionTitle();
            HideCameraRecognitionBanner();
            CameraPreviewHost.Visibility = Visibility.Visible;
            CameraStatusText.Visibility = Visibility.Collapsed;
            CameraProcessingOverlay.Visibility = Visibility.Collapsed;
            CameraLoadingRing.IsActive = true;
            CameraLoadingRing.Visibility = Visibility.Visible;
            CameraCaptureButton.Visibility = Visibility.Collapsed;
            CameraCaptureButton.IsEnabled = false;
            CameraCancelButton.Content = _cameraInventoryMode ? UiStrings.BackToInventory : UiStrings.CameraBack;
            CameraCancelButton.IsEnabled = true;
            HideCameraScanGuide();
        }

        private void ShowCameraScanGuide()
        {
            PositionCameraScanGuide();
            CameraScanGuideHost.Visibility = Visibility.Visible;
        }

        private void HideCameraScanGuide()
            => CameraScanGuideHost.Visibility = Visibility.Collapsed;

        private void PositionCameraScanGuide()
        {
            var width = CameraCaptureOverlay.ActualWidth;
            var height = CameraCaptureOverlay.ActualHeight;
            if (width <= 0 || height <= 0)
                return;

            var guide = CameraScanGuideHelper.GetGuideRect(width, height);

            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(CameraScanGuideDimTop, 0);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(CameraScanGuideDimTop, 0);
            CameraScanGuideDimTop.Width = width;
            CameraScanGuideDimTop.Height = guide.Y;

            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(CameraScanGuideDimBottom, 0);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(CameraScanGuideDimBottom, guide.Y + guide.Height);
            CameraScanGuideDimBottom.Width = width;
            CameraScanGuideDimBottom.Height = Math.Max(0, height - guide.Y - guide.Height);

            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(CameraScanGuideDimLeft, 0);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(CameraScanGuideDimLeft, guide.Y);
            CameraScanGuideDimLeft.Width = guide.X;
            CameraScanGuideDimLeft.Height = guide.Height;

            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(CameraScanGuideDimRight, guide.X + guide.Width);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(CameraScanGuideDimRight, guide.Y);
            CameraScanGuideDimRight.Width = Math.Max(0, width - guide.X - guide.Width);
            CameraScanGuideDimRight.Height = guide.Height;

            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(CameraScanGuideFrame, guide.X);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(CameraScanGuideFrame, guide.Y);
            CameraScanGuideFrame.Width = guide.Width;
            CameraScanGuideFrame.Height = guide.Height;
            CameraScanGuideFrame.BorderThickness = new Thickness(_cameraScanGuideBorderThickness);
        }

        private (double Width, double Height) GetCameraPreviewDimensions()
        {
            var previewWidth = CameraCaptureOverlay.ActualWidth;
            var previewHeight = CameraCaptureOverlay.ActualHeight;
            if (previewWidth <= 0 || previewHeight <= 0)
            {
                previewWidth = CameraPreviewElement.ActualWidth;
                previewHeight = CameraPreviewElement.ActualHeight;
            }

            return (previewWidth, previewHeight);
        }

        private async Task ApplyFocusRegionsOfInterestAsync()
        {
            if (_cameraMediaCapture == null)
                return;

            try
            {
                var roiControl = _cameraMediaCapture.VideoDeviceController.RegionsOfInterestControl;
                if (!roiControl.AutoFocusSupported)
                    return;

                var (previewWidth, previewHeight) = GetCameraPreviewDimensions();
                if (previewWidth <= 0 || previewHeight <= 0)
                    return;

                var (imageWidth, imageHeight) = GetCameraMappingDimensions();
                if (imageWidth == 0 || imageHeight == 0)
                    return;

                var guide = CameraScanGuideHelper.GetGuideRect(previewWidth, previewHeight);
                var normalized = CameraScanGuideHelper.ClampNormalizedRect(
                    CameraScanGuideHelper.MapGuideToNormalizedFocusBounds(
                        guide, previewWidth, previewHeight, imageWidth, imageHeight));

                var regions = new List<RegionOfInterest>
                {
                    new()
                    {
                        AutoFocusEnabled = true,
                        AutoWhiteBalanceEnabled = roiControl.AutoWhiteBalanceSupported,
                        AutoExposureEnabled = roiControl.AutoExposureSupported,
                        Bounds = normalized,
                        BoundsNormalized = true,
                        Weight = 100
                    }
                };

                await roiControl.SetRegionsAsync(regions);
            }
            catch
            {
                // Il ROI focus non è critico per l'avvio della fotocamera.
            }
        }

        private (uint Width, uint Height) GetCameraMappingDimensions()
        {
            var frameSource = GetCameraFrameSource();
            var frameWidth = frameSource?.CurrentFormat.VideoFormat.Width ?? 0;
            var frameHeight = frameSource?.CurrentFormat.VideoFormat.Height ?? 0;
            if (frameWidth > 0 && frameHeight > 0)
                return (frameWidth, frameHeight);

            var photoProps = _activePhotoEncoding ?? GetOcrPhotoEncodingProperties();
            if (photoProps is { Width: > 0, Height: > 0 })
                return (photoProps.Width, photoProps.Height);

            return (1920, 1080);
        }

        private void ApplyInventoryScanCameraUiScale()
        {
            CameraCancelButton.MinHeight = 72;
            CameraCancelButton.MinWidth = 190;
            CameraCancelButton.FontSize = 24;
            CameraCancelButton.Padding = new Thickness(32, 14, 32, 14);
            CameraCancelButton.CornerRadius = new CornerRadius(32);

            CameraCaptureButton.Width = 110;
            CameraCaptureButton.Height = 110;
            CameraCaptureButton.CornerRadius = new CornerRadius(55);
            CameraCaptureButton.BorderThickness = new Thickness(5);

            CameraStatusText.FontSize = 26;
            CameraStatusText.MaxWidth = 560;

            CameraProcessingText.FontSize = 24;
            CameraProcessingRing.Width = 60;
            CameraProcessingRing.Height = 60;

            CameraLoadingRing.Width = 60;
            CameraLoadingRing.Height = 60;

            CameraSectionTitleText.FontSize = 20;
            CameraRecognitionBanner.Margin = new Thickness(24, 0, 24, 156);
            CameraRecognitionBannerText.FontSize = 22;
            CameraManualButton.FontSize = 22;
            CameraManualButton.MinWidth = 280;
            CameraManualButton.MinHeight = 66;
            CameraManualButton.Padding = new Thickness(28, 14, 28, 14);
            _cameraScanGuideBorderThickness = 4;
        }

        private void ResetCameraOverlayUiScale()
        {
            CameraCancelButton.MinHeight = 56;
            CameraCancelButton.MinWidth = 140;
            CameraCancelButton.FontSize = 18;
            CameraCancelButton.Padding = new Thickness(24, 12, 24, 12);
            CameraCancelButton.CornerRadius = new CornerRadius(28);

            CameraCaptureButton.Width = 88;
            CameraCaptureButton.Height = 88;
            CameraCaptureButton.CornerRadius = new CornerRadius(44);
            CameraCaptureButton.BorderThickness = new Thickness(4);

            CameraStatusText.FontSize = 20;
            CameraStatusText.MaxWidth = 480;

            CameraProcessingText.FontSize = 18;
            CameraProcessingRing.Width = 48;
            CameraProcessingRing.Height = 48;

            CameraLoadingRing.Width = 48;
            CameraLoadingRing.Height = 48;

            CameraSectionTitleText.FontSize = 16;
            CameraRecognitionBanner.Margin = new Thickness(24, 0, 24, 132);
            CameraRecognitionBannerText.FontSize = 18;
            CameraManualButton.FontSize = 18;
            CameraManualButton.MinWidth = 220;
            CameraManualButton.MinHeight = 56;
            CameraManualButton.Padding = new Thickness(24, 12, 24, 12);
            _cameraScanGuideBorderThickness = 3;
        }

        private void StartCameraKeepAlive()
        {
            StopCameraKeepAlive();
            _cameraKeepAliveCts = new CancellationTokenSource();
            _cameraKeepAliveTask = Task.Run(() => CameraKeepAliveLoopAsync(_cameraKeepAliveCts.Token));
        }

        private void StopCameraKeepAlive()
        {
            if (_cameraKeepAliveCts == null)
                return;
            _cameraKeepAliveCts.Cancel();
            _cameraKeepAliveCts.Dispose();
            _cameraKeepAliveCts = null;
            _cameraKeepAliveTask = null;
        }

        private static async Task CameraKeepAliveLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (App.BridgeConnected)
                        await App.BridgeClient.SendHeartbeatAsync(token);
                }
                catch
                {
                    // Ignora errori keepalive camera.
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private void ReturnToLiveCamera()
        {
            if (_cameraSessionCts?.Token.IsCancellationRequested == true)
                return;

            CameraPreviewHost.Visibility = Visibility.Visible;
            if (_cameraMediaPlayer == null)
                RestartCameraPreview();
            else
                _cameraMediaPlayer.Play();

            _ = ConfigureCameraFocusAsync(_cameraSessionCts?.Token ?? CancellationToken.None);

            CameraCaptureButton.Visibility = Visibility.Visible;
            CameraCaptureButton.IsEnabled = true;
            CameraCancelButton.Content = _cameraInventoryMode ? UiStrings.BackToInventory : UiStrings.CameraBack;
            CameraCancelButton.IsEnabled = true;
            ShowCameraScanGuide();
        }

        private MediaFrameSource? GetCameraFrameSource()
        {
            if (_cameraMediaCapture == null)
                return null;

            return _cameraMediaCapture.FrameSources.Values.FirstOrDefault(source =>
                    source.Info.MediaStreamType == MediaStreamType.VideoPreview &&
                    source.Info.SourceKind == MediaFrameSourceKind.Color)
                ?? _cameraMediaCapture.FrameSources.Values.FirstOrDefault(source =>
                    source.Info.MediaStreamType == MediaStreamType.VideoRecord &&
                    source.Info.SourceKind == MediaFrameSourceKind.Color);
        }

        private void RestartCameraPreview()
        {
            if (_cameraSessionCts?.Token.IsCancellationRequested == true)
                return;

            var frameSource = GetCameraFrameSource();
            if (frameSource == null)
                return;

            DetachCameraPreviewPlayer();

            _cameraMediaPlayer = new MediaPlayer
            {
                RealTimePlayback = true,
                AutoPlay = false
            };
            _cameraMediaPlayer.Source = MediaSource.CreateFromMediaFrameSource(frameSource);
            CameraPreviewElement.SetMediaPlayer(_cameraMediaPlayer);
            _cameraMediaPlayer.Play();
        }

        private void DetachCameraPreviewPlayer()
        {
            try
            {
                _cameraMediaPlayer?.Pause();
                CameraPreviewElement.SetMediaPlayer(null);
                if (_cameraMediaPlayer?.Source != null)
                    _cameraMediaPlayer.Source = null;
                _cameraMediaPlayer?.Dispose();
            }
            catch
            {
                // Ignora errori in chiusura player.
            }
            finally
            {
                _cameraMediaPlayer = null;
            }
        }

        private ImageEncodingProperties? GetOcrPhotoEncodingProperties()
        {
            if (_cameraMediaCapture == null)
                return null;

            return _cameraMediaCapture.VideoDeviceController
                .GetAvailableMediaStreamProperties(MediaStreamType.Photo)
                .OfType<ImageEncodingProperties>()
                .Where(p => p.Width > 0 && p.Height > 0)
                .OrderByDescending(p => (long)p.Width * p.Height)
                .FirstOrDefault();
        }

        private IEnumerable<ImageEncodingProperties> GetOcrPhotoEncodingPropertyCandidates()
        {
            if (_cameraMediaCapture == null)
                yield break;

            var candidates = _cameraMediaCapture.VideoDeviceController
                .GetAvailableMediaStreamProperties(MediaStreamType.Photo)
                .OfType<ImageEncodingProperties>()
                .Where(p => p.Width > 0 && p.Height > 0)
                .ToList();

            var max = candidates
                .OrderByDescending(p => (long)p.Width * p.Height)
                .FirstOrDefault();
            if (max != null)
                yield return max;

            const long targetPixels = 1920L * 1080L;
            var hd = candidates
                .OrderBy(p => Math.Abs((long)p.Width * p.Height - targetPixels))
                .FirstOrDefault();
            if (hd != null && (max == null || hd.Width != max.Width || hd.Height != max.Height))
                yield return hd;
        }

        private VideoEncodingProperties? GetBalancedPreviewProperties()
        {
            if (_cameraMediaCapture == null)
                return null;

            const long targetPixels = 960L * 540L;
            return _cameraMediaCapture.VideoDeviceController
                .GetAvailableMediaStreamProperties(MediaStreamType.VideoPreview)
                .OfType<VideoEncodingProperties>()
                .Where(p => p.Width > 0 && p.Height > 0)
                .OrderBy(p => Math.Abs((long)p.Width * p.Height - targetPixels))
                .FirstOrDefault();
        }

        private async Task ConfigurePreviewStreamAsync(CancellationToken cancellationToken = default)
        {
            if (_cameraMediaCapture == null)
                return;

            var previewProps = GetBalancedPreviewProperties();
            if (previewProps == null)
                return;

            try
            {
                await _cameraMediaCapture.VideoDeviceController
                    .SetMediaStreamPropertiesAsync(MediaStreamType.VideoPreview, previewProps)
                    .AsTask()
                    .WaitAsync(cancellationToken);
            }
            catch
            {
                // L'anteprima userà la risoluzione disponibile.
            }
        }

        private async Task ConfigurePhotoStreamAsync(CancellationToken cancellationToken = default)
        {
            if (_cameraMediaCapture == null)
                return;

            _activePhotoEncoding = null;

            foreach (var photoProps in GetOcrPhotoEncodingPropertyCandidates())
            {
                try
                {
                    await _cameraMediaCapture.VideoDeviceController
                        .SetMediaStreamPropertiesAsync(MediaStreamType.Photo, photoProps)
                        .AsTask()
                        .WaitAsync(cancellationToken);
                    _activePhotoEncoding = photoProps;
                    return;
                }
                catch
                {
                    // Prova la risoluzione successiva.
                }
            }
        }

        private async Task ConfigureCaptureStreamsAsync(CancellationToken cancellationToken = default)
        {
            await ConfigurePreviewStreamAsync(cancellationToken);
            await ConfigurePhotoStreamAsync(cancellationToken);
        }

        private ImageEncodingProperties CreatePhotoEncodingProperties()
        {
            var props = ImageEncodingProperties.CreateJpeg();
            var ocrProps = _activePhotoEncoding ?? GetOcrPhotoEncodingProperties();
            if (ocrProps is { Width: > 0, Height: > 0 })
            {
                props.Width = ocrProps.Width;
                props.Height = ocrProps.Height;
            }

            return props;
        }

        private async Task ConfigureCameraFocusAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_cameraMediaCapture == null)
                return;

            var focus = _cameraMediaCapture.VideoDeviceController.FocusControl;
            if (!focus.Supported)
                return;

            await focus.UnlockAsync();
            await ApplyFocusRegionsOfInterestAsync();

            var mode = focus.SupportedFocusModes.Contains(FocusMode.Continuous)
                ? FocusMode.Continuous
                : focus.SupportedFocusModes.Contains(FocusMode.Auto)
                    ? FocusMode.Auto
                    : focus.SupportedFocusModes.FirstOrDefault();

            var range = focus.SupportedFocusRanges.Contains(AutoFocusRange.Macro)
                ? AutoFocusRange.Macro
                : focus.SupportedFocusRanges.Contains(AutoFocusRange.FullRange)
                    ? AutoFocusRange.FullRange
                    : focus.SupportedFocusRanges.FirstOrDefault();

            focus.Configure(new FocusSettings { Mode = mode, AutoFocusRange = range });

            if (mode != FocusMode.Continuous)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await focus.FocusAsync().AsTask().WaitAsync(cancellationToken);
            }
        }

        private async Task EnsureFocusBeforeCaptureAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_cameraMediaCapture == null)
                return;

            var focus = _cameraMediaCapture.VideoDeviceController.FocusControl;
            if (!focus.Supported)
            {
                await Task.Delay(100, cancellationToken);
                return;
            }

            await focus.UnlockAsync();
            await ApplyFocusRegionsOfInterestAsync();

            if (focus.FocusState == MediaCaptureFocusState.Focused)
            {
                await Task.Delay(50, cancellationToken);
                return;
            }

            if (focus.FocusState == MediaCaptureFocusState.Searching)
            {
                await WaitForFocusLockAsync(focus, cancellationToken);
                return;
            }

            var focused = await TryAutoFocusAsync(focus, cancellationToken);
            if (!focused)
            {
                var mode = focus.SupportedFocusModes.Contains(FocusMode.Auto)
                    ? FocusMode.Auto
                    : focus.SupportedFocusModes.FirstOrDefault();

                focus.Configure(new FocusSettings
                {
                    Mode = mode,
                    AutoFocusRange = AutoFocusRange.FullRange
                });

                cancellationToken.ThrowIfCancellationRequested();
                await focus.FocusAsync().AsTask().WaitAsync(cancellationToken);
                await WaitForFocusLockAsync(focus, cancellationToken);
            }

            await Task.Delay(50, cancellationToken);
        }

        private static async Task<bool> TryAutoFocusAsync(FocusControl focus, CancellationToken cancellationToken)
        {
            foreach (var range in new[] { AutoFocusRange.Macro, AutoFocusRange.FullRange })
            {
                if (!focus.SupportedFocusRanges.Contains(range))
                    continue;

                focus.Configure(new FocusSettings
                {
                    Mode = FocusMode.Single,
                    AutoFocusRange = range
                });

                cancellationToken.ThrowIfCancellationRequested();
                await focus.FocusAsync().AsTask().WaitAsync(cancellationToken);
                if (await WaitForFocusLockAsync(focus, cancellationToken))
                    return true;
            }

            return focus.FocusState == MediaCaptureFocusState.Focused;
        }

        private static async Task<bool> WaitForFocusLockAsync(FocusControl focus, CancellationToken cancellationToken)
        {
            if (!focus.WaitForFocusSupported)
            {
                await Task.Delay(120, cancellationToken);
                return true;
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(1500);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (focus.FocusState == MediaCaptureFocusState.Focused)
                    return true;
                if (focus.FocusState == MediaCaptureFocusState.Failed)
                    return false;
                await Task.Delay(80, cancellationToken);
            }

            return focus.FocusState == MediaCaptureFocusState.Focused;
        }

        private async Task PrepareLowLagCaptureAsync(CancellationToken cancellationToken = default)
        {
            if (_lowLagPhotoCapture != null || _cameraMediaCapture == null)
                return;

            try
            {
                var encoding = CreatePhotoEncodingProperties();
                _lowLagPhotoCapture = await _cameraMediaCapture
                    .PrepareLowLagPhotoCaptureAsync(encoding)
                    .AsTask()
                    .WaitAsync(cancellationToken);
            }
            catch
            {
                _lowLagPhotoCapture = null;
            }
        }

        private async Task FinishLowLagCaptureAsync()
        {
            if (_lowLagPhotoCapture == null)
                return;

            try
            {
                await _lowLagPhotoCapture.FinishAsync();
            }
            catch
            {
                // Ignora errori in chiusura low-lag capture.
            }
            finally
            {
                _lowLagPhotoCapture = null;
            }
        }

        private async Task<InMemoryRandomAccessStream> CapturePhotoStreamAsync(CancellationToken cancellationToken)
        {
            if (_cameraMediaCapture == null)
                throw new InvalidOperationException("Fotocamera non inizializzata.");

            if (_lowLagPhotoCapture != null)
            {
                try
                {
                    var captured = await _lowLagPhotoCapture.CaptureAsync().AsTask().WaitAsync(cancellationToken);
                    var output = new InMemoryRandomAccessStream();
                    await RandomAccessStream.CopyAsync(captured.Frame, output);
                    captured.Frame.Dispose();
                    output.Seek(0);
                    return output;
                }
                catch
                {
                    await FinishLowLagCaptureAsync();
                }
            }

            var encoding = CreatePhotoEncodingProperties();
            var photoStream = new InMemoryRandomAccessStream();
            await _cameraMediaCapture.CapturePhotoToStreamAsync(encoding, photoStream);
            photoStream.Seek(0);
            return photoStream;
        }

        private async Task<RoiCaptureResult?> CaptureRoiWithRetryAsync()
        {
            if (_cameraMediaCapture == null)
                throw new InvalidOperationException("Fotocamera non inizializzata.");

            var cancellationToken = _cameraSessionCts?.Token ?? CancellationToken.None;
            var (previewWidth, previewHeight) = GetCameraPreviewDimensions();
            const int maxAttempts = 2;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await EnsureFocusBeforeCaptureAsync(cancellationToken);

                using var photoStream = await CapturePhotoStreamAsync(cancellationToken);
                var roi = await CameraRoiCapturePipeline.ProcessCaptureStreamAsync(
                    photoStream, previewWidth, previewHeight);
                if (roi == null)
                    continue;

                if (ImageSharpnessHelper.IsSharpEnough(roi.SharpnessScore) || attempt == maxAttempts - 1)
                    return roi;
            }

            return null;
        }

        private async Task<ComponentScanResult?> ScanRoiCaptureAsync(RoiCaptureResult roi, bool showToast = true)
        {
            try
            {
                var croppedScan = await _visionService!.ScanComponentFromBytesAsync(
                    roi.RoiPngBytes, "roi-capture.png");
                if (IsRecognizedScan(croppedScan))
                {
                    var croppedCode = GetBestScanCode(croppedScan);
                    if (showToast && !string.IsNullOrEmpty(croppedCode))
                        ShowToast($"Codice estratto: {croppedCode}", InfoBarSeverity.Success);
                    return croppedScan;
                }

                var fullScan = await _visionService.ScanComponentFromBytesAsync(
                    roi.FullFrameJpegBytes, "full-capture.jpg", "image/jpeg");
                if (showToast && IsRecognizedScan(fullScan))
                {
                    var code = GetBestScanCode(fullScan);
                    if (!string.IsNullOrEmpty(code))
                        ShowToast($"Codice estratto: {code}", InfoBarSeverity.Success);
                }

                return fullScan;
            }
            catch (Exception ex)
            {
                if (showToast)
                    ShowToast($"Errore scansione foto: {ex.Message}", InfoBarSeverity.Error);
                return null;
            }
        }

        private async Task<bool> TryInitializeCameraOverlayAsync(CancellationToken cancellationToken)
        {
            await _cameraResourceLock.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetVideoCaptureSelector())
                    .AsTask()
                    .WaitAsync(cancellationToken);
                var device = devices.FirstOrDefault(d => d.IsEnabled);
                if (device == null)
                    return false;

                foreach (var configureStreams in new[] { true, false })
                {
                    if (await TryStartCameraSessionAsync(device.Id, configureStreams, cancellationToken))
                        return true;
                }

                return false;
            }
            catch (UnauthorizedAccessException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
            finally
            {
                _cameraResourceLock.Release();
            }
        }

        private async Task<bool> TryStartCameraSessionAsync(
            string deviceId,
            bool configureStreams,
            CancellationToken cancellationToken)
        {
            await FinishLowLagCaptureAsync();

            var initialized = await TryInitializeMediaCaptureWithMemoryFallbackAsync(
                deviceId, MediaCaptureSharingMode.ExclusiveControl, cancellationToken);
            if (!initialized)
            {
                initialized = await TryInitializeMediaCaptureWithMemoryFallbackAsync(
                    deviceId, MediaCaptureSharingMode.SharedReadOnly, cancellationToken);
            }

            if (!initialized)
            {
                _cameraMediaCapture?.Dispose();
                _cameraMediaCapture = null;
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (configureStreams)
            {
                try
                {
                    await ConfigureCaptureStreamsAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Prova comunque ad avviare l'anteprima.
                }
            }

            var frameSource = GetCameraFrameSource();
            if (frameSource == null)
            {
                _cameraMediaCapture?.Dispose();
                _cameraMediaCapture = null;
                return false;
            }

            DetachCameraPreviewPlayer();

            _cameraMediaPlayer = new MediaPlayer
            {
                RealTimePlayback = true,
                AutoPlay = false
            };
            _cameraMediaPlayer.Source = MediaSource.CreateFromMediaFrameSource(frameSource);
            CameraPreviewElement.SetMediaPlayer(_cameraMediaPlayer);
            _cameraMediaPlayer.Play();

            try
            {
                await ConfigureCameraFocusAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Il focus non è critico per l'avvio della fotocamera.
            }

            try
            {
                await PrepareLowLagCaptureAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Low-lag capture è opzionale; si usa il fallback standard.
            }

            cancellationToken.ThrowIfCancellationRequested();

            CameraLoadingRing.Visibility = Visibility.Collapsed;
            CameraLoadingRing.IsActive = false;
            CameraCaptureButton.Visibility = Visibility.Visible;
            CameraCaptureButton.IsEnabled = true;
            ShowCameraScanGuide();
            return true;
        }

        private async Task<bool> TryInitializeMediaCaptureAsync(
            string deviceId,
            MediaCaptureSharingMode sharingMode,
            MediaCaptureMemoryPreference memoryPreference,
            CancellationToken cancellationToken)
        {
            if (_cameraMediaCapture == null)
                return false;

            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = deviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                SharingMode = sharingMode,
                MemoryPreference = memoryPreference
            };

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var initTask = _cameraMediaCapture.InitializeAsync(settings).AsTask();
                var completed = await Task.WhenAny(initTask, Task.Delay(8000, cancellationToken));
                if (completed != initTask)
                    return false;

                await initTask.WaitAsync(cancellationToken);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> TryInitializeMediaCaptureWithMemoryFallbackAsync(
            string deviceId,
            MediaCaptureSharingMode sharingMode,
            CancellationToken cancellationToken)
        {
            foreach (var memoryPreference in new[]
                     {
                         MediaCaptureMemoryPreference.Cpu,
                         MediaCaptureMemoryPreference.Auto
                     })
            {
                _cameraMediaCapture?.Dispose();
                _cameraMediaCapture = new MediaCapture();

                if (await TryInitializeMediaCaptureAsync(
                        deviceId, sharingMode, memoryPreference, cancellationToken))
                {
                    return true;
                }
            }

            _cameraMediaCapture?.Dispose();
            _cameraMediaCapture = null;
            return false;
        }

        private async void CameraCapture_Click(object sender, RoutedEventArgs e)
        {
            if (_cameraMediaCapture == null)
                return;

            CameraCaptureButton.IsEnabled = false;
            CameraCancelButton.IsEnabled = false;
            HideCameraRecognitionBanner();
            HideCameraScanGuide();

            try
            {
                var roi = await CaptureRoiWithRetryAsync();
                if (_cameraSessionCts?.Token.IsCancellationRequested == true)
                    return;

                ReturnToLiveCamera();
                await ProcessCapturedRoiAsync(roi);
            }
            catch (OperationCanceledException)
            {
                // L'utente ha premuto Indietro durante lo scatto.
            }
            catch (Exception ex)
            {
                CameraPreviewHost.Visibility = Visibility.Collapsed;
                CameraStatusText.Text = $"{UiStrings.CameraOpenError} {ex.Message}";
                CameraStatusText.Visibility = Visibility.Visible;
                RestartCameraPreview();
                CameraPreviewHost.Visibility = Visibility.Visible;
                CameraCaptureButton.IsEnabled = true;
                CameraCancelButton.IsEnabled = true;
            }
        }

        private async Task ProcessCapturedRoiAsync(RoiCaptureResult? roi)
        {
            CameraProcessingOverlay.Visibility = Visibility.Visible;

            try
            {
                if (roi == null)
                {
                    ShowCameraRecognitionBanner(UiStrings.CameraScanError);
                    return;
                }

                if (_cameraInventoryMode)
                {
                    var scan = await ScanRoiCaptureAsync(roi, showToast: false);
                    if (_cameraSessionCts?.Token.IsCancellationRequested == true)
                        return;

                    if (IsRecognizedScan(scan))
                        await ProcessInventoryScanAsync(scan!);
                    else
                        ShowCameraRecognitionBanner(GetCameraScanFailureMessage(scan));
                }
                else
                {
                    var scan = await ScanRoiCaptureAsync(roi, showToast: false);
                    if (_cameraSessionCts?.Token.IsCancellationRequested == true)
                        return;

                    if (scan != null && await TryMatchScanResultAsync(scan, showToast: false))
                    {
                        RefreshCameraComponentList();
                        _cameraSessionRefreshCallback?.Invoke();
                    }
                    else if (scan == null || !scan.Success)
                    {
                        ShowCameraRecognitionBanner(GetCameraScanFailureMessage(scan));
                    }
                    else if (string.IsNullOrEmpty(GetBestScanCode(scan)))
                    {
                        ShowCameraRecognitionBanner(UiStrings.CameraComponentNotRecognized);
                    }
                    else
                    {
                        var code = GetBestScanCode(scan)!;
                        ShowCameraRecognitionBanner(UiStrings.FormatCameraAccessoryNotFound(code));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // L'utente ha premuto Indietro durante l'elaborazione.
            }
            catch (Exception ex)
            {
                ShowToast($"Errore scansione foto: {ex.Message}", InfoBarSeverity.Error);
            }
            finally
            {
                CameraProcessingOverlay.Visibility = Visibility.Collapsed;
                if (_cameraSessionCts?.Token.IsCancellationRequested != true)
                {
                    CameraCaptureButton.IsEnabled = true;
                    CameraCancelButton.IsEnabled = true;
                    ShowCameraScanGuide();
                }
            }
        }

        private async void CameraManualInsert_Click(object sender, RoutedEventArgs e)
        {
            var code = await ShowManualCodeDialogAsync();
            if (string.IsNullOrWhiteSpace(code))
                return;

            HideCameraRecognitionBanner();
            CameraCaptureButton.IsEnabled = false;
            CameraCancelButton.IsEnabled = false;
            CameraManualButton.IsEnabled = false;

            try
            {
                if (_cameraInventoryMode)
                {
                    var scan = new ComponentScanResult
                    {
                        Success = true,
                        FileName = "manual-entry",
                        ComponentCode = code,
                        ComponentType = "unknown",
                        AllCodes = new List<string> { code },
                        RawTextFromImage = code
                    };
                    await ProcessInventoryScanAsync(scan);
                }
                else
                {
                    if (!await TryMatchComponentByCodeAsync(code, showToast: false))
                        ShowCameraRecognitionBanner(UiStrings.FormatCameraAccessoryNotFound(code));
                    RefreshCameraComponentList();
                    _cameraSessionRefreshCallback?.Invoke();
                }
            }
            finally
            {
                CameraManualButton.IsEnabled = true;
                if (_cameraSessionCts?.Token.IsCancellationRequested != true)
                    ReturnToLiveCamera();
            }
        }

        private async Task<string?> ShowManualCodeDialogAsync()
        {
            var input = new TextBox
            {
                PlaceholderText = "Codice componente",
                MinWidth = 420,
                FontSize = 22,
                MinHeight = 58,
                Text = ""
            };
            var dialog = new ContentDialog
            {
                Title = "Inserisci manualmente",
                Content = input,
                PrimaryButtonText = UiStrings.ConfirmRegisterButton,
                PrimaryButtonStyle = (Style)Application.Current.Resources["BrembanaInventoryScanPrimaryDialogButtonStyle"],
                CloseButtonText = UiStrings.CancelButton,
                DefaultButton = ContentDialogButton.None,
                Style = (Style)Application.Current.Resources["BrembanaInventoryScanContentDialogStyle"],
                XamlRoot = Content.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return null;
            return input.Text.Trim();
        }

        private static string GetCameraScanFailureMessage(ComponentScanResult? scan)
        {
            if (scan == null)
                return UiStrings.CameraScanError;

            if (!scan.Success)
                return scan.ErrorMessage ?? UiStrings.CameraScanError;

            return UiStrings.CameraComponentNotRecognized;
        }

        private void CameraCancel_Click(object sender, RoutedEventArgs e)
            => EndCameraScanSession();

        public void CancelCameraSession() => EndCameraScanSession();

        public void ClearWarehouseSession()
        {
            _currentTracker = null;
            _sessionAttivita = null;
        }

        private void EndCameraScanSession()
        {
            _cameraSessionCts?.Cancel();
            _cameraSessionTcs?.TrySetResult();
        }

        private async Task HideCameraOverlayAsync()
        {
            CameraCaptureOverlay.Visibility = Visibility.Collapsed;
            _cameraSessionTcs = null;

            await _cameraResourceLock.WaitAsync();
            try
            {
                await CleanupCameraResourcesAsync();
            }
            finally
            {
                _cameraResourceLock.Release();
                _cameraSessionCts?.Dispose();
                _cameraSessionCts = null;
            }
        }

        private async Task CleanupCameraResourcesAsync()
        {
            try
            {
                await FinishLowLagCaptureAsync();
                DetachCameraPreviewPlayer();

                if (_cameraMediaCapture != null)
                {
                    _cameraMediaCapture.Dispose();
                    _cameraMediaCapture = null;
                }

                _activePhotoEncoding = null;
            }
            catch
            {
                // Ignora errori in chiusura risorse camera.
            }
        }

        private async void ShowToast(string message, InfoBarSeverity severity)
        {
            ToastBar.Title = message;
            ToastBar.Severity = severity;
            ToastBar.IsOpen = true;
            await Task.Delay(3000);
            ToastBar.IsOpen = false;
        }
    }

    internal class JobOrderTracker
    {
        public string JobOrderName { get; set; } = "";
        public string JobOrderStatus { get; set; } = "Pending";
        public List<TrackedActivity> MatchedActivities { get; set; } = new();
        public List<TrackedComponent> TrackedComponents { get; set; } = new();
        public List<string> DateUpdates { get; set; } = new();
    }

    internal class TrackedActivity
    {
        public int OrmId { get; set; }
        public string ActivityName { get; set; } = "";
        public string Responsabile { get; set; } = "";
        public string Stabilimento { get; set; } = "";
        public string ProjectStatus { get; set; } = "";
        public List<string> AssignedResources { get; set; } = new();
        public bool DateWasUpdated { get; set; }
        public bool StatusWasUpdated { get; set; }
        public bool ActualStartSet { get; set; }
        public bool ActualFinishSet { get; set; }
    }

    internal class TrackedComponent
    {
        public string ResourceName { get; set; } = "";
        public string Categoria { get; set; } = "";
        public string Modello { get; set; } = "";
        public string TipoKey { get; set; } = "";
        public string SpecLabel { get; set; } = "";
        public string SpecValue { get; set; } = "";
        public string SpecUnit { get; set; } = "";
        public string SpecDetail { get; set; } = "";
        public bool IsOut { get; set; }
        public bool IsSelectedForRegister { get; set; }
        public DateTime? OutgoingScanTime { get; set; }
        public DateTime? IncomingScanTime { get; set; }
    }

    public class AppSettings
    {
        [Key] public int Id { get; set; }
        public string? LocalizationFilePath { get; set; }
        public string? ProjectResourceDataset { get; set; }
        public string? WPSDataDirectory { get; set; }
        public string? LastSelectedPlant { get; set; }
        public string? DefaultResponsible { get; set; }
        public string? BackendUrl { get; set; }
        public string? StabilimentoList { get; set; }
        public string? ResponsabileList { get; set; }
        public string? NetworkDatabasePath { get; set; }
        public string? SettingsPasswordHash { get; set; }
    }

    internal class JobTrackingData
    {
        [JsonProperty("jobOrder")] public string JobOrder { get; set; } = "";
        [JsonProperty("updated")] public DateTime Updated { get; set; }
        [JsonProperty("out")] public Dictionary<string, DateTime> Out { get; set; } = new();
        [JsonProperty("in")] public Dictionary<string, DateTime> In { get; set; } = new();
    }

    public class AppDbContext : DbContext
    {
        public DbSet<AppSettings> Settings { get; set; } = null!;

        public void EnsureMigrated() => Database.EnsureCreated();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dbPath = Path.Combine(appData, "Brembana", "brembana.db");
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            optionsBuilder.UseSqlite($"Data Source={dbPath}");
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<AppSettings>(e => e.HasKey(x => x.Id));
        }
    }

    public class SettingsService
    {
        private readonly AppDbContext _db;
        public SettingsService() { _db = new AppDbContext(); _db.EnsureMigrated(); }

        public async Task<AppSettings> GetSettingsAsync() => await LoadSettingsAsync();

        public async Task<AppSettings> LoadSettingsAsync()
        {
            try
            {
                var s = await _db.Settings.FirstOrDefaultAsync();
                if (s == null) { s = DefaultSettings(); _db.Settings.Add(s); await _db.SaveChangesAsync(); }
                return s;
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("no such column"))
            {
                await _db.Database.EnsureDeletedAsync();
                await _db.Database.EnsureCreatedAsync();
                var s = DefaultSettings(); _db.Settings.Add(s); await _db.SaveChangesAsync(); return s;
            }
        }

        public async Task<bool> SaveSettingsAsync(AppSettings settings)
        {
            try
            {
                var existing = await _db.Settings.FirstOrDefaultAsync();
                if (existing != null)
                {
                    existing.LocalizationFilePath = settings.LocalizationFilePath;
                    existing.ProjectResourceDataset = settings.ProjectResourceDataset;
                    existing.WPSDataDirectory = settings.WPSDataDirectory;
                    existing.LastSelectedPlant = settings.LastSelectedPlant;
                    existing.DefaultResponsible = settings.DefaultResponsible;
                    existing.BackendUrl = settings.BackendUrl;
                    existing.StabilimentoList = settings.StabilimentoList;
                    existing.ResponsabileList = settings.ResponsabileList;
                    existing.NetworkDatabasePath = settings.NetworkDatabasePath;
                    existing.SettingsPasswordHash = settings.SettingsPasswordHash;
                    _db.Settings.Update(existing);
                }
                else { _db.Settings.Add(settings); }
                await _db.SaveChangesAsync();
                return true;
            }
            catch { return false; }
        }

        private static AppSettings DefaultSettings() => new()
        {
            LocalizationFilePath = string.Empty,
            ProjectResourceDataset = string.Empty, WPSDataDirectory = string.Empty,
            LastSelectedPlant = "RICENGO", DefaultResponsible = string.Empty,
            BackendUrl = "http://127.0.0.1:8000",
            StabilimentoList = "RICENGO,VALBREMBO,SCHIO",
            ResponsabileList = "Emanuele Galbiati,Mirko Carminati,Francesco Pucci",
            NetworkDatabasePath = string.Empty,
            SettingsPasswordHash = HashPassword("SID#2050")
        };

        public static string HashPassword(string password)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
            return Convert.ToHexStringLower(bytes);
        }
    }
}
