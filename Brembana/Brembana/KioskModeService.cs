using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using WinRT.Interop;

namespace Brembana;

/// <summary>
/// Gestisce modalità kiosk WinUI 3: fullscreen, title bar, topmost, chiusura controllata.
/// Compatibile con Windows 11 Pro (nessuna API Enterprise / Shell Launcher).
/// </summary>
internal sealed class KioskModeService
{
    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpShowwindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotopmost = new(-2);

    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;
    private bool _allowClose = true;
    private bool _kioskActive;

    public bool IsKioskActive => _kioskActive;
    public bool AllowClose => _allowClose;

    public KioskModeService(Window window)
    {
        _window = window;
        _appWindow = window.AppWindow;
        _hwnd = WindowNative.GetWindowHandle(window);
        _appWindow.Closing += OnAppWindowClosing;
    }

    /// <summary>
    /// Attiva fullscreen reale, nasconde title bar, disabilita resize e rende TopMost.
    /// </summary>
    public void EnterKioskMode()
    {
        try
        {
            // Contenuto a bordo pieno, senza chrome Windows.
            _window.ExtendsContentIntoTitleBar = true;
            _window.SetTitleBar(null);

            if (_appWindow.Presenter is OverlappedPresenter overlapped)
            {
                overlapped.IsResizable = false;
                overlapped.IsMaximizable = false;
                overlapped.IsMinimizable = false;
                overlapped.SetBorderAndTitleBar(false, false);
            }

            _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            SetTopMost(true);
            _kioskActive = true;
            _allowClose = false;
            AppLogger.Info("Modalità kiosk attivata.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Impossibile attivare modalità kiosk.", ex);
        }
    }

    /// <summary>
    /// Uscita amministratore dalla modalità kiosk (finestra normale, chiusura ancora bloccata
    /// finché non si usa Chiudi applicazione dal menu).
    /// </summary>
    public void ExitKioskMode()
    {
        try
        {
            _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);

            if (_appWindow.Presenter is OverlappedPresenter overlapped)
            {
                overlapped.IsResizable = true;
                overlapped.IsMaximizable = true;
                overlapped.IsMinimizable = true;
                overlapped.SetBorderAndTitleBar(true, true);
                overlapped.Maximize();
            }

            _window.ExtendsContentIntoTitleBar = false;
            SetTopMost(false);
            _kioskActive = false;
            AppLogger.Info("Modalità kiosk disattivata (admin).");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Impossibile uscire dalla modalità kiosk.", ex);
        }
    }

    /// <summary>Permette la prossima chiusura (usata da Chiudi / Riavvia admin).</summary>
    public void PermitClose() => _allowClose = true;

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
            return;

        // Impedisce chiusura accidentale (Alt+F4, gesture, taskbar, ecc.).
        args.Cancel = true;
        AppLogger.Warning("Tentativo di chiusura bloccato (modalità kiosk).");
    }

    private void SetTopMost(bool topMost)
    {
        SetWindowPos(
            _hwnd,
            topMost ? HwndTopmost : HwndNotopmost,
            0, 0, 0, 0,
            SwpNomove | SwpNosize | SwpShowwindow);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);
}
