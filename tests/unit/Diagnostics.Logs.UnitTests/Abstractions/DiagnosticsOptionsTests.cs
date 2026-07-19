using Diagnostics.Abstractions;

namespace Diagnostics.Logs.UnitTests.Abstractions;

public class DiagnosticsOptionsTests
{
    [Fact]
    public void Defaults_MatchTheBalancedProfileFromTheDesignDoc()
    {
        var options = new DiagnosticsOptions();

        // design doc §9.6 — "balanced profile: flush 500 rows / 2s, bounded queue ~10k".
        Assert.Equal(500, options.FlushBatchSize);
        Assert.Equal(TimeSpan.FromSeconds(2), options.FlushInterval);
        Assert.Equal(10_000, options.MaxQueueSize);
        Assert.Equal(TimeSpan.FromSeconds(30), options.ConfigPollInterval);
        Assert.Equal("DEV", options.EnvironmentName);
        Assert.Equal(32 * 1024, options.MaxBodyCaptureSizeBytes);
    }

    [Fact]
    public void Redact_DefaultsToIdentity()
    {
        var options = new DiagnosticsOptions();

        Assert.Equal("some body", options.Redact("some body"));
    }
}
