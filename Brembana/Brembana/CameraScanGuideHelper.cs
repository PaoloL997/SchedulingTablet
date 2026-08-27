using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace Brembana;

internal static class CameraScanGuideHelper
{
    public const double GuideWidthFraction = 0.32;
    public const double GuideHeightFraction = 0.22;
    public const double GuideVerticalCenterFraction = 0.50;

    public static Rect GetGuideRect(double previewWidth, double previewHeight)
    {
        var width = previewWidth * GuideWidthFraction;
        var height = previewHeight * GuideHeightFraction;
        var centerY = previewHeight * GuideVerticalCenterFraction;
        var top = Math.Clamp(centerY - height / 2, 0, Math.Max(0, previewHeight - height));
        return new Rect((previewWidth - width) / 2, top, width, height);
    }

    public static Rect MirrorGuideForPreviewFlip(Rect guide, double previewWidth, double previewHeight)
    {
        return new Rect(
            previewWidth - guide.X - guide.Width,
            previewHeight - guide.Y - guide.Height,
            guide.Width,
            guide.Height);
    }

    public static Rect GetUniformToFillContentRect(
        double elementWidth,
        double elementHeight,
        double contentWidth,
        double contentHeight)
    {
        if (elementWidth <= 0 || elementHeight <= 0 || contentWidth <= 0 || contentHeight <= 0)
            return new Rect(0, 0, elementWidth, elementHeight);

        var contentAspect = contentWidth / contentHeight;
        var elementAspect = elementWidth / elementHeight;

        if (contentAspect > elementAspect)
        {
            var displayHeight = elementHeight;
            var displayWidth = elementHeight * contentAspect;
            var offsetX = (displayWidth - elementWidth) / 2;
            return new Rect(-offsetX, 0, displayWidth, displayHeight);
        }

        var displayWidthTall = elementWidth;
        var displayHeightTall = elementWidth / contentAspect;
        var offsetY = (displayHeightTall - elementHeight) / 2;
        return new Rect(0, -offsetY, displayWidthTall, displayHeightTall);
    }

    public static BitmapBounds MapGuideToImageBounds(
        Rect guideUi,
        double previewWidth,
        double previewHeight,
        uint imageWidth,
        uint imageHeight)
    {
        if (imageWidth == 0 || imageHeight == 0 || previewWidth <= 0 || previewHeight <= 0)
        {
            return new BitmapBounds
            {
                X = 0,
                Y = 0,
                Width = Math.Max(imageWidth, 1),
                Height = Math.Max(imageHeight, 1)
            };
        }

        var mirrored = MirrorGuideForPreviewFlip(guideUi, previewWidth, previewHeight);
        return MapRectToImageBounds(mirrored, previewWidth, previewHeight, imageWidth, imageHeight);
    }

    public const double CropPaddingFraction = 0.12;

    public static BitmapBounds ApplyPadding(
        BitmapBounds bounds,
        uint imageWidth,
        uint imageHeight,
        double paddingFraction = CropPaddingFraction)
    {
        if (imageWidth == 0 || imageHeight == 0)
            return bounds;

        var padX = (uint)Math.Max(1, bounds.Width * paddingFraction);
        var padY = (uint)Math.Max(1, bounds.Height * paddingFraction);
        var x = bounds.X >= padX ? bounds.X - padX : 0u;
        var y = bounds.Y >= padY ? bounds.Y - padY : 0u;
        var right = Math.Min((ulong)bounds.X + bounds.Width + padX, imageWidth);
        var bottom = Math.Min((ulong)bounds.Y + bounds.Height + padY, imageHeight);
        var width = Math.Max((uint)right - x, 1u);
        var height = Math.Max((uint)bottom - y, 1u);

        return new BitmapBounds { X = x, Y = y, Width = width, Height = height };
    }

    public static Rect MapGuideToNormalizedFocusBounds(
        Rect guideUi,
        double previewWidth,
        double previewHeight,
        uint imageWidth,
        uint imageHeight)
    {
        if (imageWidth == 0 || imageHeight == 0 || previewWidth <= 0 || previewHeight <= 0)
            return new Rect(0.25, 0.35, 0.5, 0.3);

        var pixelBounds = MapGuideToImageBounds(guideUi, previewWidth, previewHeight, imageWidth, imageHeight);
        return new Rect(
            pixelBounds.X / (double)imageWidth,
            pixelBounds.Y / (double)imageHeight,
            pixelBounds.Width / (double)imageWidth,
            pixelBounds.Height / (double)imageHeight);
    }

    public static Rect ClampNormalizedRect(Rect rect)
    {
        var x = Math.Clamp(rect.X, 0, 1);
        var y = Math.Clamp(rect.Y, 0, 1);
        var width = Math.Clamp(rect.Width, 0.05, Math.Max(0.05, 1 - x));
        var height = Math.Clamp(rect.Height, 0.05, Math.Max(0.05, 1 - y));
        return new Rect(x, y, width, height);
    }

    internal static BitmapBounds MapRectToImageBounds(
        Rect guideInPreview,
        double previewWidth,
        double previewHeight,
        uint imageWidth,
        uint imageHeight)
    {
        var contentRect = GetUniformToFillContentRect(previewWidth, previewHeight, imageWidth, imageHeight);

        double ToImageX(double elementX) => (elementX - contentRect.X) / contentRect.Width * imageWidth;
        double ToImageY(double elementY) => (elementY - contentRect.Y) / contentRect.Height * imageHeight;

        var x = (uint)Math.Clamp((int)Math.Floor(ToImageX(guideInPreview.X)), 0, (int)imageWidth - 1);
        var y = (uint)Math.Clamp((int)Math.Floor(ToImageY(guideInPreview.Y)), 0, (int)imageHeight - 1);
        var right = (uint)Math.Clamp((int)Math.Ceiling(ToImageX(guideInPreview.Right)), (int)x + 1, (int)imageWidth);
        var bottom = (uint)Math.Clamp((int)Math.Ceiling(ToImageY(guideInPreview.Bottom)), (int)y + 1, (int)imageHeight);

        return new BitmapBounds
        {
            X = x,
            Y = y,
            Width = Math.Max(right - x, 1),
            Height = Math.Max(bottom - y, 1)
        };
    }
}
