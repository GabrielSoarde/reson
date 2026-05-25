using FluentAssertions;
using Soundpad.Sound;

namespace Soundpad.Tests.Sound;

public class SoundColorsTests
{
    [Fact] public void First_Is_The_Classic_Blue() => SoundColors.PickInitial(0).Should().Be("#3b82f6");
    [Fact] public void Second_Differs_From_First() => SoundColors.PickInitial(1).Should().NotBe(SoundColors.PickInitial(0));
    [Fact] public void Wraps_After_Palette_Length() => SoundColors.PickInitial(SoundColors.Palette.Length).Should().Be("#3b82f6");
    [Fact] public void Negative_Index_Is_Safe() => SoundColors.Palette.Should().Contain(SoundColors.PickInitial(-1));
    [Fact] public void All_Entries_Are_Hex() => SoundColors.Palette.Should().OnlyContain(c => System.Text.RegularExpressions.Regex.IsMatch(c, "^#[0-9a-fA-F]{6}$"));
}
