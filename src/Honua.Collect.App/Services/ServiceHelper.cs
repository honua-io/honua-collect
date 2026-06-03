using Microsoft.Extensions.DependencyInjection;

namespace Honua.Collect.App.Services;

/// <summary>
/// Resolves services from the app's dependency-injection container. Shell
/// instantiates tab pages from <c>DataTemplate</c>s (parameterless), so pages
/// obtain their collaborators here instead of constructing transports, stores,
/// and credentials inline — giving one configured HTTP client, one record book,
/// and one auth session across the app.
/// </summary>
public static class ServiceHelper
{
    private static IServiceProvider? _services;

    /// <summary>Binds the container; called once at startup from <c>App</c>.</summary>
    /// <param name="services">The built service provider.</param>
    public static void Initialize(IServiceProvider services) => _services = services;

    /// <summary>Resolves a required service.</summary>
    /// <typeparam name="T">The service type.</typeparam>
    /// <returns>The resolved instance.</returns>
    public static T Get<T>() where T : notnull
        => (_services ?? throw new InvalidOperationException("ServiceHelper is not initialized."))
            .GetRequiredService<T>();
}
