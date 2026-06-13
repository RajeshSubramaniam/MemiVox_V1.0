using MyScorer.Core;

namespace MyScorer.Tests;

public class ValidationTests
{
    [Theory]
    [InlineData("23082201", true)]
    [InlineData("setup-abc", true)]
    [InlineData("SETUP001", true)]
    [InlineData("a", true)]
    [InlineData("12345678901234567890", true)]  // exactly 20
    [InlineData("", false)]
    [InlineData("a b", false)]          // space
    [InlineData("a@b", false)]          // special char
    [InlineData("abc/def", false)]      // path traversal
    [InlineData("abc..def", false)]     // dots
    [InlineData("123456789012345678901", false)] // 21 chars
    public void IdPattern_ValidatesCorrectly(string input, bool expected)
    {
        var result = Validation.IdPattern().IsMatch(input);
        Assert.Equal(expected, result);
    }
}
