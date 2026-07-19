using Diagnostics.NLog.Configuration;

namespace Diagnostics.Logs.UnitTests.NLog;

public class NLogRuleXmlParserTests
{
    [Fact]
    public void ParseRules_SingleWildcardRule_ReturnsIt()
    {
        const string xml = """
            <nlog xmlns="http://www.nlog-project.org/schemas/NLog.xsd">
              <rules>
                <logger name="*" minlevel="Info" writeTo="DiagnosticsLogsAsync" />
              </rules>
            </nlog>
            """;

        var rules = NLogRuleXmlParser.ParseRules(xml);

        var rule = Assert.Single(rules);
        Assert.Equal("*", rule.LoggerNamePattern);
        Assert.Equal("Info", rule.MinLevel);
    }

    [Fact]
    public void ParseRules_MultipleRules_ReturnsAllInDocumentOrder()
    {
        const string xml = """
            <nlog xmlns="http://www.nlog-project.org/schemas/NLog.xsd">
              <rules>
                <logger name="OCRWeb.*" minlevel="Trace" writeTo="DiagnosticsLogsAsync" />
                <logger name="Microsoft.AspNetCore.*" minlevel="Warn" writeTo="DiagnosticsLogsAsync" />
              </rules>
            </nlog>
            """;

        var rules = NLogRuleXmlParser.ParseRules(xml);

        Assert.Equal(2, rules.Count);
        Assert.Equal(("OCRWeb.*", "Trace"), (rules[0].LoggerNamePattern, rules[0].MinLevel));
        Assert.Equal(("Microsoft.AspNetCore.*", "Warn"), (rules[1].LoggerNamePattern, rules[1].MinLevel));
    }

    [Fact]
    public void ParseRules_MissingAttributes_FallsBackToWildcardAndInfo()
    {
        const string xml = """
            <nlog xmlns="http://www.nlog-project.org/schemas/NLog.xsd">
              <rules>
                <logger />
              </rules>
            </nlog>
            """;

        var rules = NLogRuleXmlParser.ParseRules(xml);

        var rule = Assert.Single(rules);
        Assert.Equal("*", rule.LoggerNamePattern);
        Assert.Equal("Info", rule.MinLevel);
    }

    [Fact]
    public void ParseRules_NoRulesElement_ReturnsEmpty()
    {
        const string xml = """<nlog xmlns="http://www.nlog-project.org/schemas/NLog.xsd"></nlog>""";

        var rules = NLogRuleXmlParser.ParseRules(xml);

        Assert.Empty(rules);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseRules_EmptyOrWhitespaceInput_ReturnsEmptyWithoutThrowing(string xml)
    {
        var rules = NLogRuleXmlParser.ParseRules(xml);

        Assert.Empty(rules);
    }

    [Fact]
    public void ParseRules_NoNamespace_StillParses()
    {
        // Not every hand-edited row in Configurations will necessarily declare the NLog xmlns.
        const string xml = "<nlog><rules><logger name=\"*\" minlevel=\"Error\" /></rules></nlog>";

        var rules = NLogRuleXmlParser.ParseRules(xml);

        var rule = Assert.Single(rules);
        Assert.Equal("Error", rule.MinLevel);
    }
}
