using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage.Streams;

static ImageEncodingProperties CreateMaxPhotoEncoding(MediaCapture capture)
{
    var props = ImageEncodingProperties.CreateJpeg();
    var best = capture.VideoDeviceController
        .GetAvailableMediaStreamProperties(MediaStreamType.Photo)
        .OfType<ImageEncodingProperties>()
        .Where(p => p.Width > 0 && p.Height > 0)
        .OrderByDescending(p => (long)p.Width * p.Height)
        .FirstOrDefault();
    if (best is { Width: > 0, Height: > 0 })
    {
        props.Width = best.Width;
        props.Height = best.Height;
        Console.WriteLine($"Photo stream: {best.Width}x{best.Height}");
    }

    return props;
}

static async Task ConfigurePreviewStreamAsync(MediaCapture capture)
{
    const long targetPixels = 960L * 540L;
    var previewProps = capture.VideoDeviceController
        .GetAvailableMediaStreamProperties(MediaStreamType.VideoPreview)
        .OfType<VideoEncodingProperties>()
        .Where(p => p.Width > 0 && p.Height > 0)
        .OrderBy(p => Math.Abs((long)p.Width * p.Height - targetPixels))
        .FirstOrDefault();

    if (previewProps == null)
        return;

    try
    {
        await capture.VideoDeviceController
            .SetMediaStreamPropertiesAsync(MediaStreamType.VideoPreview, previewProps);
        Console.WriteLine($"Preview stream: {previewProps.Width}x{previewProps.Height}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Preview config skipped: {ex.Message}");
    }
}

static async Task<bool> ConfigurePhotoStreamAsync(MediaCapture capture)
{
    var candidates = capture.VideoDeviceController
        .GetAvailableMediaStreamProperties(MediaStreamType.Photo)
        .OfType<ImageEncodingProperties>()
        .Where(p => p.Width > 0 && p.Height > 0)
        .ToList();

    var ordered = candidates
        .OrderByDescending(p => (long)p.Width * p.Height)
        .Concat(candidates.OrderBy(p => Math.Abs((long)p.Width * p.Height - 1920L * 1080L)))
        .DistinctBy(p => $"{p.Width}x{p.Height}")
        .ToList();

    foreach (var photoProps in ordered)
    {
        try
        {
            await capture.VideoDeviceController
                .SetMediaStreamPropertiesAsync(MediaStreamType.Photo, photoProps);
            Console.WriteLine($"Photo stream configured: {photoProps.Width}x{photoProps.Height}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Photo config failed for {photoProps.Width}x{photoProps.Height}: {ex.Message}");
        }
    }

    return false;
}

static async Task<(MediaCapture capture, MediaFrameSource frameSource)> CreateCaptureAsync()
{
    var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
    var device = devices.FirstOrDefault(d => d.IsEnabled)
        ?? throw new InvalidOperationException("Nessuna fotocamera abilitata.");

    MediaCapture? capture = null;
    foreach (var memoryPreference in new[]
             {
                 MediaCaptureMemoryPreference.Cpu,
                 MediaCaptureMemoryPreference.Auto
             })
    {
        capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = device.Id,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                MemoryPreference = memoryPreference
            });
            Console.WriteLine($"MediaCapture memory: {memoryPreference}");
            break;
        }
        catch
        {
            capture.Dispose();
            capture = null;
        }
    }

    if (capture == null)
        throw new InvalidOperationException("Impossibile inizializzare MediaCapture.");

    await ConfigurePreviewStreamAsync(capture);
    await ConfigurePhotoStreamAsync(capture);

    var frameSource = capture.FrameSources.Values.FirstOrDefault(source =>
            source.Info.MediaStreamType == MediaStreamType.VideoPreview &&
            source.Info.SourceKind == MediaFrameSourceKind.Color)
        ?? capture.FrameSources.Values.FirstOrDefault(source =>
            source.Info.MediaStreamType == MediaStreamType.VideoRecord &&
            source.Info.SourceKind == MediaFrameSourceKind.Color)
        ?? throw new InvalidOperationException("Frame source non disponibile.");

    return (capture, frameSource);
}

static async Task<bool> TryLowLagCaptureAsync(LowLagPhotoCapture lowLag, string label)
{
    try
    {
        var captured = await lowLag.CaptureAsync();
        using var stream = new InMemoryRandomAccessStream();
        await RandomAccessStream.CopyAsync(captured.Frame, stream);
        captured.Frame.Dispose();
        Console.WriteLine($"OK: {label} ({stream.Size} bytes)");
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL: {label} -> {ex.Message}");
        return false;
    }
}

static async Task<bool> TryStandardCaptureAsync(MediaCapture capture, ImageEncodingProperties encoding, string label)
{
    try
    {
        using var stream = new InMemoryRandomAccessStream();
        await capture.CapturePhotoToStreamAsync(encoding, stream);
        Console.WriteLine($"OK: {label} ({stream.Size} bytes)");
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL: {label} -> {ex.Message}");
        return false;
    }
}

var (capture, frameSource) = await CreateCaptureAsync();
var encoding = CreateMaxPhotoEncoding(capture);

var player = new MediaPlayer { RealTimePlayback = true, AutoPlay = false };
player.Source = MediaSource.CreateFromMediaFrameSource(frameSource);
player.Play();
await Task.Delay(700);

LowLagPhotoCapture? lowLag = null;
var lowLagOk = false;
try
{
    lowLag = await capture.PrepareLowLagPhotoCaptureAsync(encoding);
    lowLagOk = await TryLowLagCaptureAsync(lowLag, "low-lag capture con anteprima attiva");
    if (lowLagOk)
    {
        lowLagOk = await TryLowLagCaptureAsync(lowLag, "secondo low-lag capture con anteprima attiva");
    }
}
finally
{
    if (lowLag != null)
        await lowLag.FinishAsync();
}

if (!lowLagOk)
{
    lowLagOk = await TryStandardCaptureAsync(capture, encoding, "standard capture con anteprima attiva");
}

player.Pause();
player.Source = null;
player.Dispose();
capture.Dispose();

if (lowLagOk)
{
    Console.WriteLine("Capture with active preview: PASS");
    return 0;
}

return 1;
