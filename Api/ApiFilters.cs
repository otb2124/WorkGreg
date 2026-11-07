using System.Security.Cryptography;
using System.Text;
using WorkGreg.Services;
using Microsoft.Extensions.Options;

namespace WorkGreg.Api;

/// <summary>Requires the X-Api-Key header to match Api:ApiKey.</summary>
public sealed class ApiKeyFilter(IOptions<ApiOptions> options) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // Startup validation already refuses an empty key; this is a second line of defence.
        if (string.IsNullOrWhiteSpace(options.Value.ApiKey))
            return ValueTask.FromResult<object?>(Results.StatusCode(503));

        var supplied = Encoding.UTF8.GetBytes(context.HttpContext.Request.Headers["X-Api-Key"].ToString());
        var expected = Encoding.UTF8.GetBytes(options.Value.ApiKey);

        return CryptographicOperations.FixedTimeEquals(supplied, expected)
            ? next(context)
            : ValueTask.FromResult<object?>(Results.Unauthorized());
    }
}

/// <summary>Same rule as the bot (<see cref="BotOptions.IsAllowed"/>): the {chatId} route value must be allowed.</summary>
public sealed class ChatAccessFilter(IOptions<BotOptions> options) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!long.TryParse(context.HttpContext.Request.RouteValues["chatId"]?.ToString(), out var chatId))
            return ValueTask.FromResult<object?>(Results.BadRequest("Invalid chat id."));

        if (!options.Value.IsAllowed(chatId))
            return ValueTask.FromResult<object?>(Results.StatusCode(StatusCodes.Status403Forbidden));

        return next(context);
    }
}