using Diagnostics.Abstractions;

namespace Diagnostics.Logs.UnitTests.Abstractions;

public class TransactionRecordTests
{
    [Fact]
    public void CanBeConstructed_WithOnlyTheRequiredFields()
    {
        var id = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var startTime = DateTime.UtcNow;

        var record = new TransactionRecord
        {
            Id = id,
            CorrelationId = correlationId,
            Category = "UploadPdf",
            StartTime = startTime,
        };

        Assert.Equal(id, record.Id);
        Assert.Null(record.ParentId);
        Assert.Equal(correlationId, record.CorrelationId);
        Assert.Equal("UploadPdf", record.Category);
        Assert.Equal(startTime, record.StartTime);
        Assert.Null(record.DurationMs);
        Assert.Null(record.Message);
    }
}
