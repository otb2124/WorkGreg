using System.Text.Json.Serialization;
using WorkGreg.Services;

namespace WorkGreg.Composition;

public static class ApiServiceCollectionExtensions
{
    /// <summary>JSON enum-as-string and the CORS policy for the REST API.</summary>
    public static IServiceCollection AddWorkGregApi(this IServiceCollection services, IConfiguration configuration)
    {
        // API: enums as strings ("feed" / "Feed") instead of numbers.
        services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        var api = configuration.GetSection("Api").Get<ApiOptions>() ?? new ApiOptions();
        services.AddCors(o => o.AddDefaultPolicy(p =>
        {
            if (api.AllowedOrigins.Length > 0)
                p.WithOrigins(api.AllowedOrigins).AllowAnyHeader().AllowAnyMethod();
        }));

        return services;
    }
}