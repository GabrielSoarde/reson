using FluentAssertions;
using Soundpad.Security;

namespace Soundpad.Tests.Security;

public class TokenMaskTests
{
    [Fact]
    public void Mask_Long_Token_Shows_Only_First_And_Last_Four()
    {
        TokenMask.Mask("60dd78bd3517d510cb4faaf470cf1105").Should().Be("60dd…1105");
    }

    [Fact]
    public void Mask_Short_Token_Is_Fully_Hidden()
    {
        TokenMask.Mask("abcdef").Should().Be("…");
        TokenMask.Mask("").Should().Be("…");
    }

    [Fact]
    public void MaskUrl_Masks_The_t_Query_Value_Only()
    {
        TokenMask.MaskUrl("http://192.168.1.101:8080/?t=60dd78bd3517d510cb4faaf470cf1105")
            .Should().Be("http://192.168.1.101:8080/?t=60dd…1105");
    }

    [Fact]
    public void MaskUrl_Without_Token_Is_Unchanged()
    {
        TokenMask.MaskUrl("http://192.168.1.101:8080/").Should().Be("http://192.168.1.101:8080/");
    }

    [Fact]
    public void MaskUrl_Masks_The_t_Fragment_Value()
    {
        TokenMask.MaskUrl("http://192.168.1.101:8080/#t=60dd78bd3517d510cb4faaf470cf1105")
            .Should().Be("http://192.168.1.101:8080/#t=60dd…1105");
    }
}
