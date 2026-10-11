using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Composition;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Shell.Win32.Startup;

namespace PhotoReview.Shell.Win32.Viewing;

/// <summary>WP-21: đăng ký của gói trình bày ảnh vào <c>ShellCompositionRoot.RegisterFeatures</c> (một dòng).</summary>
internal static class ShellViewingRegistration
{
    /// <summary>
    /// C-16: <see cref="IPresentationSinkFactory"/> của shell = <see cref="Win32PresentationSinkFactory"/> (đăng ký sau thắng
    /// đăng ký trước). Cửa sổ nối <c>PresentationComposer</c>/prefetch/cache GPU vào cùng <c>ShellPresentationHub</c> ở
    /// <c>ShellMainWindow.Viewing.cs</c>.
    /// </summary>
    internal static IServiceCollection AddShellViewing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IPresentationSinkFactory>(sp => new Win32PresentationSinkFactory(
            sp.GetRequiredService<ShellPresentationHub>(), sp.GetService<IUiDispatcher>(), sp.GetService<ReviewMetrics>()));
        return services;
    }
}
