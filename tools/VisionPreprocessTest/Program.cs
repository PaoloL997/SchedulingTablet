using System.Runtime.InteropServices.WindowsRuntime;
using Brembana;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

static async Task<int> RunAsync(string imagePath, string? apiKey, bool cropTest, bool pipelineTest, bool mappingTest, bool roiPipelineTest)
{
    Console.WriteLine($"Input: {imagePath}");

    if (mappingTest)
        return RunMappingTests();

    if (roiPipelineTest)
        return await RunRoiPipelineTestAsync(imagePath);

    if (cropTest)
        return await RunCropTestAsync(imagePath);

    if (pipelineTest)
        return await RunPipelineTestAsync(imagePath, apiKey);

    byte[] processed;
    try
    {
        processed = await VisionImageHelper.GetImageBytesAsync(imagePath);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Preprocess FAILED: {ex.Message}");
        Console.Error.WriteLine(ex);
        return 1;
    }

    var isPng = processed.Length >= 8
        && processed[0] == 0x89
        && processed[1] == 0x50
        && processed[2] == 0x4E
        && processed[3] == 0x47;
    if (!isPng)
    {
        Console.Error.WriteLine("Preprocess FAILED: output is not a PNG");
        return 1;
    }

    var outputPath = Path.Combine(Path.GetTempPath(), "BrembanaVisionPreprocessTest.png");
    await File.WriteAllBytesAsync(outputPath, processed);
    Console.WriteLine($"Preprocess OK: {processed.Length} bytes -> {outputPath}");

    if (string.IsNullOrWhiteSpace(apiKey))
    {
        Console.WriteLine("Skipping OCR test (no GOOGLE_CLOUD_VISION_API_KEY).");
        return 0;
    }

    return await RunOcrAsync(imagePath, apiKey);
}

static int RunMappingTests()
{
    const double previewWidth = 1280;
    const double previewHeight = 720;
    const uint imageWidth = 2560;
    const uint imageHeight = 1440;

    var guide = CameraScanGuideHelper.GetGuideRect(previewWidth, previewHeight);
    if (guide.Width <= 0 || guide.Height <= 0)
    {
        Console.Error.WriteLine("Mapping FAILED: guide has invalid size.");
        return 1;
    }

    if (guide.Width > previewWidth * CameraScanGuideHelper.GuideWidthFraction + 1)
    {
        Console.Error.WriteLine("Mapping FAILED: guide width fraction mismatch.");
        return 1;
    }

    var mirrored = CameraScanGuideHelper.MirrorGuideForPreviewFlip(guide, previewWidth, previewHeight);
    if (Math.Abs(mirrored.Y - guide.Y) > 1)
    {
        Console.Error.WriteLine("Mapping FAILED: vertically centered guide should mirror to same Y.");
        return 1;
    }

    var crop = CameraScanGuideHelper.MapGuideToImageBounds(
        guide, previewWidth, previewHeight, imageWidth, imageHeight);
    if (crop.Width == 0 || crop.Height == 0 || crop.Width >= imageWidth || crop.Height >= imageHeight)
    {
        Console.Error.WriteLine("Mapping FAILED: crop bounds invalid.");
        return 1;
    }

    var padded = CameraScanGuideHelper.ApplyPadding(crop, imageWidth, imageHeight);
    if (padded.Width < crop.Width || padded.Height < crop.Height)
    {
        Console.Error.WriteLine("Mapping FAILED: padding did not expand crop bounds.");
        return 1;
    }

    var longEdge = Math.Max(crop.Width, crop.Height);
    Console.WriteLine($"Mapping OK: guide=({guide.X:F0},{guide.Y:F0},{guide.Width:F0}x{guide.Height:F0})");
    Console.WriteLine($"Mapping OK: crop=({crop.X},{crop.Y},{crop.Width}x{crop.Height}) padded=({padded.Width}x{padded.Height}) longEdge={longEdge}");
    return 0;
}

