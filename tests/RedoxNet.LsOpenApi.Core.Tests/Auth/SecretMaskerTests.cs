using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Auth;
using Xunit;

namespace RedoxNet.LsOpenApi.Core.Tests.Auth;

public class SecretMaskerTests
{
    [Fact]
    public void Mask_Null_ReturnsEmptyPlaceholder()
    {
        SecretMasker.Mask(null).Should().Be(SecretMasker.EmptyPlaceholder);
    }

    [Fact]
    public void Mask_Empty_ReturnsEmptyPlaceholder()
    {
        SecretMasker.Mask(string.Empty).Should().Be(SecretMasker.EmptyPlaceholder);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("abc")]
    [InlineData("abcd")]
    public void Mask_ShortInputs_ReturnsBareStars(string input)
    {
        SecretMasker.Mask(input).Should().Be("****");
    }

    [Fact]
    public void Mask_RevealsLastFourCharacters()
    {
        SecretMasker.Mask("abcdefgh").Should().Be("****efgh");
    }

    [Fact]
    public void MaskWithPrefix_KeepsHeadAndTail()
    {
        SecretMasker.MaskWithPrefix("PSabcdefghxyzw", prefixLength: 4).Should().Be("PSab****xyzw");
    }

    [Fact]
    public void MaskWithPrefix_TooShort_ReturnsBareStars()
    {
        SecretMasker.MaskWithPrefix("abcde", prefixLength: 4).Should().Be("****");
    }
}
