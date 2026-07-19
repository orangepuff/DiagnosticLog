namespace Diagnostics.NLog.Configuration;

/// <summary>
/// Dapper projection of one row of <c>[dbo].[Configurations]</c>.
/// </summary>
public sealed class ConfigurationRow
{
    public int Id { get; set; }
    public string LoggerName { get; set; } = string.Empty;
    public int? EnvironmentId { get; set; }
    public string XmlValue { get; set; } = string.Empty;
    public DateTime? UpdatedTime { get; set; }
}