static async Task<int> RunRoiPipelineTestAsync(string imagePath)
{
    var imageBytes = await File.ReadAllBytesAsync(imagePath);
    const double previewWidth = 1280;
    const double previewHeight = 720;

    var roi = await CameraRoiCapturePipeline.ProcessCaptureBytesAsync(
        imageBytes, previewWidth, previewHeight);
    if (roi == null)
    {
        Console.Error.WriteLine("ROI pipeline FAILED: null result.");
        return 1;
    }

    var isPng = roi.RoiPngBytes.Length >= 8
        && roi.RoiPngBytes[0] == 0x89
        && roi.RoiPngBytes[1] == 0x50;
    if (!isPng)
    {
        Console.Error.WriteLine("ROI pipeline FAILED: output is not PNG.");
        return 1;
    }

    var longEdge = Math.Max(roi.RoiWidth, roi.RoiHeight);
    if (longEdge < 200)
    {
        Console.Error.WriteLine($"ROI pipeline FAILED: ROI too small ({roi.RoiWidth}x{roi.RoiHeight}).");
        return 1;
    }

    var outputPath = Path.Combine(Path.GetTempPath(), "BrembanaRoiPipelineTest.png");
    await File.WriteAllBytesAsync(outputPath, roi.RoiPngBytes);
    Console.WriteLine($"ROI pipeline OK: {roi.RoiWidth}x{roi.RoiHeight} sharpness={roi.SharpnessScore:F1} bytes={roi.RoiPngBytes.Length}");
    Console.WriteLine($"Saved: {outputPath}");
    Console.WriteLine($"Sharp enough: {ImageSharpnessHelper.IsSharpEnough(roi.SharpnessScore)}");
    return 0;
}

static async Task<int> RunCropTestAsync(string imagePath)
{
    var imageBytes = await File.ReadAllBytesAsync(imagePath);
    using var inputStream = new InMemoryRandomAccessStream();
    await inputStream.WriteAsync(imageBytes.AsBuffer());
    inputStream.Seek(0);

    var decoder = await BitmapDecoder.CreateAsync(inputStream);
    var imageWidth = decoder.PixelWidth;
    var imageHeight = decoder.PixelHeight;

    const double previewWidth = 1280;
    const double previewHeight = 720;
    var guide = CameraScanGuideHelper.GetGuideRect(previewWidth, previewHeight);
    var crop = CameraScanGuideHelper.MapGuideToImageBounds(
        guide, previewWidth, previewHeight, imageWidth, imageHeight);

    using var bitmap = await decoder.GetSoftwareBitmapAsync();
    var tempFolder = await StorageFolder.GetFolderFromPathAsync(Path.GetTempPath());
    var file = await tempFolder.CreateFileAsync(
        "BrembanaVisionCropTest.jpg",
        CreationCollisionOption.ReplaceExisting);

    using var outStream = await file.OpenAsync(FileAccessMode.ReadWrite);
    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outStream);
    encoder.BitmapTransform.Bounds = crop;
    encoder.BitmapTransform.Rotation = BitmapRotation.Clockwise180Degrees;
    encoder.SetSoftwareBitmap(bitmap);
    await encoder.FlushAsync();

    Console.WriteLine($"Crop test OK: bounds=({crop.X},{crop.Y},{crop.Width}x{crop.Height})");
    Console.WriteLine($"Saved: {file.Path}");
    return 0;
}

