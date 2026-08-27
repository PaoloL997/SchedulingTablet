using Windows.ApplicationModel.DataTransfer;
using Windows.Devices.Enumeration;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Brembana;

internal static class PhotoCaptureHelper
{
    private const string WindowsCameraPackageFamilyName = "Microsoft.WindowsCamera_8wekyb3d8bbwe";

    public static async Task<bool> HasCameraAsync()
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            return devices.Any(d => d.IsEnabled);
        }
        catch
        {
            return false;
        }
    }

    public static bool ShouldFallbackToFilePicker(Exception ex)
    {
        var message = ex.Message;
        return message.Contains("identità del pacchetto", StringComparison.OrdinalIgnoreCase)
            || message.Contains("package identity", StringComparison.OrdinalIgnoreCase)
            || ex.HResult == unchecked((int)0x80070490); // ERROR_NOT_FOUND
    }

    public static async Task<StorageFile?> GetPhotoWithoutOverlayAsync(IntPtr hwnd)
    {
        if (!await HasCameraAsync())
            return await PickPhotoFileAsync(hwnd);

        try
        {
            var captured = await CapturePhotoViaWindowsCameraAppAsync(hwnd);
            if (captured != null)
                return captured;

            return null;
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (Exception ex) when (ShouldFallbackToFilePicker(ex))
        {
            // Ultimo fallback: file picker.
        }

        return await PickPhotoFileAsync(hwnd);
    }

    public static async Task<StorageFile?> CapturePhotoViaWindowsCameraAppAsync(IntPtr hwnd)
    {
        var tempFolder = await StorageFolder.GetFolderFromPathAsync(Path.GetTempPath());
        var tempFile = await tempFolder.CreateFileAsync(
            "BrembanaCapture.jpg",
            CreationCollisionOption.GenerateUniqueName);
        var token = SharedStorageAccessManager.AddFile(tempFile);

        var options = new LauncherOptions
        {
            TreatAsUntrusted = false,
            DisplayApplicationPicker = false,
            TargetApplicationPackageFamilyName = WindowsCameraPackageFamilyName
        };
        InitializeWithWindow.Initialize(options, hwnd);

        var input = new ValueSet
        {
            { "MediaType", "photo" },
            { "PhotoFileToken", token }
        };

        var result = await Launcher.LaunchUriForResultsAsync(
            new Uri("microsoft.windows.camera.picker:"),
            options,
            input);

        if (result.Status == LaunchUriStatus.Success && result.Result != null)
            return tempFile;

        return null;
    }

    public static async Task<StorageFile?> PickPhotoFileAsync(IntPtr hwnd)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".webp");
        picker.FileTypeFilter.Add(".gif");
        picker.ViewMode = PickerViewMode.Thumbnail;
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        return await picker.PickSingleFileAsync();
    }
}
