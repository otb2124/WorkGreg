using Microsoft.Extensions.Options;

namespace WorkGreg.Services;

/// <summary>Section <c>Applier</c>. The browser-based autofill is off until it is switched on here.</summary>
public sealed class ApplierOptions
{
    /// <summary>False: the AutoFill button keeps the Phase 2 preview behaviour and no browser is started.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>False opens a visible browser window; useful when debugging locally.</summary>
    public bool Headless { get; set; } = true;

    /// <summary>One run (open page, fill, screenshot) is cancelled after this many seconds.</summary>
    public int MaxRunSeconds { get; set; } = 90;

    /// <summary>Most runs per user and UTC day.</summary>
    public int MaxPerDay { get; set; } = 10;

    /// <summary>Stored screenshots older than this many days are removed.</summary>
    public int ScreenshotRetentionDays { get; set; } = 7;
}

public sealed class ApplierOptionsValidator : IValidateOptions<ApplierOptions>
{
    public ValidateOptionsResult Validate(string? name, ApplierOptions options)
    {
        var errors = new List<string>();

        if (options.MaxRunSeconds is < 10 or > 600)
            errors.Add("Applier:MaxRunSeconds must be between 10 and 600.");
        if (options.MaxPerDay is < 1 or > 100)
            errors.Add("Applier:MaxPerDay must be between 1 and 100.");
        if (options.ScreenshotRetentionDays is < 1 or > 365)
            errors.Add("Applier:ScreenshotRetentionDays must be between 1 and 365.");

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}