static async Task<int> RunPipelineTestAsync(string imagePath, string? apiKey)
{
    var imageBytes = await File.ReadAllBytesAsync(imagePath);
    using var inputStream = new InMemoryRandomAccessStream();
    await inputStream.WriteAsync(imageBytes.AsBuffer());
    inputStream.Seek(0);

    var decoder = await BitmapDecoder.CreateAsync(inputStream);
    var imageWidth = decoder.PixelWidth;
    var imageHeight = decoder.PixelHeight;

    using var bitmap = await decoder.GetSoftwareBitmapAsync(
        BitmapPixelFormat.Bgra8,
        BitmapAlphaMode.Premultiplied,
        new BitmapTransform(),
        ExifOrientationMode.IgnoreExifOrientation,
        ColorManagementMode.DoNotColorManage);

    var tempFolder = await StorageFolder.GetFolderFromPathAsync(Path.GetTempPath());
    var orientedFile = await tempFolder.CreateFileAsync(
        "BrembanaVisionOrientedTest.jpg",
        CreationCollisionOption.ReplaceExisting);

    using (var orientedStream = await orientedFile.OpenAsync(FileAccessMode.ReadWrite))
    {
        var orientedEncoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, orientedStream);
        orientedEncoder.BitmapTransform.Rotation = BitmapRotation.Clockwise180Degrees;
        orientedEncoder.SetSoftwareBitmap(bitmap);
        await orientedEncoder.FlushAsync();
    }

    using var orientedInput = await orientedFile.OpenAsync(FileAccessMode.Read);
    var orientedDecoder = await BitmapDecoder.CreateAsync(orientedInput);
    var orientedWidth = orientedDecoder.PixelWidth;
    var orientedHeight = orientedDecoder.PixelHeight;
    using var orientedBitmap = await orientedDecoder.GetSoftwareBitmapAsync();

    const double previewWidth = 1280;
    const double previewHeight = 720;
    var guide = CameraScanGuideHelper.GetGuideRect(previewWidth, previewHeight);
    var crop = CameraScanGuideHelper.MapRectToImageBounds(
        guide, previewWidth, previewHeight, orientedWidth, orientedHeight);

    var croppedFile = await tempFolder.CreateFileAsync(
        "BrembanaVisionPipelineTest.jpg",
        CreationCollisionOption.ReplaceExisting);

    using (var outStream = await croppedFile.OpenAsync(FileAccessMode.ReadWrite))
    {
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outStream);
        encoder.BitmapTransform.Bounds = crop;
        encoder.BitmapTransform.Rotation = BitmapRotation.None;
        encoder.SetSoftwareBitmap(orientedBitmap);
        await encoder.FlushAsync();
    }

    Console.WriteLine($"Pipeline crop OK: bounds=({crop.X},{crop.Y},{crop.Width}x{crop.Height})");
    Console.WriteLine($"Saved: {croppedFile.Path}");

    if (string.IsNullOrWhiteSpace(apiKey))
    {
        Console.WriteLine("Skipping OCR pipeline test (no GOOGLE_CLOUD_VISION_API_KEY).");
        return 0;
    }

    var croppedResult = await RunOcrAsync(croppedFile.Path, apiKey);
    if (croppedResult == 0)
        return 0;

    Console.WriteLine("Cropped OCR failed, retrying on oriented full image...");
    return await RunOcrAsync(orientedFile.Path, apiKey);
}

static async Task<int> RunOcrAsync(string imagePath, string apiKey)
{
    var service = ComponentVisionServiceFactory.Create(apiKey);
    if (service == null)
    {
        Console.Error.WriteLine("OCR FAILED: could not create vision service.");
        return 1;
    }

    var result = await service.ScanComponentAsync(imagePath, Path.GetFileName(imagePath));
    if (!result.Success)
    {
        Console.Error.WriteLine($"OCR FAILED: {result.ErrorMessage}");
        Console.Error.WriteLine(result.RawResponse);
        return 1;
    }

    if (string.IsNullOrWhiteSpace(result.ComponentCode)
        || result.ComponentCode.Equals("NO_COMPONENT_FOUND", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("OCR FAILED: no component code found.");
        Console.Error.WriteLine($"OCR text: {result.RawTextFromImage?.Replace('\n', ' ')}");
        return 1;
    }

    Console.WriteLine($"OCR OK: code={result.ComponentCode}");
    Console.WriteLine($"OCR text: {result.RawTextFromImage?.Replace('\n', ' ')}");
    return 0;
}

static string? LoadApiKey()
{
    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, ".env"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SchedulingTablet", "SchedulingTablet", ".env"),
    };

    foreach (var path in candidates)
    {
        if (!File.Exists(path))
            continue;

        foreach (var line in File.ReadAllLines(path))
        {
            if (!line.StartsWith("GOOGLE_CLOUD_VISION_API_KEY=", StringComparison.Ordinal))
                continue;

            var value = line["GOOGLE_CLOUD_VISION_API_KEY=".Length..].Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    return Environment.GetEnvironmentVariable("GOOGLE_CLOUD_VISION_API_KEY");
}

var cropTest = args.Contains("--crop-test", StringComparer.OrdinalIgnoreCase);
var pipelineTest = args.Contains("--pipeline-test", StringComparer.OrdinalIgnoreCase);
var mappingTest = args.Contains("--mapping-test", StringComparer.OrdinalIgnoreCase);
var roiPipelineTest = args.Contains("--roi-pipeline-test", StringComparer.OrdinalIgnoreCase);
var imagePath = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
    ?? @"C:\Users\plitta\dev\test_google_vision_api\WIN_20260618_08_39_26_Pro.jpg";

if (!mappingTest)
{
    if (!roiPipelineTest && !File.Exists(imagePath))
    {
        Console.Error.WriteLine($"Image not found: {imagePath}");
        return 1;
    }
}

return await RunAsync(imagePath, LoadApiKey(), cropTest, pipelineTest, mappingTest, roiPipelineTest);
