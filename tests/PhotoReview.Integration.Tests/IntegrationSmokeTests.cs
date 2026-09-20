namespace PhotoReview.Integration.Tests;

[Trait("Category", "Slow")]
public sealed class IntegrationSmokeTests
{
    [Fact(DisplayName = "Integration test project initializes properly")]
    public void ProjectInitializes()
    {
        Assert.True(true);
    }
}
