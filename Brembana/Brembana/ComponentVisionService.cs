using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Brembana;

public interface IComponentVisionService
{
    Task<ComponentScanResult> ScanComponentAsync(string filePath, string fileName);
    Task<ComponentScanResult> ScanComponentFromBytesAsync(byte[] imageBytes, string fileName, string mimeType = "image/png");
}

public static class ComponentVisionServiceFactory
{
    public static IComponentVisionService? Create(string? googleCloudVisionApiKey)
    {
        if (string.IsNullOrWhiteSpace(googleCloudVisionApiKey))
            return null;

        return new GoogleVisionService(googleCloudVisionApiKey.Trim());
    }
}

internal static class VisionImageHelper
{
    public static string GetMimeType(string filePath) => Path.GetExtension(filePath).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        _ => "image/jpeg"
    };

    public static async Task<byte[]> GetImageBytesAsync(string filePath)
    {
        var rawBytes = await File.ReadAllBytesAsync(filePath);
        return await PrepareImageForOcrAsync(rawBytes);
    }

    public static Task<byte[]> PrepareImageForOcrAsync(byte[] imageBytes)
        => PrepareImageBytesForOcrInternalAsync(imageBytes);

    private static async Task<byte[]> PrepareImageBytesForOcrInternalAsync(byte[] imageBytes)
    {
        using var inputStream = new InMemoryRandomAccessStream();
        await inputStream.WriteAsync(imageBytes.AsBuffer());
        inputStream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(inputStream);
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);

        var pixels = pixelData.DetachPixelData();
        var processed = ApplyGrayscaleAutocontrast(pixels);

        using var outputStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outputStream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            decoder.OrientedPixelWidth,
            decoder.OrientedPixelHeight,
            decoder.DpiX,
            decoder.DpiY,
            processed);
        await encoder.FlushAsync();

        outputStream.Seek(0);
        var result = new byte[outputStream.Size];
        await outputStream.ReadAsync(result.AsBuffer(), (uint)outputStream.Size, InputStreamOptions.None);
        return result;
    }

    private static byte[] ApplyGrayscaleAutocontrast(byte[] bgra)
    {
        var count = bgra.Length / 4;
        var gray = new byte[count];
        byte min = 255, max = 0;

        for (var i = 0; i < count; i++)
        {
            var offset = i * 4;
            var value = (byte)(0.299 * bgra[offset + 2] + 0.587 * bgra[offset + 1] + 0.114 * bgra[offset]);
            gray[i] = value;
            if (value < min) min = value;
            if (value > max) max = value;
        }

        var result = new byte[bgra.Length];
        for (var i = 0; i < count; i++)
        {
            var value = gray[i];
            if (min != max)
                value = (byte)((gray[i] - min) * 255 / (max - min));

            var offset = i * 4;
            result[offset] = value;
            result[offset + 1] = value;
            result[offset + 2] = value;
            result[offset + 3] = 255;
        }

        return result;
    }
}

public sealed class GoogleVisionService : IComponentVisionService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public GoogleVisionService(string apiKey)
    {
        _apiKey = apiKey;
        _httpClient = new HttpClient();
    }

    public async Task<ComponentScanResult> ScanComponentAsync(string filePath, string fileName)
    {
        try
        {
            var imageBytes = await VisionImageHelper.GetImageBytesAsync(filePath);
            return await ScanPreparedImageAsync(imageBytes, fileName);
        }
        catch (Exception ex)
        {
            return CreateErrorResult(ex, fileName);
        }
    }

    public async Task<ComponentScanResult> ScanComponentFromBytesAsync(
        byte[] imageBytes,
        string fileName,
        string mimeType = "image/png")
    {
        try
        {
            var prepared = await VisionImageHelper.PrepareImageForOcrAsync(imageBytes);
            return await ScanPreparedImageAsync(prepared, fileName);
        }
        catch (Exception ex)
        {
            return CreateErrorResult(ex, fileName);
        }
    }

    private async Task<ComponentScanResult> ScanPreparedImageAsync(byte[] imageBytes, string fileName)
    {
        try
        {
            var requestBody = new
            {
                requests = new object[]
                {
                    new
                    {
                        image = new { content = Convert.ToBase64String(imageBytes) },
                        features = new object[]
                        {
                            new { type = "TEXT_DETECTION" }
                        }
                    }
                }
            };

            var url = $"https://vision.googleapis.com/v1/images:annotate?key={Uri.EscapeDataString(_apiKey)}";
            var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content);
            var respJson = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new Exception($"Google Vision API error: {respJson}");

            var root = JObject.Parse(respJson);
            var firstResponse = root["responses"]?[0] as JObject;
            var visionError = firstResponse?["error"]?.ToString();
            if (!string.IsNullOrWhiteSpace(visionError))
            {
                return new ComponentScanResult
                {
                    Success = false,
                    ErrorMessage = $"Google Vision error: {visionError}",
                    FileName = fileName,
                    ComponentCode = "ERROR",
                    RawResponse = respJson
                };
            }

            var fullText = firstResponse?["fullTextAnnotation"]?["text"]?.ToString() ?? "";
            var candidates = ExtractCandidateCodes(fullText);
            var candidateCode = candidates.FirstOrDefault() ?? "";

            return new ComponentScanResult
            {
                Success = true,
                FileName = fileName,
                ComponentCode = string.IsNullOrWhiteSpace(candidateCode) ? "NO_COMPONENT_FOUND" : candidateCode,
                ComponentType = "unknown",
                AllCodes = candidates,
                RawTextFromImage = fullText,
                RawResponse = respJson
            };
        }
        catch (Exception ex)
        {
            return CreateErrorResult(ex, fileName);
        }
    }

    private static ComponentScanResult CreateErrorResult(Exception ex, string fileName)
    {
        return new ComponentScanResult
        {
            Success = false,
            ErrorMessage = ex.Message,
            FileName = fileName,
            ComponentCode = "ERROR",
            RawResponse = ex.ToString()
        };
    }

    private static List<string> ExtractCandidateCodes(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        var separators = new[] { ' ', '\n', '\r', '\t', ',', ';', ':', '/', '\\', '|', '.', '(', ')', '[', ']', '{', '}' };
        var parts = text.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ranked = parts
            .Select(NormalizeCode)
            .Where(IsLikelyComponentCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(ScoreComponentCode)
            .ThenBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return ranked;
    }

    private static string NormalizeCode(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Trim('-', '_');
    }

    private static bool IsLikelyComponentCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (value.Length is < 2 or > 24)
            return false;

        var alnumCount = value.Count(char.IsLetterOrDigit);
        if (alnumCount < value.Length)
            return false;

        return value.Any(char.IsLetter) && value.Any(char.IsDigit);
    }

    private static int ScoreComponentCode(string value)
    {
        var score = value.Length;
        if (value.Any(char.IsUpper))
            score += 2;
        if (value.Any(char.IsDigit))
            score += 2;
        return score;
    }
}

public class ComponentScanResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? FileName { get; set; }
    public string? ComponentCode { get; set; }
    public string? RawResponse { get; set; }
    public string? ComponentType { get; set; }
    public List<string>? AllCodes { get; set; }
    public string? RawTextFromImage { get; set; }
}
