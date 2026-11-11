using FluentAssertions;
using miEUDIverifier.Services.Tessio;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace miEUDIverifier.Tests.Services;

/// <summary>
/// Tests for the options a host supplies to the in-process verifier.
/// </summary>
/// <remarks>
/// The binding tests are not ceremony. Configuration is how a real deployment sets these, and
/// .NET's binder ADDS to a collection property that already holds items rather than replacing it.
/// A pre-filled default of seven claims plus three in appsettings.json therefore asked for ten —
/// and asking for more than the registration certificate grants makes the German wallet drop the
/// request with no consent screen, which looks like a certificate fault. The regression is cheap to
/// pin and expensive to find in the field.
/// </remarks>
public class InProcessVerifierOptionsTests
{
    private static IConfigurationRoot Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e =>
                new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void RequestedClaims_BindsToExactlyWhatWasConfigured()
    {
        var options = Config(
                ("Pid:PublicBaseUrl", "https://verifier.example.com"),
                ("Pid:RequestedClaims:0", "family_name"),
                ("Pid:RequestedClaims:1", "given_name"),
                ("Pid:RequestedClaims:2", "birth_date"))
            .GetSection("Pid").Get<InProcessVerifierOptions>();

        options.Should().NotBeNull();

        // Three. Not three plus the seven defaults.
        options!.RequestedClaims.Should().Equal("family_name", "given_name", "birth_date");
    }

    [Fact]
    public void RequestedClaims_IsNullWhenNotConfigured_SoTheDefaultApplies()
    {
        var options = Config(("Pid:PublicBaseUrl", "https://verifier.example.com"))
            .GetSection("Pid").Get<InProcessVerifierOptions>();

        // Null, not empty: the registration resolves it to DefaultRequestedClaims. An empty list
        // would mean "ask for nothing", which Validate refuses.
        options!.RequestedClaims.Should().BeNull();
        InProcessVerifierOptions.DefaultRequestedClaims.Should().HaveCount(7)
            .And.Contain("family_name").And.Contain("issuing_country");
    }

    [Fact]
    public void Validate_RefusesAnEmptyClaimList_ButAcceptsNull()
    {
        var options = new InProcessVerifierOptions
        {
            PublicBaseUrl = "https://verifier.example.com",
            AccessCertificatePath = Path.GetTempFileName(),
            IssuerTrustAnchorDirectory = Path.GetTempPath(),
            RegistrationCertificate = "a.b.c",
        };

        try
        {
            options.RequestedClaims = null;
            options.Validate();   // the default applies, so this is fine

            options.RequestedClaims = [];
            var empty = () => options.Validate();
            empty.Should().Throw<InvalidOperationException>()
                .WithMessage("*asked for nothing*");
        }
        finally
        {
            File.Delete(options.AccessCertificatePath);
        }
    }

    [Fact]
    public void Validate_RefusesALoopbackPublicBaseUrl()
    {
        // The wallet fetches the request object over the internet, so localhost cannot work. It is
        // worth failing at startup rather than letting a scan time out with nothing to show why.
        var options = new InProcessVerifierOptions
        {
            PublicBaseUrl = "http://localhost:5000",
            AccessCertificatePath = "x",
            IssuerTrustAnchorDirectory = "x",
            RegistrationCertificate = "a.b.c",
        };

        var validate = () => options.Validate();
        validate.Should().Throw<InvalidOperationException>().WithMessage("*loopback*");
    }

    [Fact]
    public void Validate_RefusesAMissingRegistrationCertificate()
    {
        var options = new InProcessVerifierOptions
        {
            PublicBaseUrl = "https://verifier.example.com",
            AccessCertificatePath = "x",
            IssuerTrustAnchorDirectory = "x",
        };

        var validate = () => options.Validate();
        validate.Should().Throw<InvalidOperationException>()
            .WithMessage("*RegistrationCertificate*");
    }
}
