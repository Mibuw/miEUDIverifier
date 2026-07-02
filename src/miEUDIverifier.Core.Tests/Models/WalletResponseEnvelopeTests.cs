using miEUDIverifier.Models;
using FluentAssertions;
using System.Text.Json;
using Xunit;

namespace miEUDIverifier.Tests.Models;

/// <summary>
/// Tests für die Status-Erkennung im <see cref="WalletResponseEnvelope"/>.
/// Das Envelope wird beim Polling zurückgegeben und zeigt an ob die Wallet
/// bereits geantwortet hat, noch wartet, oder ein Fehler aufgetreten ist.
/// </summary>
public class WalletResponseEnvelopeTests
{
    [Theory]
    [InlineData("submitted")]  // Standard-Status des EUDI-Backends
    [InlineData("SUBMITTED")]  // Case-Insensitive
    [InlineData("complete")]   // Alternative Backend-Implementierungen
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
        // vp_token als JSON-Objekt: { "credential-id": ["base64-data"] }
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
