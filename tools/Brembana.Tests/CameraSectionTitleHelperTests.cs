using Brembana;
using Xunit;

namespace Brembana.Tests;

public class CameraSectionTitleHelperTests
{
    [Fact]
    public void Camera_session_says_whether_it_is_an_exit_or_a_return()
    {
        Assert.Equal(UiStrings.CameraScanTitleOutgoing, CameraSectionTitleHelper.ForCameraSession(isOutgoing: true));
        Assert.Equal(UiStrings.CameraScanTitleIncoming, CameraSectionTitleHelper.ForCameraSession(isOutgoing: false));
        Assert.Contains("Uscita", UiStrings.CameraScanTitleOutgoing);
        Assert.Contains("Rientro", UiStrings.CameraScanTitleIncoming);
    }

    [Fact]
    public void No_title_mentions_the_vision_provider()
    {
        var titles = new[]
        {
            CameraSectionTitleHelper.ForCameraSession(isOutgoing: true),
            CameraSectionTitleHelper.ForCameraSession(isOutgoing: false)
        };

        Assert.All(titles, t => Assert.DoesNotContain("Vision", t, StringComparison.OrdinalIgnoreCase));
    }
}
