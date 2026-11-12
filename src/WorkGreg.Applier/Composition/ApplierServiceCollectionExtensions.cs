using Microsoft.Extensions.DependencyInjection.Extensions;

namespace WorkGreg.Composition;

public static class ApplierServiceCollectionExtensions
{
    /// <summary>
    /// The browser side of autofill. Nothing starts a browser here: <see cref="Applying.PlaywrightHost"/> launches Chromium on
    /// first use only, so registering it is free while <c>Applier:Enabled</c> is false.
    /// Add new ATS adapters (Lever, Ashby, generic) as one more <c>IAtsAdapter</c> line below.
    /// Needs <c>AddFetching()</c> (HostThrottle) and <c>AddApplicationServices()</c> (AutofillService) to be registered too.
    /// </summary>
    public static IServiceCollection AddApplier(this IServiceCollection services)
    {
        services.TryAddSingleton<Applying.PlaywrightHost>();

        services.AddSingleton<Applying.IAtsAdapter, Applying.GreenhouseAdapter>();

        services.TryAddSingleton<Applying.IFormFiller, Applying.PlaywrightFormFiller>();

        // One runner: it is the hosted worker and the queue the rest of the app puts applications into.
        services.AddScoped<Applying.ApplicationRunStore>();
        services.AddSingleton<Applying.ApplicationRunner>();
        services.AddSingleton<Applying.IApplicationQueue>(sp => sp.GetRequiredService<Applying.ApplicationRunner>());
        services.AddHostedService(sp => sp.GetRequiredService<Applying.ApplicationRunner>());
        return services;
    }
}