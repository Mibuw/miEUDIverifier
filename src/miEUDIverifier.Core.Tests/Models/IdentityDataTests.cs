using miEUDIverifier.Models;
using FluentAssertions;
using Xunit;

namespace miEUDIverifier.Tests.Models;

/// <summary>
/// Tests for <see cref="IdentityData"/>.
/// IdentityData is the result object after a successful wallet scan.
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
    [InlineData(null,           "Wolfgang",  "1976-09-24")]  // No family name
    [InlineData("Mitterbucher", null,         "1976-09-24")]  // No given name
    [InlineData("Mitterbucher", "Wolfgang",   null)]          // No birth date
    [InlineData(null,           null,         null)]          // Everything missing
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
