using AirDrop.Core.Identity;
using Xunit;

namespace AirDrop.Core.Tests.Identity;

public class ContactHashTests
{
    [Theory]
    [InlineData("Usuario@Ejemplo.com", "usuario@ejemplo.com")]
    [InlineData("  usuario@ejemplo.com  ", "usuario@ejemplo.com")]
    [InlineData("USUARIO@EJEMPLO.COM", "usuario@ejemplo.com")]
    public void Normalize_LowercasesEmailAddresses(string input, string expected)
    {
        Assert.Equal(expected, ContactHash.Normalize(input));
    }

    [Theory]
    [InlineData("+34 600 123 456", "34600123456")]
    [InlineData("+34-600-123-456", "34600123456")]
    [InlineData("(600) 123 456", "600123456")]
    [InlineData("600.123.456", "600123456")]
    public void Normalize_StripsPhoneNumberFormatting(string input, string expected)
    {
        // Sin esto, el mismo número escrito de dos formas produciría hashes distintos
        // y el emparejamiento por contacto fallaría.
        Assert.Equal(expected, ContactHash.Normalize(input));
    }

    [Fact]
    public void Normalize_ProducesIdenticalHashesForEquivalentPhoneFormats()
    {
        var spaced = ContactHash.Compute("+34 600 123 456");
        var dashed = ContactHash.Compute("+34-600-123-456");

        Assert.Equal(spaced, dashed);
    }

    [Fact]
    public void Compute_ProducesFullSha256()
    {
        Assert.Equal(32, ContactHash.Compute("usuario@ejemplo.com").Length);
    }

    [Fact]
    public void ComputeTruncated_DefaultsToTheTwoBytesUsedInBleAdvertisements()
    {
        var truncated = ContactHash.ComputeTruncated("usuario@ejemplo.com");

        Assert.Equal(ContactHash.BleTruncatedLength, truncated.Length);
        Assert.Equal(2, truncated.Length);
    }

    [Fact]
    public void ComputeTruncated_TakesThePrefixOfTheFullHash()
    {
        var full = ContactHash.Compute("usuario@ejemplo.com");
        var truncated = ContactHash.ComputeTruncated("usuario@ejemplo.com");

        Assert.Equal(full[..2], truncated);
    }

    [Fact]
    public void ComputeTruncated_RejectsLengthsOutsideTheHashSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ContactHash.ComputeTruncated("a@b.com", 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ContactHash.ComputeTruncated("a@b.com", 33));
    }

    [Fact]
    public void Compute_ProducesDifferentHashesForDifferentIdentifiers()
    {
        Assert.NotEqual(
            ContactHash.Compute("usuario@ejemplo.com"),
            ContactHash.Compute("otro@ejemplo.com"));
    }

    [Fact]
    public void Matches_ComparesHashesOfEqualLength()
    {
        var hash = ContactHash.Compute("usuario@ejemplo.com");
        var same = ContactHash.Compute("USUARIO@EJEMPLO.COM");

        Assert.True(ContactHash.Matches(hash, same));
        Assert.False(ContactHash.Matches(hash, ContactHash.Compute("otro@ejemplo.com")));
    }

    [Fact]
    public void Matches_RejectsHashesOfDifferentLength()
    {
        var full = ContactHash.Compute("usuario@ejemplo.com");
        var truncated = ContactHash.ComputeTruncated("usuario@ejemplo.com");

        Assert.False(ContactHash.Matches(full, truncated));
    }

    [Fact]
    public void ToHex_ProducesLowercaseHexForLogging()
    {
        Assert.Equal("00ff42", ContactHash.ToHex([0x00, 0xFF, 0x42]));
    }

    [Fact]
    public void Compute_RejectsEmptyIdentifiers()
    {
        Assert.Throws<ArgumentException>(() => ContactHash.Compute(""));
        Assert.Throws<ArgumentNullException>(() => ContactHash.Compute(null!));
    }
}
