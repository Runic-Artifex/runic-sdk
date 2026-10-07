using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

// The handshake state transitions, driven directly so that both orderings of a token
// check and a deadline expiry are exercised without timers (#35).
public sealed class WebUiHandshakeStateTests
{
    [Fact]
    public void ATokenCheckBeforeTheDeadlineKeepsTheSocket()
    {
        var handshake = new WebUiHandshake();

        Assert.Equal(WebUiHandshake.Pending, handshake.CheckToken(tokenIsValid: true));
        Assert.False(handshake.TryExpire());
        Assert.Equal(WebUiHandshake.TokenChecked, handshake.State);
        Assert.False(handshake.IsClosing);
    }

    [Fact]
    public void AnExpiryBeforeTheTokenCheckClosesTheSocketAndTheCheckCannotRevertIt()
    {
        var handshake = new WebUiHandshake();

        Assert.True(handshake.TryExpire());
        Assert.Equal(WebUiHandshake.Expired, handshake.CheckToken(tokenIsValid: true));
        Assert.Equal(WebUiHandshake.Expired, handshake.State);
        Assert.True(handshake.IsClosing);
    }

    [Fact]
    public void AnEndedSocketCannotBeExpired()
    {
        var handshake = new WebUiHandshake();

        Assert.True(handshake.TryEnd());
        Assert.False(handshake.TryExpire());
        Assert.Equal(WebUiHandshake.Ended, handshake.State);
        Assert.Equal(WebUiHandshake.Ended, handshake.CheckToken(tokenIsValid: true));
    }

    [Fact]
    public void AnExpiredSocketCannotEnd()
    {
        var handshake = new WebUiHandshake();

        Assert.True(handshake.TryExpire());
        Assert.False(handshake.TryEnd());
        Assert.Equal(WebUiHandshake.Expired, handshake.State);
    }

    [Fact]
    public void AWrongTokenRejectsThePendingSocketPermanently()
    {
        var handshake = new WebUiHandshake();

        Assert.Equal(WebUiHandshake.Pending, handshake.CheckToken(tokenIsValid: false));
        Assert.Equal(WebUiHandshake.Rejected, handshake.State);
        Assert.True(handshake.IsClosing);
        Assert.Equal(WebUiHandshake.Rejected, handshake.CheckToken(tokenIsValid: true));
        Assert.False(handshake.TryExpire());
        Assert.Equal(WebUiHandshake.Rejected, handshake.State);
    }

    [Fact]
    public void AWrongTokenAfterAMatchRejectsAnAuthenticatedSocket()
    {
        var handshake = new WebUiHandshake();

        Assert.Equal(WebUiHandshake.Pending, handshake.CheckToken(tokenIsValid: true));
        Assert.Equal(WebUiHandshake.TokenChecked, handshake.CheckToken(tokenIsValid: true));
        Assert.Equal(WebUiHandshake.TokenChecked, handshake.CheckToken(tokenIsValid: false));
        Assert.Equal(WebUiHandshake.Rejected, handshake.State);
    }
}
