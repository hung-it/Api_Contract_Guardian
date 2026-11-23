using Xunit;

namespace Guardian.Core.Tests;

public sealed class GateExitCodeTests
{
    [Theory]
    [InlineData(nameof(GateExitCode.Success), 0)]
    [InlineData(nameof(GateExitCode.PolicyFailure), 2)]
    [InlineData(nameof(GateExitCode.InvalidSpec), 3)]
    [InlineData(nameof(GateExitCode.EngineFailure), 4)]
    [InlineData(nameof(GateExitCode.InvalidPolicy), 5)]
    public void PreservesExitCodeContract(string name, int expected)
    {
        var actual = Enum.Parse<GateExitCode>(name);

        Assert.Equal(expected, (int)actual);
    }
}
