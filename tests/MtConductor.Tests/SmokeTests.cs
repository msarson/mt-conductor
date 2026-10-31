namespace MtConductor.Tests;

public class SmokeTests
{
    [Fact]
    public void MainForm_type_is_available()
    {
        Assert.NotNull(typeof(MainForm));
    }
}
