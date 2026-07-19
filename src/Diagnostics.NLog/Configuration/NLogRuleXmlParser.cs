using System.Xml.Linq;

namespace Diagnostics.NLog.Configuration;

/// <summary>
/// One <c>&lt;logger&gt;</c> rule parsed out of the DB-stored NLog XML.
/// </summary>
public sealed record ParsedRule(string LoggerNamePattern, string MinLevel);

/// <summary>
/// Parses only the <c>&lt;rules&gt;</c> section of the NLog XML stored in <c>[dbo].[Configurations].xValue</c>.
/// Targets are NOT read from this XML — infrastructure wiring (connection string, the two custom targets, environment identity) is set in code via <c>AddDiagnostics</c>, per the design doc §5 split of concerns.
/// This keeps levels/rules/filters tunable centrally by ops without redeploy, while our targets can take constructor-injected dependencies instead of relying on NLog's XML-driven property binding.
/// </summary>
public static class NLogRuleXmlParser
{
    public static IReadOnlyList<ParsedRule> ParseRules(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return [];
        }
        
        var doc = XDocument.Parse(xml);
        var root = doc.Root;
        if (root is null)
        {
            return [];
        }

        var ns = root.GetDefaultNamespace();

        return root
            .Descendants(ns + "rules")
            .Elements(ns + "logger")
            .Select(e => new ParsedRule(
                LoggerNamePattern: (string?)e.Attribute("name") ?? "*",
                MinLevel: (string?)e.Attribute("minlevel") ?? "Info"))
            .ToList();
    }
}
