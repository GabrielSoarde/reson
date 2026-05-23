using FluentAssertions;
using Soundpad.Update;

namespace Soundpad.Tests.Update;

public class UpdateCheckerTests
{
    [Theory]
    // Newer → true
    [InlineData("v1.0.1", "1.0.0", true)]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("v2.0.0", "1.9.9", true)]
    [InlineData("v1.1.0", "1.0.9", true)]
    [InlineData("V1.0.1", "1.0.0", true)]   // uppercase V stripped too
    [InlineData(" v1.0.1 ", "1.0.0", true)] // surrounding whitespace tolerated
    [InlineData("v1.0.0.1", "1.0.0.0", true)]
    // Equal → false
    [InlineData("v1.0.0", "1.0.0", false)]
    [InlineData("1.0.0", "1.0.0", false)]
    // Older → false
    [InlineData("v1.0.0", "1.0.1", false)]
    [InlineData("v0.9.9", "1.0.0", false)]
    // Malformed → false (never offer a bogus update)
    [InlineData("vbanana", "1.0.0", false)]
    [InlineData("v1.0.1", "garbage", false)]
    [InlineData("", "1.0.0", false)]
    [InlineData(null, "1.0.0", false)]
    [InlineData("v1.0.1", null, false)]
    [InlineData("latest", "1.0.0", false)]
    public void IsNewer_Compares_Versions(string? latestTag, string? current, bool expected)
    {
        UpdateChecker.IsNewer(latestTag, current).Should().Be(expected);
    }
}
