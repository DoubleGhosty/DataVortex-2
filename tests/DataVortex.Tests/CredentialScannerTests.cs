using System.IO;
using System.Text;
using DataVortex.Core.Extraction;
using Xunit;

namespace DataVortex.Tests;

/// <summary>Covers the ULP (url:login:password) parsing added to the credential scanner — the tricky bits are the
/// URL/password that can themselves contain ':' and '@' (android deep-links, symbol passwords), and the
/// passculture-only scope.</summary>
public sealed class CredentialScannerTests
{
    [Theory]
    [InlineData("https://passculture.app/connexion:antoine.croci92@gmail.com:%%A349quake%%", "antoine.croci92@gmail.com", "%%A349quake%%")]
    [InlineData("https://app.passculture.beta.gouv.fr/connexion:anthony.felez@gmail.com:Misty25150??", "anthony.felez@gmail.com", "Misty25150??")]
    [InlineData("https://app.passculture.webapp/:jennifertisserand@yahoo.fr:Loanoago3103!", "jennifertisserand@yahoo.fr", "Loanoago3103!")]
    [InlineData("https://passculture.pro/connexion:sachacome12@gmail.com:Sacha261145.", "sachacome12@gmail.com", "Sacha261145.")]
    // android deep-link: the URL carries '@' and '==' — must still pick the real email + password
    [InlineData("android://SMp2IXhDKEUU8rJYZqsrSe-KnlqR_4aIibm5XUmv_-UkpLYo8WKIY4rGbklVktgfyUy2Pa0Ki7AglsblHvM7fw==@app.passculture.webapp/:becili.ayoub@gmail.com:Colombes_92700", "becili.ayoub@gmail.com", "Colombes_92700")]
    // password itself contains '@'
    [InlineData("android://x==@app.passculture.webapp/:edeplinval@gmail.com:Lokithor@2006", "edeplinval@gmail.com", "Lokithor@2006")]
    public void ParseUlp_extracts_passculture_credentials(string line, string email, string pass)
    {
        var c = CredentialScanner.ParseUlp(line);
        Assert.NotNull(c);
        Assert.Equal(email, c!.Username);
        Assert.Equal(pass, c.Password);
    }

    [Theory]
    [InlineData("https://netflix.com/login:user@gmail.com:pass")]  // non-passculture URL → out of scope
    [InlineData("https://passculture.app/connexion")]               // no login/password
    [InlineData("just some random text mentioning passculture")]    // not ULP
    [InlineData("https://other.com:bob@passculture.app:pw")]        // 'passculture' only in the email domain, not the URL
    [InlineData("")]
    public void ParseUlp_rejects_non_passculture_or_malformed(string line)
        => Assert.Null(CredentialScanner.ParseUlp(line));

    [Fact]
    public void ScanStream_picks_up_only_passculture_ulp_lines()
    {
        var text = string.Join("\n",
            "https://passculture.app/connexion:a@gmail.com:pw1",
            "https://netflix.com:b@gmail.com:pw2",
            "android://tok==@app.passculture.webapp/:c@gmail.com:pw3");
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(text));

        var found = CredentialScanner.ScanStream(ms);

        Assert.Equal(2, found.Count); // the two passculture lines, not netflix
        Assert.Contains(found, c => c.Username == "a@gmail.com" && c.Password == "pw1");
        Assert.Contains(found, c => c.Username == "c@gmail.com" && c.Password == "pw3");
    }
}
