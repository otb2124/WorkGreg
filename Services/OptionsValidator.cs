using Microsoft.Extensions.Options;

namespace WorkGreg.Services;

// Registered with ValidateOnStart(): a bad config stops the app at startup with one clear message
// instead of failing later, deep inside a request or a poll cycle.

public sealed class BotOptionsValidator : IValidateOptions<BotOptions>
{
    public ValidateOptionsResult Validate(string? name, BotOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Token))
            errors.Add("Bot:Token is not set. Use user-secrets or the Bot__Token env var.");

        if (options.Access == AccessMode.Allowlist && options.AllowedChatIds.Length == 0)
            errors.Add("Bot:Access is Allowlist but Bot:AllowedChatIds is empty, so nobody could use the bot. " +
                       "Add chat ids, or set Bot:Access to Open.");

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}

public sealed class PollOptionsValidator : IValidateOptions<PollOptions>
{
    public ValidateOptionsResult Validate(string? name, PollOptions options)
    {
        var errors = new List<string>();

        if (options.IntervalMinutes < 1)
            errors.Add("Polling:IntervalMinutes must be at least 1.");
        if (options.StartupDelaySeconds < 0)
            errors.Add("Polling:StartupDelaySeconds can't be negative.");
        if (options.MaxMessagesPerRun is < 1 or > 200)
            errors.Add("Polling:MaxMessagesPerRun must be between 1 and 200.");
        if (options.SendDelayMilliseconds is < 0 or > 5000)
            errors.Add("Polling:SendDelayMilliseconds must be between 0 and 5000.");

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}

public sealed class ApiOptionsValidator : IValidateOptions<ApiOptions>
{
    public ValidateOptionsResult Validate(string? name, ApiOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ApiKey))
            errors.Add("Api:ApiKey is not set. Use user-secrets or the Api__ApiKey env var.");

        foreach (var origin in options.AllowedOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                errors.Add($"Api:AllowedOrigins contains '{origin}', which is not an absolute http(s) origin.");
        }

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}