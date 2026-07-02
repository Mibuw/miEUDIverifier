using miEUDIverifier.Models;
using FluentAssertions;
using Xunit;

namespace miEUDIverifier.Tests.Models;

/// <summary>
/// Tests für <see cref="IdentityData"/>.
/// IdentityData ist das Ergebnis-Objekt nach einem erfolgreichen Wallet-Scan.
/// </summary>
public class IdentityDataTests
{
    [Fact]
    public void IsComplete_ReturnsTrue_WhenAllThreeFieldsPresent()
    {
        var identity = new IdentityData
        {
            FamilyName = "Mitterbucher",
            GivenName  = "Wolfgang",
            BirthDate  = "1976-09-24",
        };

        identity.IsComplete.Should().BeTrue();
    }

    [Theory]
    [InlineData(null,           "Wolfgang",  "1976-09-24")]  // Kein Familienname
    [InlineData("Mitterbucher", null,         "1976-09-24")]  // Kein Vorname
    [InlineData("Mitterbucher", "Wolfgang",   null)]          // Kein Geburtsdatum
    [InlineData(null,           null,         null)]          // Alles fehlt
    public void IsComplete_ReturnsFalse_WhenAnyFieldMissing(
        string? family, string? given, string? birth)
    {
        var identity = new IdentityData
        {
            FamilyName = family,
            GivenName  = given,
            BirthDate  = birth,
        };

        identity.IsComplete.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsComplete_ReturnsFalse_WhenFieldIsWhitespace(string whitespace)
    {
        var identity = new IdentityData
        {
            FamilyName = whitespace,
            GivenName  = "Wolfgang",
            BirthDate  = "1976-09-24",
        };

        identity.IsComplete.Should().BeFalse(
            because: "Leerzeichen-Felder gelten als nicht vorhanden");
    }

    [Fact]
    public void AdditionalClaims_IsEmptyByDefault()
    {
        var identity = new IdentityData();
        identity.AdditionalClaims.Should().BeEmpty();
        identity.AdditionalClaims.Should().NotBeNull();
    }
}
