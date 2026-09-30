using ForgeDock.Application;
namespace ForgeDock.Tests;
public class EnvironmentFileTests
{
    [Fact]
    public void ImportsCommentsQuotesExportsAndLiteralValues()
    {
        var values = EnvironmentFile.Parse("# comment\r\nexport PORT = 8080\nEMPTY=\nPASSWORD='space # = secret'\nURL=https://example.com/#anchor\nNAME=hello # comment\nRAW=${PORT}\nQUOTED=\"hello \\\"world\\\"\" # comment");
        Assert.Equal("8080", values["PORT"]); Assert.Equal("", values["EMPTY"]);
        Assert.Equal("space # = secret", values["PASSWORD"]); Assert.Equal("https://example.com/#anchor", values["URL"]);
        Assert.Equal("hello", values["NAME"]); Assert.Equal("${PORT}", values["RAW"]); Assert.Equal("hello \"world\"", values["QUOTED"]);
    }
    [Theory]
    [InlineData("BAD-NAME=super-secret")]
    [InlineData("NAME='super-secret")]
    [InlineData("NAME='super-secret' trailing")]
    [InlineData("NAME=super-secret\nNAME=second")]
    [InlineData("NAME=super-secret\0")]
    public void ErrorsNeverEchoValues(string content)
    {
        var error = Assert.Throws<FormatException>(() => EnvironmentFile.Parse(content));
        Assert.DoesNotContain("super-secret", error.Message); Assert.Contains("Line", error.Message);
    }
    [Fact]
    public void RejectsEmptyAndExcessiveInput()
    {
        Assert.Throws<FormatException>(() => EnvironmentFile.Parse("# nothing"));
        Assert.Throws<FormatException>(() => EnvironmentFile.Parse("A=" + new string('x', 16385)));
        Assert.Throws<FormatException>(() => EnvironmentFile.Parse(string.Join('\n', Enumerable.Range(0, 101).Select(i => $"KEY{i}=value"))));
    }
}
