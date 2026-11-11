using System.Text.Json;
using FluentAssertions;
using miEUDIverifier.Models;
using miEUDIverifier.Services.Tessio;
using miEUDIverifier.Tests.Helpers;
using Xunit;

namespace miEUDIverifier.Tests.Services;

/// <summary>
/// Tests for the reading of a verified presentation on the in-process path.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam worth pinning. The library reports an mdoc's claims as namespace to element to
/// value, which is the same shape the reference backend puts in a credential's <c>attributes</c>,
/// and the whole reason the reading of them is shared between the two paths. If the library ever
/// reports them differently, these tests fail here rather than in front of a wallet.
/// </para>
/// <para>
/// The shape used below is not invented: it is the shape a real German PID presentation produced,
/// whose disclosed claims came back under the single key <c>eu.europa.ec.eudi.pid.1</c>.
/// </para>
/// </remarks>
public class TessioVerifierEngineTests
{
    private const string PidDocType = "eu.europa.ec.eudi.pid.1";

    /// <summary>
    /// The engine, built for reading a response only.
    /// </summary>
    /// <remarks>
    /// The session store, the key store, the request object store and the request builder take no
    /// part in reading a response, so they are not stood up. Passing them as null is deliberate and
    /// safe here: should the reading ever come to need one, these tests fail loudly with a
    /// NullReferenceException rather than passing against a half-built object.
    /// </remarks>
    private static TessioVerifierEngine ReadingEngine(
        out CapturingLogger<TessioVerifierEngine> logger, string docType = PidDocType)
    {
        logger = new CapturingLogger<TessioVerifierEngine>();
        return new TessioVerifierEngine(
            sessions: null!,
            responseKeys: null!,
            requestObjects: null!,
            requestBuilder: null!,
            options: new TessioEngineOptions
            {
                DocType = docType,
                MdocNamespace = docType,
                RequestedClaims = ["family_name"],
                ResponseUri = new Uri("https://verifier.example/verify/callback"),
            },
            clock: TimeProvider.System,
            logger: logger);
    }

    private static WalletResponseEnvelope Presentation(string claimsJson, string docType = PidDocType) =>
        new()
        {
            TransactionId = "session-1",
            Status = "submitted",
            Credentials =
            [
                new VerifiedCredential
                {
                    CredentialId = "credential",
                    Format = "mso_mdoc",
                    DocType = docType,
                    Attributes = JsonDocument.Parse(claimsJson).RootElement.Clone(),
                },
            ],
        };

    /// <summary>
    /// A German PID as it actually arrives: the claims nested under the namespace, a structured
    /// place of birth of which only the locality is populated, and nationality as a list.
    /// </summary>
    [Fact]
    public async Task ExtractIdentityDataAsync_ReadsAGermanPidPresentation()
    {
        var engine = ReadingEngine(out _);
        var envelope = Presentation("""
            {
              "eu.europa.ec.eudi.pid.1": {
                "family_name": "Mustermann",
                "given_name": "Erika",
                "birth_date": "1964-08-12",
                "place_of_birth": { "locality": "Berlin" },
                "nationality": ["DE"],
                "issuing_authority": "DEZXZ",
                "issuing_country": "DE"
              }
            }
            """);

        var identity = await engine.ExtractIdentityDataAsync(envelope, null);

        identity.FamilyName.Should().Be("Mustermann");
        identity.GivenName.Should().Be("Erika");
        identity.BirthDate.Should().Be("1964-08-12");
        identity.PlaceOfBirth.Should().Be("Berlin");
        identity.Nationality.Should().Be("DE");
        identity.IssuingAuthority.Should().Be("DEZXZ");
        identity.IssuingCountry.Should().Be("DE");
        identity.CredentialFormat.Should().Be("mso_mdoc");

        // Set only once the type matched, so a populated value means the data came from a
        // credential of the type that was asked for.
        identity.CredentialType.Should().Be(PidDocType);
        identity.IsComplete.Should().BeTrue();
    }

    /// <summary>Several nationalities are joined rather than silently reduced to the first.</summary>
    [Fact]
    public async Task ExtractIdentityDataAsync_JoinsSeveralNationalities()
    {
        var engine = ReadingEngine(out _);
        var envelope = Presentation("""
            {
              "eu.europa.ec.eudi.pid.1": {
                "family_name": "Mustermann",
                "given_name": "Erika",
                "birth_date": "1964-08-12",
                "nationality": ["DE", "AT"]
              }
            }
            """);

        var identity = await engine.ExtractIdentityDataAsync(envelope, null);

        identity.Nationality.Should().Be("DE, AT");
    }

    /// <summary>
    /// A credential of another type contributes nothing, and says so. The library was told which
    /// doctype to expect, so this can only happen if the engine and the request were configured
    /// apart -- which is exactly why it is checked again here instead of trusted.
    /// </summary>
    [Fact]
    public async Task ExtractIdentityDataAsync_TakesNothingFromAnotherCredentialType()
    {
        var engine = ReadingEngine(out var logger);
        var envelope = Presentation("""
            { "org.iso.18013.5.1.mDL": { "family_name": "Mustermann", "given_name": "Erika" } }
            """, docType: "org.iso.18013.5.1.mDL");

        var identity = await engine.ExtractIdentityDataAsync(envelope, null);

        identity.FamilyName.Should().BeNull();

        // Null, not empty: the caller tells "no credential of the requested type" from "the data was
        // incomplete" by exactly this.
        identity.CredentialType.Should().BeNull();
        // Reported as a warning, not swallowed: a mismatch here means a misconfiguration.
        logger.MessagesAt(Microsoft.Extensions.Logging.LogLevel.Warning)
            .Should().Contain(m => m.Contains("org.iso.18013.5.1.mDL"));
    }

    /// <summary>A response carrying no credential yields nothing, and does not throw.</summary>
    [Fact]
    public async Task ExtractIdentityDataAsync_HandlesAResponseWithoutACredential()
    {
        var engine = ReadingEngine(out _);

        var identity = await engine.ExtractIdentityDataAsync(
            new WalletResponseEnvelope { TransactionId = "session-1", Status = "failed" }, null);

        identity.CredentialType.Should().BeNull();
        identity.FamilyName.Should().BeNull();
    }
}
