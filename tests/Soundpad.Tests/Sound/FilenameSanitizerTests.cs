using FluentAssertions;
using Soundpad.Sound;

namespace Soundpad.Tests.Sound;

public class FilenameSanitizerTests
{
    [Theory]
    [InlineData("hello.mp3", "hello.mp3")]
    [InlineData("ra_ze ult (1).mp3", "ra_ze ult (1).mp3")]
    [InlineData("foo/bar.mp3", "foo_bar.mp3")]
    [InlineData("foo\\bar.mp3", "foo_bar.mp3")]
    [InlineData("..\\evil.mp3", "__evil.mp3")]
    [InlineData("foo:bar.mp3", "foo_bar.mp3")]
    [InlineData("foo*bar?.mp3", "foo_bar_.mp3")]
    public void Replaces_Invalid_Chars(string input, string expected)
    {
        FilenameSanitizer.Sanitize(input, fallbackId: "fallback").Should().Be(expected);
    }

    [Theory]
    [InlineData("CON.mp3", "_CON.mp3")]
    [InlineData("con.MP3", "_con.MP3")]
    [InlineData("PRN.wav", "_PRN.wav")]
    [InlineData("AUX.ogg", "_AUX.ogg")]
    [InlineData("NUL.flac", "_NUL.flac")]
    [InlineData("COM1.mp3", "_COM1.mp3")]
    [InlineData("LPT9.mp3", "_LPT9.mp3")]
    [InlineData("CON", "_CON")]
    public void Prefixes_Reserved_Windows_Names(string input, string expected)
    {
        FilenameSanitizer.Sanitize(input, fallbackId: "fallback").Should().Be(expected);
    }

    [Theory]
    [InlineData("hello.mp3.", "hello.mp3")]
    [InlineData("hello.mp3 ", "hello.mp3")]
    [InlineData("hello.mp3...", "hello.mp3")]
    [InlineData("hello.mp3   ", "hello.mp3")]
    public void Trims_Trailing_Dots_And_Spaces(string input, string expected)
    {
        FilenameSanitizer.Sanitize(input, fallbackId: "fallback").Should().Be(expected);
    }

    [Fact]
    public void Strips_Control_Chars()
    {
        // chars below 0x20 should become _
        FilenameSanitizer.Sanitize("hello.mp3", fallbackId: "fallback").Should().Be("hello_.mp3");
        FilenameSanitizer.Sanitize("hello.mp3", fallbackId: "fallback").Should().Be("hello_.mp3");
        FilenameSanitizer.Sanitize("hello.mp3", fallbackId: "fallback").Should().Be("hello_.mp3");
    }

    [Theory]
    [InlineData(".mp3")]
    [InlineData("...")]
    [InlineData("   ")]
    [InlineData("")]
    public void Falls_Back_When_Empty_After_Sanitize(string input)
    {
        var result = FilenameSanitizer.Sanitize(input, fallbackId: "myid");
        result.Should().StartWith("myid");
    }

    [Fact]
    public void Preserves_Original_Extension_In_Fallback()
    {
        FilenameSanitizer.Sanitize("...", fallbackId: "myid", extension: ".mp3")
            .Should().Be("myid.mp3");
    }
}
