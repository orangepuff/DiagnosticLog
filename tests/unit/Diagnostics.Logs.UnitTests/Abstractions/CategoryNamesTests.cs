using Diagnostics.Abstractions;

namespace Diagnostics.Logs.UnitTests.Abstractions;

public class CategoryNamesTests
{
    [Fact]
    public void None_IsTheReservedGuardrailValue()
    {
        Assert.Equal("(none)", CategoryNames.None);
    }
}
