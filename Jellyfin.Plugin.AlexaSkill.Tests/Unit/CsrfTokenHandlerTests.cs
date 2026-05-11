using System.Collections.Generic;
using Jellyfin.Plugin.AlexaSkill.Controller.Handler;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class CsrfTokenHandlerTests
{
    [Fact]
    public void GetNewCsrfToken_ReturnsToken()
    {
        var handler = new CsrfTokenHandler();
        var token = handler.GetNewCsrfToken();

        Assert.NotNull(token);
        Assert.False(string.IsNullOrEmpty(token.Token));
    }

    [Theory]
    [InlineData("nonexistent-token")]
    [InlineData("")]
    public void ValidateCsrfToken_InvalidToken_ReturnsFalse(string input)
    {
        var handler = new CsrfTokenHandler();
        Assert.False(handler.ValidateCsrfToken(input));
    }

    [Fact]
    public void ValidateCsrfToken_NullToken_ReturnsFalse()
    {
        var handler = new CsrfTokenHandler();
        Assert.False(handler.ValidateCsrfToken(null!));
    }

    [Fact]
    public void ValidateCsrfToken_ValidToken_ReturnsTrue()
    {
        var handler = new CsrfTokenHandler();
        var token = handler.GetNewCsrfToken();

        Assert.True(handler.ValidateCsrfToken(token.Token));
    }

    [Fact]
    public void GetNewCsrfToken_MultipleTokens_AllUnique()
    {
        var handler = new CsrfTokenHandler();
        var tokens = new HashSet<string>();

        for (int i = 0; i < 10; i++)
        {
            var token = handler.GetNewCsrfToken();
            Assert.DoesNotContain(token.Token, tokens);
            tokens.Add(token.Token);
        }
    }

    [Fact]
    public void RemoveExpiredCsrfTokens_RemovesNoTokens_WhenAllValid()
    {
        var handler = new CsrfTokenHandler();
        var token = handler.GetNewCsrfToken();

        handler.RemoveExpiredCsrfTokens();

        Assert.True(handler.ValidateCsrfToken(token.Token));
    }

    [Fact]
    public void ValidateCsrfToken_TokenValidatesMultipleTimes()
    {
        var handler = new CsrfTokenHandler();
        var token = handler.GetNewCsrfToken();

        Assert.True(handler.ValidateCsrfToken(token.Token));
        Assert.True(handler.ValidateCsrfToken(token.Token));
    }
}
