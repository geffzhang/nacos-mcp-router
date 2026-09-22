namespace NacosMcpRouter.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void MainProjectAssemblyLoads()
    {
        var asm = typeof(NacosMcpRouter.Program).Assembly;
        Assert.Equal("NacosMcpRouter", asm.GetName().Name);
    }
}
