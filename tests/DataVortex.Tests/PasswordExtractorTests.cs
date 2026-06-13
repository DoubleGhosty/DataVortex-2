using DataVortex.Core.Extraction;
using Xunit;

namespace DataVortex.Tests;

public class PasswordExtractorTests
{
    [Theory]
    [InlineData("Password: @JohnDoeSupport", "@JohnDoeSupport")]
    [InlineData("🔑 Password: Abc123!", "Abc123!")]
    [InlineData("pass - secret99", "secret99")]
    [InlineData("mot de passe : Bonjour2024", "Bonjour2024")]
    [InlineData("pwd=xyz", "xyz")]
    [InlineData("**Password:** @canal", "@canal")]
    [InlineData("Le fichier est password-protected", null)]
    [InlineData("no password here", null)]
    [InlineData("Mdp : 1234abcd", "1234abcd")]
    [InlineData("Pass: Hello-World_2024", "Hello-World_2024")]
    [InlineData("Archive password is: test", "test")]
    [InlineData("Le mot de passe est : Soleil2024", "Soleil2024")]
    [InlineData("contraseña: clave123", "clave123")]
    [InlineData("PASSWORD - StrongP@ss", "StrongP@ss")]
    [InlineData("just a normal caption with no secret", null)]
    [InlineData("🔐 pwd → My_Pass.2024", "My_Pass.2024")]
    [InlineData("‼️ Password: https://t.me/harmonylogs", "https://t.me/harmonylogs")]
    [InlineData("Password: t.me/harmonylogs", "t.me/harmonylogs")]
    [InlineData("🔑 Password: @HARMONYLOGS", "@HARMONYLOGS")]
    [InlineData("🔑 Password: @BRZCLOUD", "@BRZCLOUD")]
    [InlineData("🔑 Password: t.me/IckisCloud", "t.me/IckisCloud")]
    [InlineData("🔑 Password: QLogs_Offical", "QLogs_Offical")]
    [InlineData("Buy private - @Omega_Cloud_Admin\n\n🔑 Password: @BRZCLOUD", "@BRZCLOUD")]
    [InlineData("🔑 Password: cacadu92382!!", "cacadu92382!!")]
    [InlineData("Password: P@ssw0rd!#$%", "P@ssw0rd!#$%")]
    public void Parses_passwords(string input, string? expected)
        => Assert.Equal(expected, PasswordExtractor.FromMessage(input));

    [Fact]
    public void Empty_or_null_returns_null()
    {
        Assert.Null(PasswordExtractor.FromMessage(null));
        Assert.Null(PasswordExtractor.FromMessage("   "));
    }
}
