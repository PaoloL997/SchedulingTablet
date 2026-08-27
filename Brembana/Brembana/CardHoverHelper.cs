using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Brembana;

internal static class CardHoverHelper
{
    public const double HostMarginH = 4;
    public const double HostMarginV = 4;
    public static Thickness HostMargin => new(HostMarginH, HostMarginV, HostMarginH, HostMarginV);

    private const float HoverScale = 1.02f;
    private const float PressedScale = 1.01f;
    private const int HoverZIndex = 1000;
    private static readonly TimeSpan AnimDuration = TimeSpan.FromMilliseconds(220);
    private static readonly Dictionary<Button, HoverState> States = new();

    private sealed class HoverState
    {
        public required Grid Host { get; init; }
        public required Brush NormalBackground { get; set; }
        public required Brush NormalBorder { get; set; }
        public required Thickness NormalBorderThickness { get; set; }
        public required Color AccentColor { get; init; }
        public bool IsHovered { get; set; }
        public bool IsPressed { get; set; }
        public bool IsPinned { get; set; }
    }

    public static Grid WrapCard(Button button, Color accentColor, bool pinned = false)
    {
        var host = new Grid
        {
            Margin = HostMargin,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.VerticalAlignment = VerticalAlignment.Stretch;
        host.Children.Add(button);
        Attach(button, accentColor, pinned, host);
        return host;
    }

    private static void Attach(Button button, Color accentColor, bool pinned, Grid host)
    {
        var state = new HoverState
        {
            Host = host,
            NormalBackground = button.Background,
            NormalBorder = button.BorderBrush,
            NormalBorderThickness = button.BorderThickness,
            AccentColor = accentColor,
            IsPinned = pinned
        };
        States[button] = state;

        button.UseSystemFocusVisuals = false;
        button.Unloaded += (_, _) => States.Remove(button);
        button.PointerEntered += OnPointerEntered;
        button.PointerExited += OnPointerExited;
        button.PointerPressed += OnPointerPressed;
        button.PointerReleased += OnPointerReleased;
        button.PointerCanceled += OnPointerEnded;

        if (pinned)
        {
            state.NormalBackground = button.Background;
            state.NormalBorder = button.BorderBrush;
            state.NormalBorderThickness = button.BorderThickness;
        }
    }

    private static bool TryGetState(object sender, out Button button, out HoverState state)
    {
        if (sender is Button b && States.TryGetValue(b, out var s))
        {
            button = b;
            state = s;
            return true;
        }
        button = null!;
        state = null!;
        return false;
    }

    private static void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!TryGetState(sender, out var button, out var state)) return;
        state.IsHovered = true;
        Canvas.SetZIndex(state.Host, HoverZIndex);
        ApplyHoverVisual(button, state, hovered: true, pressed: state.IsPressed);
        AnimateScale(state.Host, state.IsPressed ? PressedScale : HoverScale);
    }

    private static void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!TryGetState(sender, out var button, out var state)) return;
        state.IsHovered = false;
        state.IsPressed = false;
        Canvas.SetZIndex(state.Host, 0);
        RestoreRestingVisual(button, state);
        AnimateScale(state.Host, 1f);
    }

    private static void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!TryGetState(sender, out var button, out var state)) return;
        state.IsPressed = true;
        Canvas.SetZIndex(state.Host, HoverZIndex);
        ApplyHoverVisual(button, state, hovered: true, pressed: true);
        AnimateScale(state.Host, PressedScale);
    }

    private static void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        => OnPointerEnded(sender, e);

    private static void OnPointerEnded(object sender, PointerRoutedEventArgs e)
    {
        if (!TryGetState(sender, out var button, out var state)) return;
        state.IsPressed = false;
        if (state.IsHovered || state.IsPinned)
        {
            Canvas.SetZIndex(state.Host, state.IsHovered ? HoverZIndex : 0);
            ApplyHoverVisual(button, state, hovered: true, pressed: false);
            AnimateScale(state.Host, state.IsHovered ? HoverScale : 1f);
        }
        else
        {
            Canvas.SetZIndex(state.Host, 0);
            RestoreRestingVisual(button, state);
            AnimateScale(state.Host, 1f);
        }
    }

    private static void RestoreRestingVisual(Button button, HoverState state)
    {
        button.Background = state.NormalBackground;
        button.BorderBrush = state.NormalBorder;
        button.BorderThickness = state.NormalBorderThickness;
    }

    private static void ApplyHoverVisual(Button button, HoverState state, bool hovered, bool pressed)
    {
        if (!hovered && !state.IsPinned)
        {
            RestoreRestingVisual(button, state);
            return;
        }

        var baseColor = ThemeHelper.GetColor("BrembanaSurfaceBrush");
        var accent = state.AccentColor;
        var mix = pressed ? 0.12 : state.IsPinned ? 0.16 : 0.18;
        button.Background = new SolidColorBrush(Color.FromArgb(
            255,
            (byte)Math.Min(255, (int)(baseColor.R + (accent.R - baseColor.R) * mix)),
            (byte)Math.Min(255, (int)(baseColor.G + (accent.G - baseColor.G) * mix)),
            (byte)Math.Min(255, (int)(baseColor.B + (accent.B - baseColor.B) * mix))));
        button.BorderBrush = new SolidColorBrush(Color.FromArgb(
            pressed ? (byte)255 : (byte)230,
            accent.R, accent.G, accent.B));
        button.BorderThickness = pressed ? new Thickness(2.5) : new Thickness(2);
    }

    private static void AnimateScale(UIElement element, float targetScale)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        if (element is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
            visual.CenterPoint = new System.Numerics.Vector3((float)fe.ActualWidth / 2f, (float)fe.ActualHeight / 2f, 0f);

        var easing = compositor.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.22f, 1f),
            new System.Numerics.Vector2(0.36f, 1f));

        var scaleX = compositor.CreateScalarKeyFrameAnimation();
        scaleX.InsertKeyFrame(1f, targetScale, easing);
        scaleX.Duration = AnimDuration;

        var scaleY = compositor.CreateScalarKeyFrameAnimation();
        scaleY.InsertKeyFrame(1f, targetScale, easing);
        scaleY.Duration = AnimDuration;

        visual.StartAnimation("Scale.X", scaleX);
        visual.StartAnimation("Scale.Y", scaleY);
    }
}
