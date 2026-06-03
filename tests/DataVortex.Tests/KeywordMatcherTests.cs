using DataVortex.Core.Extraction;
using Xunit;

namespace DataVortex.Tests;

public class KeywordMatcherTests
{
    [Theory]
    [InlineData("user_passwords.txt", true)]
    [InlineData("DB_PASSWORD_DUMP.txt", true)]
    [InlineData("Password.txt", true)]
    [InlineData("emails_only.txt", false)]
    [InlineData("", false)]
    public void Matches_filename_case_insensitively(string name, bool expected)
    {
        var matcher = new KeywordMatcher(enabled: true, keywords: new[] { "password" });
        Assert.Equal(expected, matcher.MatchesText(name));
    }

    [Fact]
    public void Disabled_when_no_keywords()
        => Assert.False(new KeywordMatcher(true, System.Array.Empty<string>()).Enabled);

    [Fact]
    public void Disabled_when_flag_off()
        => Assert.False(new KeywordMatcher(false, new[] { "password" }).Enabled);

    [Fact]
    public void Multiple_keywords_are_supported()
    {
        var matcher = new KeywordMatcher(true, new[] { "password", "mdp" });
        Assert.True(matcher.MatchesText("liste_mdp.txt"));
        Assert.True(matcher.MatchesText("PASSWORDS.txt"));
        Assert.False(matcher.MatchesText("random.txt"));
    }
}
