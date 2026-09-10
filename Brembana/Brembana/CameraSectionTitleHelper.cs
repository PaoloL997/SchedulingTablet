namespace Brembana;

internal static class CameraSectionTitleHelper
{
    /// <summary>Il titolo della fotocamera dice cosa si sta scansionando: uscita o rientro.</summary>
    public static string ForCameraSession(bool isOutgoing) =>
        isOutgoing ? UiStrings.CameraScanTitleOutgoing : UiStrings.CameraScanTitleIncoming;
}
