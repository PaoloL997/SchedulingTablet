using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Brembana;

internal sealed class RoiCaptureResult
{
    public required byte[] RoiPngBytes { get; init; }
    public required byte[] FullFrameJpegBytes { get; init; }
    public required uint RoiWidth { get; init; }
    public required uint RoiHeight { get; init; }
    public required double SharpnessScore { get; init; }
}

internal static class CameraRoiCapturePipeline
{
    public static async Task<RoiCaptureResult?> ProcessCaptureStreamAsync(
        IRandomAccessStream photoStream,
        double previewWidth,
        double previewHeight)
    {
        photoStream.Seek(0);
        var jpegBytes = new byte[photoStream.Size];
        await photoStream.ReadAsync(jpegBytes.AsBuffer(), (uint)photoStream.Size, InputStreamOptions.None);

        return await ProcessCaptureBytesAsync(jpegBytes, previewWidth, previewHeight);
    }

    public static async Task<RoiCaptureResult?> ProcessCaptureBytesAsync(
        byte[] jpegBytes,
        double previewWidth,
        double previewHeight)
    {
        if (jpegBytes.Length == 0 || previewWidth <= 0 || previewHeight <= 0)
            return null;

        using var inputStream = new InMemoryRandomAccessStream();
        await inputStream.WriteAsync(jpegBytes.AsBuffer());
        inputStream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(inputStream);
        var imageWidth = decoder.PixelWidth;
        var imageHeight = decoder.PixelHeight;
        if (imageWidth == 0 || imageHeight == 0)
            return null;

        var guide = CameraScanGuideHelper.GetGuideRect(previewWidth, previewHeight);
        var cropBounds = CameraScanGuideHelper.MapGuideToImageBounds(
            guide, previewWidth, previewHeight, imageWidth, imageHeight);
        cropBounds = CameraScanGuideHelper.ApplyPadding(cropBounds, imageWidth, imageHeight);

        var transform = new BitmapTransform { Bounds = cropBounds };

        var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        if (bitmap == null)
            return null;

        try
        {
            var pixels = await GetPixelBytesAsync(bitmap);
            var sharpness = ImageSharpnessHelper.ComputeLaplacianVariance(
                pixels, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight);

            var roiPng = await EncodePngAsync(bitmap);
            if (roiPng == null)
                return null;

            return new RoiCaptureResult
            {
                RoiPngBytes = roiPng,
                FullFrameJpegBytes = jpegBytes,
                RoiWidth = (uint)bitmap.PixelWidth,
                RoiHeight = (uint)bitmap.PixelHeight,
                SharpnessScore = sharpness
            };
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    private static async Task<byte[]?> EncodePngAsync(SoftwareBitmap bitmap)
    {
        using var outputStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outputStream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();

        outputStream.Seek(0);
        var bytes = new byte[outputStream.Size];
        await outputStream.ReadAsync(bytes.AsBuffer(), (uint)outputStream.Size, InputStreamOptions.None);
        return bytes;
    }

    private static Task<byte[]> GetPixelBytesAsync(SoftwareBitmap bitmap)
    {
        using var converted = SoftwareBitmap.Convert(
            bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var buffer = new byte[4 * converted.PixelWidth * converted.PixelHeight];
        converted.CopyToBuffer(buffer.AsBuffer());
        return Task.FromResult(buffer);
    }
}
