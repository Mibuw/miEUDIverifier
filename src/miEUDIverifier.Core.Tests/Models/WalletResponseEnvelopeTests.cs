using miEUDIverifier.Models;
using FluentAssertions;
using System.Text.Json;
using Xunit;

namespace miEUDIverifier.Tests.Models;

/// <summary>
/// Tests for the status detection in <see cref="WalletResponseEnvelope"/>.
/// The envelope is returned while polling and indicates whether the wallet
/// has already answered, is still pending, or an error occurred.
/// </summary>
public class WalletResponseEnvelopeTests
{
    [Theory]
    [InlineData("submitted")]  // Default status of the EUDI backend
    [InlineData("SUBMITTED")]  // Case-insensitive
    [InlineData("complete")]   // Alternative backend implementations
    [InlineData("Complete")]
    public void IsSubmitted_ReturnsTrue_ForCompletedStatuses(string status)
    {
        var envelope = new WalletResponseEnvelope { Status = status };
        envelope.IsSubmitted.Should().BeTrue();
    }

    [Theory]
    [InlineData("requested")]
    [InlineData("request_object_retrieved")]
    [InlineData("pending")]
    [InlineData(null)]
    public void IsSubmitted_ReturnsFalse_ForPendingOrNullStatus(string? status)
    {
        var envelope = new WalletResponseEnvelope { Status = status };
        envelope.IsSubmitted.Should().BeFalse();
    }

    [Theory]
    [InlineData("timed_out")]
    [InlineData("TIMED_OUT")]
    [InlineData("timedout")]
    public void IsTimedOut_ReturnsTrue_ForTimeoutStatuses(string status)
    {
        var envelope = new WalletResponseEnvelope { Status = status };
        envelope.IsTimedOut.Should().BeTrue();
    }

    [Fact]
    public void HasVpToken_ReturnsTrue_WhenVpTokenIsJsonObject()
    {
        // vp_token as a JSON object: { "credential-id": ["base64-data"] }
        using var doc = JsonDocument.Parse("""{"cred-id": ["dGVzdA=="]}""");
        var envelope = new WalletResponseEnvelope { VpToken = doc.RootElement };

        envelope.HasVpToken.Should().BeTrue(
            because: "ein vorhandenes vp_token-Objekt signalisiert eine Wallet-Antwort");
    }

    [Fact]
    public void HasVpToken_ReturnsFalse_WhenVpTokenIsNull()
    {
        var envelope = new WalletResponseEnvelope { VpToken = null };
        envelope.HasVpToken.Should().BeFalse();
    }

    [Fact]
    public void HasError_ReturnsTrue_WhenErrorFieldIsSet()
    {
        var envelope = new WalletResponseEnvelope { Error = "access_denied" };
        envelope.HasError.Should().BeTrue();
    }

    [Fact]
    public void HasError_ReturnsFalse_WhenNoError()
    {
        var envelope = new WalletResponseEnvelope { Status = "requested" };
        envelope.HasError.Should().BeFalse();
    }
}
