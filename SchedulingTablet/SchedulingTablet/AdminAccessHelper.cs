using Microsoft.UI.Xaml.Input;

namespace Brembana;

/// <summary>
/// Pressione prolungata (5s) su un elemento UI per sbloccare il menu amministratore.
/// </summary>
internal sealed class AdminAccessHelper
{
    public static readonly TimeSpan HoldDuration = TimeSpan.FromSeconds(5);

    private readonly DispatcherTimer _holdTimer;
    private readonly Func<Task> _onUnlocked;
    private bool _isHolding;

    public AdminAccessHelper(UIElement target, Func<Task> onUnlocked)
    {
        _onUnlocked = onUnlocked;
        _holdTimer = new DispatcherTimer { Interval = HoldDuration };
        _holdTimer.Tick += async (_, _) =>
        {
            _holdTimer.Stop();
            if (!_isHolding)
                return;
            _isHolding = false;
            try
            {
                await _onUnlocked();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Errore sblocco amministratore.", ex);
            }
        };

        target.PointerPressed += OnPointerPressed;
        target.PointerReleased += OnPointerReleased;
        target.PointerCanceled += OnPointerCanceled;
        target.PointerCaptureLost += OnPointerCaptureLost;
        target.PointerExited += OnPointerExited;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isHolding = true;
        _holdTimer.Stop();
        _holdTimer.Start();
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e) => CancelHold(sender, e);

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e) => CancelHold(sender, e);

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e) => CancelHold();

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Su touch, uscire dall'elemento interrompe il hold.
        if (_isHolding)
            CancelHold();
    }

    private void CancelHold(object? sender = null, PointerRoutedEventArgs? e = null)
    {
        _isHolding = false;
        _holdTimer.Stop();
        if (sender is UIElement element && e != null)
            element.ReleasePointerCapture(e.Pointer);
    }
}
