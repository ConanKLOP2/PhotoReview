using Microsoft.Extensions.DependencyInjection;

namespace PhotoReview.App.Composition;

/// <summary>
/// AR02a: single composition root usable by production (<see cref="App.App_Startup"/>),
/// the benchmark CLI, and (from AR02b) integration tests. Builds the production service
/// graph via <see cref="App.ConfigureServices"/> and lets a caller layer additional/overriding
/// registrations on top before the container is built.
/// </summary>
/// <remarks>
/// Public (not internal): <c>Benchmark.Cli</c> has no <c>InternalsVisibleTo</c> for
/// <c>PhotoReview.App</c>, and Q-ST3 already accepted the Cli → App dependency direction.
/// </remarks>
public static class AppHost
{
    public static ServiceProvider BuildServices(Action<IServiceCollection>? overrides = null)
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        overrides?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
