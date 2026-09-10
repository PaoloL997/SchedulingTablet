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
        private IScanFeedback? _cameraFeedback;
        private bool _cameraChecklistCompleted;
        private bool _cameraCompleteRequested;
        private string? _activeTipoKey;
        private TrackedComponent? _lastConfirmed;
        private readonly List<PendingSubstitution> _pendingSubstitutions = new();
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
        internal ChecklistProgress Progress =>
            ActivityChecklistPolicy.Progress(_currentTracker?.TrackedComponents ?? new List<TrackedComponent>());
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

            // Ogni navigazione passa di qui: lasciare l'inventario rimette le barre globali.
            ContentFrame.Navigated += (_, e) =>
            {
                var chrome = PageChromePolicy.ShowsGlobalChrome(e.SourcePageType)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                AppHeaderBar.Visibility = chrome;
                AppFooterBar.Visibility = chrome;
            };
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

        /// <summary>Utente collegato, anche per le pagine che mostrano un avatar proprio.</summary>
        internal string? LoggedInUserName { get; private set; }

        internal event Action? LoggedInUserChanged;

        public void SetLoggedInUser(string? username)
        {
            LoggedInUserName = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
            LoggedInUserChanged?.Invoke();

            if (LoggedInUserName == null)
            {
                LoggedInUserPanel.Visibility = Visibility.Collapsed;
                return;
            }

            LoggedInUserText.Text = LoggedInUserName;
            UserAvatarText.Text = UiStrings.FormatUserInitial(LoggedInUserName);
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
            _pendingSubstitutions.Clear();
            _lastConfirmed = null;
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

        internal void ConfirmAllComponents()
        {
            if (_currentTracker != null)
                ActivityChecklistPolicy.ConfirmAll(_currentTracker.TrackedComponents);
        }

        internal void ClearAllComponents()
        {
            if (_currentTracker != null)
                ActivityChecklistPolicy.ClearAll(_currentTracker.TrackedComponents);
        }

        /// <summary>
        /// Un codice, da dovunque arrivi (casella in pagina, dialog manuale, foto): se è un
        /// pezzo previsto lo conferma; se in uscita è un pezzo compatibile passa dalla
        /// sostituzione; altrimenti spiega perché no attraverso <paramref name="feedback"/>.
        /// </summary>
        internal async Task<ScanResolution> ResolveScannedCodeAsync(
            string? code,
            ComponentScanResult? scan,
            IScanFeedback feedback)
        {
            feedback.Clear();
            if (_currentTracker == null)
                return ScanResolution.Handled;

            var codes = ActivityChecklistPolicy.CandidateCodes(code, scan);
            if (codes.Count == 0)
            {
                feedback.Problem(UiStrings.CameraComponentNotRecognized);
                return ScanResolution.NotFound;
            }

            foreach (var candidate in codes)
            {
                var planned = ActivityChecklistPolicy.FindPlanned(_currentTracker, candidate);
                if (planned == null)
                    continue;

                planned.IsSelectedForRegister = true;
                _lastConfirmed = planned;
                feedback.Success(UiStrings.FormatCodeConfirmed(planned.ResourceName));
                return ScanResolution.Confirmed;
            }

            var first = codes[0];
            if (!ActivityChecklistPolicy.AllowsSubstitution(_isOutgoing))
            {
                feedback.Problem(UiStrings.FormatNotPartOfActivity(first));
                return ScanResolution.NotFound;
            }

            var resolution = await TrySubstituteAsync(first, scan, feedback);
            if (resolution == ScanResolution.NotFound)
                feedback.Problem(UiStrings.FormatCameraAccessoryNotFound(first));
            return resolution;
        }

        public async Task RunCameraScanSessionAsync(Action? onComponentsChanged = null)
        {
            if (_visionService == null)
            {
                ShowToast("Servizio visione non configurato. Controlla GOOGLE_CLOUD_VISION_API_KEY nel file .env.", InfoBarSeverity.Error);
                return;
            }

            _cameraChecklistCompleted = false;
            var origin = ContentFrame.CurrentSourcePageType;
            _cameraSessionRefreshCallback = onComponentsChanged;
            _cameraSessionCts = new CancellationTokenSource();
            _cameraSessionTcs = new TaskCompletionSource();
            ResetCameraOverlayUi();

            CameraSessionTopBar.Visibility = Visibility.Visible;

            _lastConfirmed = null;
            _cameraCompleteRequested = false;
            _activeTipoKey = ActivityChecklistPolicy.NextIncompleteTipo(SessionGroups, _activeTipoKey);
            CameraFullListText.Text = UiStrings.FullListButton;
            RefreshCameraSessionUi();

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
                HideSessionChrome();

                if (_cameraCompleteRequested)
                {
                    // L'operatore ha confermato sulla fotocamera: si registra e si torna alla
                    // lista. Se il server rifiuta una sostituzione si resta sul riepilogo.
                    _cameraCompleteRequested = false;
                    if (!await CompleteSessionAsync() && HasActiveSession)
                        NavigateToAccessories();
                }
                else if (HasActiveSession)
                {
                    // Checklist completa: si passa al riepilogo per registrare. Altrimenti si
                    // torna da dove si è arrivati, e lasciare la lista attività chiude la sessione.
                    if (_cameraChecklistCompleted)
                    {
                        NavigateToAccessories();
                    }
                    else if (origin != typeof(AccessoriesPage))
                    {
                        ClearWarehouseSession();
                        NavigateToActivities();
                    }
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
            => CameraSessionTitleText.Text = CameraSectionTitleHelper.ForCameraSession(_isOutgoing);

        /// <summary>A fine sessione spariscono testata, riquadro del pezzo e pastiglie.</summary>
        private void HideSessionChrome()
        {
            CameraSessionTopBar.Visibility = Visibility.Collapsed;
            CameraTargetCard.Visibility = Visibility.Collapsed;
            CameraConfirmedStrip.Visibility = Visibility.Collapsed;
        }

        private void ShowCameraRecognitionBanner(string message)
        {
            CameraRecognitionBannerText.Text = message;
            CameraRecognitionBanner.Visibility = Visibility.Visible;
        }

        private void HideCameraRecognitionBanner()
            => CameraRecognitionBanner.Visibility = Visibility.Collapsed;

        // ── Azioni inventario ────────────────────────────────────────────────────
        // L'hub le avvia sulla riga selezionata; Aggiungi parte dal form vuoto.
        // Stessa policy e stessi dialog di quando il codice arrivava dalla fotocamera.

        private bool CanWriteInventory(bool requiresPlant, out string? userPlant)
        {
            userPlant = App.CurrentUserStabilimento?.Trim();
            if (!App.BridgeConnected)
            {
                ShowToast(UiStrings.InventoryBridgeRequired, InfoBarSeverity.Error);
                return false;
            }

            if (requiresPlant && string.IsNullOrWhiteSpace(userPlant))
            {
                ShowToast(UiStrings.NoUserPlant, InfoBarSeverity.Error);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Form di creazione con il tipo della tab attiva già scelto. Restituisce il form
        /// solo se il componente è stato davvero creato, così l'hub può selezionarlo.
        /// </summary>
        internal async Task<InventoryCreateForm?> RunInventoryCreateAsync(string? preselectedTable)
        {
            if (!CanWriteInventory(requiresPlant: true, out var userPlant))
                return null;

            var form = await InventoryScanDialogs.ShowCreateAsync(
                Content.XamlRoot, BridgeDataService.BuildCreateForm(preselectedTable, userPlant!));
            if (form == null)
                return null;

            if (string.IsNullOrWhiteSpace(form.Plant))
                form.Plant = userPlant;

            // Il dialog blocca già i duplicati mentre si scrive; qui si ricontrolla il dato
            // confermato. Il server rifiuta i duplicati solo dentro la stessa tabella: questo
            // li intercetta anche tra tipi diversi.
            var decision = InventoryActionPolicy.Decide(
                InventoryActionMode.Create, App.BridgeData.FindExistingUid(form.Uid, form.Table), userPlant);
            if (decision.Kind == InventoryActionKind.AlreadyPresent)
            {
                await InventoryScanDialogs.ShowFoundAsync(Content.XamlRoot, decision.Match!);
                return null;
            }

            try
            {
                await RunWithSavingOverlayAsync(() => App.BridgeData.CreateInventoryResourceAsync(form));
                DatasetRefreshed?.Invoke();
                ShowToast(UiStrings.InventoryCreated, InfoBarSeverity.Success);
                return form;
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

            return null;
        }

        /// <summary>Porta il componente nello stabilimento dell'utente. True se spostato.</summary>
        internal async Task<bool> RunInventoryMoveAsync(InventoryMatch match)
        {
            if (!CanWriteInventory(requiresPlant: true, out var userPlant))
                return false;

            // L'hub disattiva già Sposta sui pezzi di qui: ricontrollo contro un elenco vecchio.
            if (InventoryActionPolicy.Decide(InventoryActionMode.Move, match, userPlant).Kind != InventoryActionKind.Move)
            {
                await InventoryScanDialogs.ShowFoundAsync(Content.XamlRoot, match);
                return false;
            }

            if (!await InventoryScanDialogs.ShowMoveAsync(Content.XamlRoot, match, userPlant!))
                return false;

            try
            {
                await RunWithSavingOverlayAsync(() => App.BridgeData.MoveInventoryResourceAsync(match, userPlant!));
                DatasetRefreshed?.Invoke();
                ShowToast(UiStrings.InventoryMoved, InfoBarSeverity.Success);
                return true;
            }
            catch (Exception ex)
            {
                ShowToast($"Errore spostamento componente: {ex.Message}", InfoBarSeverity.Error);
                return false;
            }
        }

        /// <summary>Mette o toglie la manutenzione secondo lo stato attuale. True se cambiata.</summary>
        internal async Task<bool> RunInventoryMaintenanceAsync(InventoryMatch match)
        {
            if (!CanWriteInventory(requiresPlant: false, out var userPlant))
                return false;

            var decision = InventoryActionPolicy.Decide(InventoryActionMode.Maintenance, match, userPlant);
            if (decision.Kind == InventoryActionKind.UnsupportedType)
            {
                ShowToast(UiStrings.InventoryMaintenanceUnsupported, InfoBarSeverity.Warning);
                return false;
            }

            var enable = decision.Kind == InventoryActionKind.EnableMaintenance;
            if (!await InventoryScanDialogs.ShowMaintenanceAsync(Content.XamlRoot, match, enable))
                return false;

            try
            {
                await RunWithSavingOverlayAsync(() => App.BridgeData.SetMaintenanceAsync(match, enable));
                DatasetRefreshed?.Invoke();
                ShowToast(
                    enable ? UiStrings.InventoryMaintenanceEnabled : UiStrings.InventoryMaintenanceDisabled,
                    InfoBarSeverity.Success);
                return true;
            }
            catch (Exception ex)
            {
                ShowToast($"Errore manutenzione: {ex.Message}", InfoBarSeverity.Error);
                return false;
            }
        }

        /// <summary>
        /// Una mutazione inventario aspetta anche il ricaricamento del dataset (fino a due
        /// minuti se il server è occupato): l'overlay copre la pagina e blocca i tocchi.
        /// </summary>
        private async Task RunWithSavingOverlayAsync(Func<Task> action)
        {
            SavingText.Text = UiStrings.Saving;
            SavingOverlay.Visibility = Visibility.Visible;
            try
            {
                await action();
            }
            finally
            {
                SavingOverlay.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>Requisito di misura dell'attività di sessione per una categoria (vuoto se non indicato).</summary>
        internal string RequirementFor(string? tipoKey) =>
            ActivityChecklistPolicy.FormatRequirement(
                tipoKey,
                _sessionAttivita?.BoreMin,
                _sessionAttivita?.BoreMid,
                _sessionAttivita?.BoreMax,
                _sessionAttivita?.TubePitch,
                _sessionAttivita?.ModelloTesta);

        /// <summary>
        /// La lista della spesa: una sezione per categoria (teste, generatori, tastatori,
        /// mandrini, maschere) con il requisito di misura in evidenza e, in uscita, il tasto
        /// Sostituisci accanto agli accessori.
        /// </summary>
        /// <summary>
        /// Il riepilogo della sessione: una sezione per categoria (teste, generatori,
        /// tastatori, mandrini, maschere) con il requisito di misura accanto al titolo.
        /// Toccare una carta conferma o toglie la conferma.
        /// </summary>
        internal void FillComponentsPanel(StackPanel panel, Action refresh)
        {
            panel.Children.Clear();
            if (_currentTracker == null) return;

            var groups = ActivityChecklistPolicy.GroupByCategory(_currentTracker.TrackedComponents);
            if (groups.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = UiStrings.NoComponents,
                    Opacity = 0.6, FontSize = 14, Margin = new Thickness(0, 16, 0, 0),
                    Foreground = ThemeHelper.Brush("BrembanaTextMutedBrush")
                });
                return;
            }

            foreach (var group in groups)
            {
                panel.Children.Add(CreateCategoryHeader(group));

                var comps = group.Components;
                for (int i = 0; i < comps.Count; i += CardLayoutHelper.CardsPerRow)
                {
                    var rowCards = new List<FrameworkElement>();
                    for (int c = 0; c < CardLayoutHelper.CardsPerRow && i + c < comps.Count; c++)
                        rowCards.Add(CreateComponentCard(comps[i + c], refresh));

                    panel.Children.Add(CardLayoutHelper.CreateRow(rowCards, panel.ActualWidth));
                }
            }
        }

        /// <summary>Titolo di categoria con, accanto, il requisito di misura dell'attività.</summary>
        private FrameworkElement CreateCategoryHeader(ComponentGroup group)
        {
            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Margin = new Thickness(CardLayoutHelper.HoverBleed + CardHoverHelper.HostMarginH, 10, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            header.Children.Add(new TextBlock
            {
                Text = group.Label,
                FontSize = 22,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = ThemeHelper.Brush("BrembanaTextBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });

            var requirement = RequirementFor(group.TipoKey);
            if (!string.IsNullOrWhiteSpace(requirement))
            {
                header.Children.Add(new Border
                {
                    Background = ThemeHelper.WithAlpha("BrembanaAmberBrush", 36),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 3, 12, 3),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = requirement,
                        FontSize = 19,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        Foreground = ThemeHelper.Brush("BrembanaAmberBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    }
                });
            }

            return header;
        }

        /// <summary>
        /// Avvia (uscita) o chiude (rientro) l'attività: tutti i componenti cambiano stato
        /// insieme, mai una parte. La checklist deve essere completa.
        /// </summary>
        public async Task<bool> CompleteSessionAsync()
        {
            if (_currentTracker == null)
                return false;

            if (!App.BridgeConnected)
            {
                ShowToast(UiStrings.SaveRequiresBridge, InfoBarSeverity.Warning);
                return false;
            }

            if (!Progress.IsComplete)
            {
                ShowToast(UiStrings.ChecklistIncomplete, InfoBarSeverity.Warning);
                return false;
            }

            try
            {
                SavingOverlay.Visibility = Visibility.Visible;
                SavingText.Text = UiStrings.Saving;

                if (!await PushPendingSubstitutionsAsync())
                    return false;

                var comps = _currentTracker.TrackedComponents;
                if (_isOutgoing)
                    ActivityChecklistPolicy.ApplyStart(comps, DateTime.Now);
                else
                    ActivityChecklistPolicy.ApplyClose(comps, DateTime.Now);

                SaveTrackingToFile(_currentTracker);
                await UpdateActivityStatusesAsync();

                ShowToast(_isOutgoing ? UiStrings.ActivityStarted : UiStrings.ActivityClosed, InfoBarSeverity.Success);

                ClearWarehouseSession();
                NavigateToActivities();
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

        /// <summary>
        /// Scrive sulla pianificazione le sostituzioni decise durante la scansione. Si fa
        /// qui, all'avvio dell'attività, non al momento della scansione: finché l'operatore
        /// non conferma, sul server non cambia nulla. Se il server rifiuta, la registrazione
        /// si ferma e l'attività non parte.
        /// </summary>
        private async Task<bool> PushPendingSubstitutionsAsync()
        {
            if (_pendingSubstitutions.Count == 0 || _sessionAttivita == null)
                return true;

            var ormId = _sessionAttivita.OrmId;
            var uncovered = new List<string>();

            try
            {
                foreach (var pending in _pendingSubstitutions)
                {
                    var applied = await App.BridgeData.ScanActivityResourceAsync(
                        ormId, pending.TipoKey, pending.ScannedUid, pending.ExpectedUid, conferma: true);
                    uncovered.AddRange(ScanSubstitutionPolicy.UncoveredActivities(applied));
                }
            }
            catch (BridgeApiException ex)
            {
                // Fuori tolleranza, preso da qualcun altro nel frattempo…: si spiega e si ferma.
                ShowToast(ex.Message, InfoBarSeverity.Error);
                return false;
            }
            catch (Exception ex)
            {
                ShowToast($"Errore registrazione sostituzioni: {ex.Message}", InfoBarSeverity.Error);
                return false;
            }

            _pendingSubstitutions.Clear();
            _sessionAttivita = await App.BridgeData.ResyncActivityAsync(_sessionAttivita.ActivityId)
                               ?? _sessionAttivita;

            if (uncovered.Count > 0)
                ShowToast(UiStrings.FormatScanUncovered(uncovered), InfoBarSeverity.Warning);

            return true;
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

        /// <summary>
        /// Carta di un componente: codice, misura e stato della checklist. La categoria non
        /// si ripete: la dice il titolo di sezione. Toccarla conferma o toglie la conferma.
        /// </summary>
        private Grid CreateComponentCard(TrackedComponent comp, Action refresh)
        {
            bool isSelected = comp.IsSelectedForRegister;
            var accentColor = isSelected
                ? ThemeHelper.GetColor("BrembanaGreenBrush")
                : ThemeHelper.GetColor("BrembanaTextMutedBrush");

            var code = string.IsNullOrWhiteSpace(comp.ResourceName) ? "—" : comp.ResourceName.Trim();
            var measure = UiStrings.FormatComponentMeasure(comp.SpecLabel, comp.SpecValue, comp.SpecUnit, comp.SpecDetail);

            var cardContent = new Grid { ColumnSpacing = 12, VerticalAlignment = VerticalAlignment.Center };
            cardContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cardContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var infoPanel = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            infoPanel.Children.Add(new TextBlock
            {
                Text = code,
                FontSize = 24,
                LineHeight = 28,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = ThemeHelper.Brush("BrembanaTextBrush")
            });
            if (!string.IsNullOrWhiteSpace(measure))
            {
                infoPanel.Children.Add(new TextBlock
                {
                    Text = measure,
                    FontSize = 19,
                    LineHeight = 24,
                    Foreground = ThemeHelper.Brush("BrembanaTextMutedBrush"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }
            Grid.SetColumn(infoPanel, 0);
            cardContent.Children.Add(infoPanel);

            var badge = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (isSelected)
            {
                badge.Children.Add(new FontIcon
                {
                    Glyph = "\uE73E",
                    FontSize = 20,
                    Foreground = new SolidColorBrush(accentColor),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            badge.Children.Add(new TextBlock
            {
                Text = isSelected ? UiStrings.CardStatusPresent : UiStrings.CardStatusToConfirm,
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(accentColor),
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(badge, 1);
            cardContent.Children.Add(badge);

            var borderAlpha = (byte)(isSelected ? 200 : 90);
            var surface = ThemeHelper.GetColor("BrembanaSurfaceBrush");
            var button = new Button
            {
                Content = cardContent,
                Tag = comp,
                Style = (Style)Application.Current.Resources["BrembanaCardButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(16, 13, 14, 13),
                CornerRadius = new CornerRadius(12),
                BorderBrush = new SolidColorBrush(Color.FromArgb(borderAlpha, accentColor.R, accentColor.G, accentColor.B)),
                BorderThickness = new Thickness(isSelected ? 2 : 1.5),
                Background = isSelected
                    ? new SolidColorBrush(Color.FromArgb(255,
                        (byte)Math.Min(255, surface.R + (accentColor.R - surface.R) * 0.12),
                        (byte)Math.Min(255, surface.G + (accentColor.G - surface.G) * 0.12),
                        (byte)Math.Min(255, surface.B + (accentColor.B - surface.B) * 0.12)))
                    : ThemeHelper.Brush("BrembanaSurfaceBrush"),
                MinHeight = 84
            };
            button.Click += (_, _) =>
            {
                if (_currentTracker == null) return;
                comp.IsSelectedForRegister = !comp.IsSelectedForRegister;
                refresh();
            };
            ToolTipService.SetToolTip(button, isSelected
                ? UiStrings.CardTooltipUnconfirm
                : UiStrings.CardTooltipConfirm);
            return CardHoverHelper.WrapCard(button, accentColor, pinned: isSelected);
        }

        /// <summary>
        /// Dopo ogni scansione: la categoria attiva passa alla prima incompleta, la schermata
        /// si ridisegna e, quando non manca più nulla, si chiede se registrare l'attività.
        /// </summary>
        private async Task HandleSessionScanResultAsync(ScanResolution resolution)
        {
            if (resolution is ScanResolution.Confirmed or ScanResolution.Substituted)
                _activeTipoKey = ActivityChecklistPolicy.NextIncompleteTipo(SessionGroups, _activeTipoKey);

            RefreshCameraSessionUi();
            _cameraSessionRefreshCallback?.Invoke();

            if (resolution is not (ScanResolution.Confirmed or ScanResolution.Substituted))
                return;

            if (!Progress.IsComplete)
                return;

            _cameraChecklistCompleted = true;
            HideCameraRecognitionBanner();
            CameraProcessingOverlay.Visibility = Visibility.Collapsed;

            // La conferma si apre qui sopra la fotocamera: se l'operatore accetta si registra
            // subito, altrimenti resta nella scansione e può ancora correggere.
            if (await ConfirmCompleteFromCameraAsync())
            {
                _cameraCompleteRequested = true;
                EndCameraScanSession();
            }
        }

        private async Task<bool> ConfirmCompleteFromCameraAsync()
        {
            var dialog = new ContentDialog
            {
                Title = _isOutgoing ? UiStrings.ConfirmRegisterTitleOutgoing : UiStrings.ConfirmRegisterTitleIncoming,
                Content = new TextBlock
                {
                    Text = UiStrings.FormatRegisterConfirmation(_isOutgoing, Progress.Total),
                    FontSize = 24,
                    Foreground = ThemeHelper.Brush("BrembanaTextBrush"),
                    TextWrapping = TextWrapping.WrapWholeWords
                },
                PrimaryButtonText = UiStrings.ConfirmRegisterButton,
                PrimaryButtonStyle = (Style)Application.Current.Resources["BrembanaInventoryScanPrimaryDialogButtonStyle"],
                CloseButtonText = UiStrings.CancelButton,
                // Con DefaultButton=Primary WinUI colora il tasto con l'accento di sistema
                // di Windows, ignorando PrimaryButtonStyle: qui sarebbe apparso viola invece
                // che dell'azzurro dell'app. Nessun altro dialog dell'app usa DefaultButton.
                DefaultButton = ContentDialogButton.None,
                Style = (Style)Application.Current.Resources["BrembanaInventoryScanContentDialogStyle"],
                XamlRoot = Content.XamlRoot
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private IReadOnlyList<ComponentGroup> SessionGroups =>
            _currentTracker == null
                ? Array.Empty<ComponentGroup>()
                : ActivityChecklistPolicy.GroupByCategory(_currentTracker.TrackedComponents);

        /// <summary>
        /// Ridisegna tutta la schermata di scansione: contatore e barra di avanzamento,
        /// pastiglie delle categorie, riquadro del pezzo da cercare, pezzi già confermati
        /// ed elenco completo del menu a tendina.
        /// </summary>
        private void RefreshCameraSessionUi()
        {
            if (_currentTracker == null)
                return;

            var groups = SessionGroups;
            var progress = Progress;

            CameraSessionCounterText.Text = UiStrings.FormatScanCounter(progress);
            RenderProgressStrip(progress);
            RenderCategoryChips(groups);
            RenderTargetCard(groups);
            RenderConfirmedStrip(groups);
            RenderFullList();
            ApplyLeftButtonMode();
            PositionCameraScanGuide();
        }

        /// <summary>Un segmento per componente: ambra quelli confermati, grigi gli altri.</summary>
        private void RenderProgressStrip(ChecklistProgress progress)
        {
            CameraProgressStrip.Children.Clear();
            CameraProgressStrip.ColumnDefinitions.Clear();

            var amber = ThemeHelper.Brush("BrembanaAmberBrush");
            for (var i = 0; i < progress.Total; i++)
            {
                CameraProgressStrip.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var segment = new Border
                {
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    Background = i < progress.Confirmed
                        ? amber
                        : new SolidColorBrush(Color.FromArgb(64, 255, 255, 255))
                };
                Grid.SetColumn(segment, i);
                CameraProgressStrip.Children.Add(segment);
            }
        }

        /// <summary>Pastiglie delle categorie: toccarle cambia il pezzo da cercare.</summary>
        private void RenderCategoryChips(IReadOnlyList<ComponentGroup> groups)
        {
            CameraCategoryChips.Children.Clear();

            var amber = ThemeHelper.GetColor("BrembanaAmberBrush");
            var muted = Color.FromArgb(255, 190, 198, 210);

            foreach (var group in groups)
            {
                var progress = ActivityChecklistPolicy.ProgressOf(group);
                var isActive = string.Equals(group.TipoKey, _activeTipoKey, StringComparison.OrdinalIgnoreCase);
                var accent = progress.IsComplete ? amber : muted;

                var content = new StackPanel { Spacing = 0, HorizontalAlignment = HorizontalAlignment.Center };
                content.Children.Add(new TextBlock
                {
                    Text = group.Label.ToUpperInvariant(),
                    FontSize = 16,
                    LineHeight = 20,
                    CharacterSpacing = 70,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(accent),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                content.Children.Add(new TextBlock
                {
                    Text = UiStrings.FormatChipCount(progress),
                    FontSize = 19,
                    LineHeight = 23,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(accent),
                    HorizontalAlignment = HorizontalAlignment.Center
                });

                var chip = new Button
                {
                    Content = content,
                    Tag = group.TipoKey,
                    Height = 66,
                    MinWidth = 126,
                    Padding = new Thickness(18, 0, 18, 0),
                    CornerRadius = new CornerRadius(13),
                    Background = new SolidColorBrush(Color.FromArgb(
                        isActive ? (byte)70 : (byte)34, accent.R, accent.G, accent.B)),
                    BorderBrush = new SolidColorBrush(isActive
                        ? Color.FromArgb(255, 255, 255, 255)
                        : Color.FromArgb(progress.IsComplete ? (byte)190 : (byte)90, accent.R, accent.G, accent.B)),
                    BorderThickness = new Thickness(isActive ? 2 : 1)
                };
                chip.Click += (sender, _) =>
                {
                    if (sender is Button { Tag: string tipo })
                    {
                        _activeTipoKey = tipo;
                        RefreshCameraSessionUi();
                    }
                };
                CameraCategoryChips.Children.Add(chip);
            }
        }

        /// <summary>Il riquadro sopra il mirino: cosa sto cercando adesso.</summary>
        private void RenderTargetCard(IReadOnlyList<ComponentGroup> groups)
        {
            var group = groups.FirstOrDefault(g =>
                string.Equals(g.TipoKey, _activeTipoKey, StringComparison.OrdinalIgnoreCase)) ?? groups.FirstOrDefault();

            if (group == null)
            {
                CameraTargetCard.Visibility = Visibility.Collapsed;
                return;
            }

            var next = ActivityChecklistPolicy.NextToConfirm(group);
            var total = group.Components.Count;
            var position = ActivityChecklistPolicy.IndexOfNext(group);

            CameraTargetTypeText.Text = next != null && total > 1
                ? $"{group.Label} · {UiStrings.FormatTargetPosition(position, total)}"
                : group.Label;

            CameraTargetIdText.Text = next != null
                ? $"{UiStrings.TargetIdLabel} {next.ResourceName}"
                : UiStrings.TargetDone;
            CameraTargetIdText.Foreground = next != null
                ? ThemeHelper.Brush("BrembanaTextBrush")
                : ThemeHelper.Brush("BrembanaGreenBrush");

            var requirement = RequirementFor(group.TipoKey);
            CameraTargetSpecText.Text = requirement;
            CameraTargetSpecText.Visibility = string.IsNullOrWhiteSpace(requirement)
                ? Visibility.Collapsed
                : Visibility.Visible;

            CameraTargetCard.Visibility = Visibility.Visible;
        }

        /// <summary>Sotto il mirino, i pezzi già confermati della categoria attiva.</summary>
        private void RenderConfirmedStrip(IReadOnlyList<ComponentGroup> groups)
        {
            CameraConfirmedStrip.Children.Clear();

            var group = groups.FirstOrDefault(g =>
                string.Equals(g.TipoKey, _activeTipoKey, StringComparison.OrdinalIgnoreCase));
            var confirmed = group?.Components.Where(c => c.IsSelectedForRegister).ToList() ?? new List<TrackedComponent>();

            if (confirmed.Count == 0)
            {
                CameraConfirmedStrip.Visibility = Visibility.Collapsed;
                return;
            }

            var green = ThemeHelper.GetColor("BrembanaGreenBrush");
            foreach (var comp in confirmed.Take(3))
            {
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 9,
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.Children.Add(new FontIcon
                {
                    Glyph = "\uE73E",
                    FontSize = 17,
                    Foreground = new SolidColorBrush(green),
                    VerticalAlignment = VerticalAlignment.Center
                });
                row.Children.Add(new TextBlock
                {
                    Text = comp.ResourceName,
                    FontSize = 19,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(green),
                    VerticalAlignment = VerticalAlignment.Center
                });

                var detail = UiStrings.FormatComponentMeasure(
                    comp.SpecLabel, comp.SpecValue, comp.SpecUnit, comp.SpecDetail);
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    row.Children.Add(new TextBlock
                    {
                        Text = detail,
                        FontSize = 16,
                        Foreground = new SolidColorBrush(Color.FromArgb(200, 214, 221, 231)),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }

                CameraConfirmedStrip.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(230, 14, 17, 21)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(130, green.R, green.G, green.B)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(14, 7, 16, 7),
                    Child = row
                });
            }

            if (confirmed.Count > 3)
            {
                CameraConfirmedStrip.Children.Add(new TextBlock
                {
                    Text = $"+{confirmed.Count - 3}",
                    FontSize = 17,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(green),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            CameraConfirmedStrip.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// L'elenco completo del menu a tendina: una sezione per categoria con il requisito
        /// dell'attività e una riga per pezzo, con codice, modello o misura e la spunta.
        /// </summary>
        private void RenderFullList()
        {
            CameraComponentList.Children.Clear();
            if (_currentTracker == null) return;

            var white = new SolidColorBrush(Microsoft.UI.Colors.White);
            var muted = new SolidColorBrush(Color.FromArgb(180, 210, 218, 228));
            var green = ThemeHelper.GetColor("BrembanaGreenBrush");
            var amber = ThemeHelper.GetColor("BrembanaAmberBrush");
            var pending = Color.FromArgb(255, 206, 213, 223);

            CameraComponentList.Children.Add(new TextBlock
            {
                Text = UiStrings.FormatChecklistProgress(Progress),
                FontSize = 23,
                LineHeight = 28,
                Margin = new Thickness(1, 0, 0, 2),
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = white
            });

            foreach (var group in SessionGroups)
            {
                var header = new StackPanel { Spacing = 0, Margin = new Thickness(1, 6, 0, 1) };
                header.Children.Add(new TextBlock
                {
                    Text = group.Label.ToUpperInvariant(),
                    FontSize = 17,
                    LineHeight = 22,
                    CharacterSpacing = 90,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = muted
                });

                var requirement = RequirementFor(group.TipoKey);
                if (!string.IsNullOrWhiteSpace(requirement))
                {
                    header.Children.Add(new TextBlock
                    {
                        Text = requirement,
                        FontSize = 19,
                        LineHeight = 24,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(amber),
                        TextWrapping = TextWrapping.WrapWholeWords
                    });
                }
                CameraComponentList.Children.Add(header);

                foreach (var comp in group.Components)
                {
                    var done = comp.IsSelectedForRegister;
                    var accent = done ? green : pending;
                    var detail = UiStrings.FormatComponentMeasure(
                        comp.SpecLabel, comp.SpecValue, comp.SpecUnit, comp.SpecDetail);

                    var greenText = new SolidColorBrush(green);
                    var greenDetail = new SolidColorBrush(Color.FromArgb(205, green.R, green.G, green.B));

                    var row = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(23) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    if (done)
                    {
                        var mark = new FontIcon
                        {
                            Glyph = "\uE73E",
                            FontSize = 21,
                            Foreground = greenText,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        Grid.SetColumn(mark, 0);
                        row.Children.Add(mark);
                    }

                    var uid = new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(comp.ResourceName) ? "—" : comp.ResourceName.Trim(),
                        FontSize = 23,
                        LineHeight = 29,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        Foreground = done ? greenText : white,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(uid, 1);
                    row.Children.Add(uid);

                    if (!string.IsNullOrWhiteSpace(detail))
                    {
                        var text = new TextBlock
                        {
                            Text = detail,
                            FontSize = 19,
                            LineHeight = 29,
                            Foreground = done ? greenDetail : muted,
                            VerticalAlignment = VerticalAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            TextWrapping = TextWrapping.NoWrap
                        };
                        Grid.SetColumn(text, 2);
                        row.Children.Add(text);
                    }

                    CameraComponentList.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(done ? (byte)48 : (byte)22, accent.R, accent.G, accent.B)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(done ? (byte)160 : (byte)50, accent.R, accent.G, accent.B)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(9),
                        Padding = new Thickness(9, 4, 12, 4),
                        Child = row
                    });
                }
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
            ApplyLeftButtonMode();
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

            PlaceAroundGuide(CameraTargetCard, guide, width, above: true);
            PlaceAroundGuide(CameraConfirmedStrip, guide, width, above: false);
        }

        /// <summary>
        /// Incolla un elemento sopra o sotto il mirino, centrato. Il mirino sta al centro
        /// esatto dell'overlay: qui si misura l'elemento e lo si posiziona sul canvas.
        /// </summary>
        private static void PlaceAroundGuide(FrameworkElement element, Windows.Foundation.Rect guide, double width, bool above)
        {
            if (element.Visibility != Visibility.Visible)
                return;

            element.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
            var size = element.DesiredSize;

            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(element, Math.Max(12, (width - size.Width) / 2));
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(element, above
                ? Math.Max(104, guide.Y - size.Height - 26)
                : guide.Y + guide.Height + 26);
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

        private void ResetCameraOverlayUiScale()
        {
            CameraCancelButton.MinHeight = 60;
            CameraCancelButton.MinWidth = 150;
            CameraCancelButton.FontSize = 20;
            CameraCancelButton.Padding = new Thickness(24, 12, 24, 12);
            CameraCancelButton.CornerRadius = new CornerRadius(28);

            CameraCaptureButton.Width = 88;
            CameraCaptureButton.Height = 88;
            CameraCaptureButton.CornerRadius = new CornerRadius(44);
            CameraCaptureButton.BorderThickness = new Thickness(4);

            CameraStatusText.FontSize = 22;
            CameraStatusText.MaxWidth = 520;

            CameraProcessingText.FontSize = 20;
            CameraProcessingRing.Width = 48;
            CameraProcessingRing.Height = 48;

            CameraLoadingRing.Width = 48;
            CameraLoadingRing.Height = 48;

            CameraRecognitionBanner.Margin = new Thickness(24, 0, 24, 140);
            CameraRecognitionBannerText.FontSize = 21;
            CameraManualButton.FontSize = 20;
            CameraManualButton.MinWidth = 250;
            CameraManualButton.MinHeight = 60;
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
            ApplyLeftButtonMode();
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

                var scan = await ScanRoiCaptureAsync(roi, showToast: false);
                if (_cameraSessionCts?.Token.IsCancellationRequested == true)
                    return;

                if (scan == null || !scan.Success)
                {
                    ShowCameraRecognitionBanner(GetCameraScanFailureMessage(scan));
                }
                else
                {
                    await HandleSessionScanResultAsync(
                        await ResolveScannedCodeAsync(GetBestScanCode(scan), scan, CameraFeedback));
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
                await HandleSessionScanResultAsync(await ResolveScannedCodeAsync(code, null, CameraFeedback));
            }
            finally
            {
                CameraManualButton.IsEnabled = true;
                if (_cameraSessionCts?.Token.IsCancellationRequested != true)
                    ReturnToLiveCamera();
            }
        }

        /// <summary>
        /// Un codice non tra gli accessori dell'attività può comunque essere un pezzo
        /// compatibile (mandrini, tastatori, maschere): lo chiede al server, mostra la
        /// conferma «Stai usando X al posto di Y, va bene?» e riporta la sostituzione sul
        /// tracker. Teste e generatori restano a corrispondenza esatta. I messaggi vanno
        /// dove dice <paramref name="feedback"/>: banner della fotocamera o riga in pagina.
        /// </summary>
        private async Task<ScanResolution> TrySubstituteAsync(
            string code,
            ComponentScanResult? scan,
            IScanFeedback feedback)
        {
            if (_currentTracker == null || _sessionAttivita == null || string.IsNullOrWhiteSpace(code))
                return ScanResolution.NotFound;

            var match = App.BridgeData.FindInInventory(code, scan);
            var decision = ScanSubstitutionPolicy.Decide(match);
            if (decision.Kind == ScanSubstitutionKind.NotInInventory)
                return ScanResolution.NotFound;

            var item = decision.Match!;
            var tipoKey = item.TipoKey;
            var assigned = ScanSubstitutionPolicy.AssignedUidsOfTipo(_currentTracker, tipoKey);

            if (decision.Kind == ScanSubstitutionKind.ExactTypeRequired)
            {
                feedback.Problem(UiStrings.FormatScanExactTypeRequired(item.Table, item.Uid, assigned));
                return ScanResolution.Handled;
            }

            if (!App.BridgeConnected)
            {
                feedback.Problem(UiStrings.ScanBridgeRequired);
                return ScanResolution.Handled;
            }

            // I dialog si aprono sopra la fotocamera: via l'overlay di elaborazione.
            feedback.PrepareForDialog();

            // Con più pezzi previsti dello stesso tipo, quello che sto cercando adesso è il
            // prossimo della categoria attiva: niente domanda all'operatore.
            string? atteso = ExpectedUidForSubstitution(tipoKey, assigned);
            if (atteso == null && assigned.Count > 1)
            {
                atteso = await InventoryScanDialogs.PickExpectedUidAsync(Content.XamlRoot, item.Table, assigned);
                if (atteso == null)
                    return ScanResolution.Handled;
            }

            var ormId = _sessionAttivita.OrmId;
            try
            {
                var check = await App.BridgeData.ScanActivityResourceAsync(
                    ormId, tipoKey, item.Uid, atteso, conferma: false);

                if (string.Equals(check.Esito, ScanSubstitutionPolicy.EsitoCorrispondente, StringComparison.OrdinalIgnoreCase))
                {
                    // Il tracker è più vecchio della pianificazione: il pezzo è già assegnato.
                    _lastConfirmed = ScanSubstitutionPolicy.ApplySubstitution(_currentTracker, tipoKey, null, item);
                    feedback.Success(UiStrings.FormatCodeConfirmed(item.Uid));
                    return ScanResolution.Confirmed;
                }

                if (check.RichiedeConferma)
                {
                    var ok = await InventoryScanDialogs.ShowScanSubstitutionAsync(
                        Content.XamlRoot, check.Messaggio ?? "", check.Avvisi);
                    if (!ok)
                        return ScanResolution.Handled;
                }

                // La pianificazione si tocca solo quando l'attività parte davvero: qui la
                // sostituzione resta sul tracker e si mette in coda.
                var expected = check.Atteso ?? atteso;
                var target = _currentTracker.TrackedComponents.FirstOrDefault(c =>
                    string.Equals(c.TipoKey, tipoKey, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(c.ResourceName, expected, StringComparison.OrdinalIgnoreCase));
                var previousUid = target?.ResourceName ?? "";
                var previousModello = target?.Modello ?? "";
                var previousSpec = target?.SpecValue ?? "";

                var replaced = ScanSubstitutionPolicy.ApplySubstitution(_currentTracker, tipoKey, expected, item);
                _lastConfirmed = replaced;
                _pendingSubstitutions.Add(new PendingSubstitution(
                    tipoKey, item.Uid, expected, replaced, previousUid, previousModello, previousSpec));

                feedback.Clear();
                feedback.Success(UiStrings.FormatScanSubstituted(item.Table, item.Uid, expected));
                return ScanResolution.Substituted;
            }
            catch (BridgeApiException ex)
            {
                // Fuori tolleranza, in uso altrove, requisiti mancanti…: il server spiega perché.
                feedback.Problem(ex.Message);
                return ScanResolution.Handled;
            }
            catch (Exception ex)
            {
                feedback.Problem($"Errore verifica componente: {ex.Message}");
                return ScanResolution.Handled;
            }
        }

        /// <summary>
        /// Il pezzo previsto che una sostituzione va a rimpiazzare: il prossimo da confermare
        /// della categoria attiva, se è di quel tipo; altrimenti l'unico previsto, se è uno solo.
        /// </summary>
        private string? ExpectedUidForSubstitution(string tipoKey, IReadOnlyList<string> assigned)
        {
            if (string.Equals(tipoKey, _activeTipoKey, StringComparison.OrdinalIgnoreCase))
            {
                var group = SessionGroups.FirstOrDefault(g =>
                    string.Equals(g.TipoKey, tipoKey, StringComparison.OrdinalIgnoreCase));
                var next = group == null ? null : ActivityChecklistPolicy.NextToConfirm(group);
                if (next != null && !string.IsNullOrWhiteSpace(next.ResourceName))
                    return next.ResourceName;
            }

            return assigned.Count == 1 ? assigned[0] : null;
        }

        private IScanFeedback CameraFeedback => _cameraFeedback ??= new CameraScanFeedback(this);

        /// <summary>Feedback dentro l'overlay fotocamera: i toast starebbero sotto, quindi si usa il banner.</summary>
        private sealed class CameraScanFeedback : IScanFeedback
        {
            private readonly MainWindow _window;

            public CameraScanFeedback(MainWindow window) => _window = window;

            public void Problem(string message) => _window.ShowCameraRecognitionBanner(message);

            public void Clear() => _window.HideCameraRecognitionBanner();

            // La spunta verde nell'elenco dell'overlay è già il feedback positivo.
            public void Success(string message) { }

            public void Warning(string message) => _window.ShowCameraRecognitionBanner(message);

            public void PrepareForDialog() => _window.CameraProcessingOverlay.Visibility = Visibility.Collapsed;
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

        private void CameraSessionBack_Click(object sender, RoutedEventArgs e)
            => EndCameraScanSession();

        /// <summary>
        /// Il tasto in basso a sinistra annulla l'ultimo pezzo confermato (indietro sta in
        /// alto a sinistra).
        /// </summary>
        private void CameraLeftButton_Click(object sender, RoutedEventArgs e) => UndoLastConfirm();

        /// <summary>Toglie la spunta all'ultimo pezzo confermato. La sostituzione già scritta sul server resta.</summary>
        private void UndoLastConfirm()
        {
            if (_lastConfirmed == null)
                return;

            var undoneUid = _lastConfirmed.ResourceName;
            var pending = _pendingSubstitutions.LastOrDefault(s => ReferenceEquals(s.Target, _lastConfirmed));
            if (pending != null && _currentTracker != null)
            {
                // Non era ancora sul server: si rimette il pezzo previsto e si toglie dalla coda.
                _pendingSubstitutions.Remove(pending);
                ScanSubstitutionPolicy.RevertSubstitution(
                    _currentTracker, pending.Target, pending.PreviousUid, pending.PreviousModello, pending.PreviousSpecValue);
            }
            else
            {
                _lastConfirmed.IsSelectedForRegister = false;
            }

            ShowToast(UiStrings.FormatCodeUndone(undoneUid), InfoBarSeverity.Informational);
            _lastConfirmed = null;
            _activeTipoKey = ActivityChecklistPolicy.NextIncompleteTipo(SessionGroups, _activeTipoKey);
            RefreshCameraSessionUi();
            _cameraSessionRefreshCallback?.Invoke();
        }

        private void ApplyLeftButtonMode()
        {
            CameraCancelButton.Content = UiStrings.UndoLastButton;
            CameraCancelButton.IsEnabled = _lastConfirmed != null;
        }

        public void CancelCameraSession() => EndCameraScanSession();

        public void ClearWarehouseSession()
        {
            _currentTracker = null;
            _sessionAttivita = null;
            _pendingSubstitutions.Clear();
            _lastConfirmed = null;
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

        internal async void ShowToast(string message, InfoBarSeverity severity)
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

    /// <summary>
    /// Sostituzione decisa dall'operatore e non ancora scritta sulla pianificazione: parte
    /// solo quando l'attività viene avviata davvero.
    /// </summary>
    internal sealed record PendingSubstitution(
        string TipoKey,
        string ScannedUid,
        string? ExpectedUid,
        TrackedComponent Target,
        string PreviousUid,
        string PreviousModello,
        string PreviousSpecValue);

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
