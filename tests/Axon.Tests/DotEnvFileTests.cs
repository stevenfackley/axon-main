using Axon.Infrastructure.Configuration;

namespace Axon.Tests;

/// <summary>
/// Tests the .env parser: KEY=VALUE pairs, comments, blanks, quoting,
/// values containing '=', and the optional "export " prefix.
/// Pure string-in / dictionary-out — no file I/O.
/// </summary>
public class DotEnvFileTests
{
    [Fact]
    public void Parse_SimpleKeyValue()
    {
        var env = DotEnvFile.Parse("WHOOP_API_CLIENT_ID=abc123");
        Assert.Equal("abc123", env["WHOOP_API_CLIENT_ID"]);
    }

    [Fact]
    public void Parse_MultipleLines()
    {
        var env = DotEnvFile.Parse("A=1\nB=2\nC=3");
        Assert.Equal("1", env["A"]);
        Assert.Equal("2", env["B"]);
        Assert.Equal("3", env["C"]);
    }

    [Fact]
    public void Parse_IgnoresBlankLinesAndComments()
    {
        var env = DotEnvFile.Parse("# a comment\n\nA=1\n   # indented comment\nB=2");
        Assert.Equal(2, env.Count);
        Assert.Equal("1", env["A"]);
        Assert.Equal("2", env["B"]);
    }

    [Fact]
    public void Parse_TrimsWhitespaceAroundKeyAndValue()
    {
        var env = DotEnvFile.Parse("  KEY  =  value  ");
        Assert.True(env.ContainsKey("KEY"));
        Assert.Equal("value", env["KEY"]);
    }

    [Fact]
    public void Parse_StripsSurroundingDoubleQuotes()
    {
        var env = DotEnvFile.Parse("KEY=\"quoted value\"");
        Assert.Equal("quoted value", env["KEY"]);
    }

    [Fact]
    public void Parse_StripsSurroundingSingleQuotes()
    {
        var env = DotEnvFile.Parse("KEY='quoted value'");
        Assert.Equal("quoted value", env["KEY"]);
    }

    [Fact]
    public void Parse_ValueContainingEqualsSign_SplitsOnFirstOnly()
    {
        // OAuth secrets / base64 can contain '='.
        var env = DotEnvFile.Parse("SECRET=ab==cd=ef");
        Assert.Equal("ab==cd=ef", env["SECRET"]);
    }

    [Fact]
    public void Parse_StripsExportPrefix()
    {
        var env = DotEnvFile.Parse("export KEY=value");
        Assert.Equal("value", env["KEY"]);
    }

    [Fact]
    public void Parse_HandlesCarriageReturnsFromCrlfFiles()
    {
        var env = DotEnvFile.Parse("A=1\r\nB=2\r\n");
        Assert.Equal("1", env["A"]);
        Assert.Equal("2", env["B"]);
    }

    [Fact]
    public void Parse_LineWithoutEquals_IsIgnored()
    {
        var env = DotEnvFile.Parse("NOT_A_PAIR\nA=1");
        Assert.Single(env);
        Assert.Equal("1", env["A"]);
    }
}
