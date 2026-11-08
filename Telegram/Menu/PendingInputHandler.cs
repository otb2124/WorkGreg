using WorkGreg.Domain;
using WorkGreg.Services;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Menu;

/// <summary>
/// Plain text as the answer to a prompt ("send me your keywords"). Validation and saving are the services' job;
/// this only picks the service call for the prompt kind and the screen to show afterwards.
/// </summary>
internal sealed class PendingInputHandler(
    ProfileService profiles,
    PendingInputStore pending,
    PromptScreen promptScreen,
    ProfileScreen profileScreen,
    FiltersScreen filtersScreen)
{
    /// <summary>Returns null when nothing was waiting for an answer.</summary>
    public async Task<TextResult?> HandleAsync(long chatId, string text, CancellationToken ct)
    {
        var pend = pending.Get(chatId);
        if (pend is null) return null;

        var (kind, profileId) = pend.Target;

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var input = string.Join(pend.Target.IsList ? ", " : " ", lines);
        var clear = input is "-" or "clear" or "off";
        var csv = clear ? "" : input;

        Result<SearchProfile, Error> result;
        switch (kind)
        {
            case PromptKind.NewProfile: result = await profiles.CreateAsync(chatId, new ProfileDraft(input), ct); break;
            case PromptKind.Keyword: result = await profiles.SetKeywordAsync(chatId, profileId, input, ct); break;
            case PromptKind.Rename: result = await profiles.RenameAsync(chatId, profileId, input, ct); break;
            case PromptKind.Include: result = await profiles.SetIncludeAsync(chatId, profileId, csv, ct); break;
            case PromptKind.Exclude: result = await profiles.SetExcludeAsync(chatId, profileId, csv, ct); break;
            case PromptKind.Companies: result = await profiles.SetCompaniesAsync(chatId, profileId, csv, ct); break;
            case PromptKind.Locations: result = await profiles.SetLocationsAsync(chatId, profileId, csv, ct); break;

            case PromptKind.MinSalary:
                if (clear)
                {
                    result = await profiles.SetMinSalaryAsync(chatId, profileId, null, ct);
                    break;
                }

                if (!int.TryParse(input, out var salary) || salary <= 0)
                    return await AskAgainAsync(chatId, pend, Warn("Please send a positive whole number."), ct);

                result = await profiles.SetMinSalaryAsync(chatId, profileId, salary, ct);
                break;

            default:
                throw new InvalidOperationException($"Prompt kind {kind} has no handler.");
        }

        // A validation error: ask again.
        if (result.IsFailure)
            return await AskAgainAsync(chatId, pend, Fail(result.Error), ct);

        pending.Clear(chatId);
        var profile = result.Value;

        Screen screen = kind switch
        {
            PromptKind.NewProfile => await profileScreen.RenderAsync(chatId, profile.Id, Ok(ProfileAdded(profile)), ct),
            PromptKind.Keyword => await profileScreen.RenderAsync(chatId, profileId, Ok(KeywordChanged(profile)), ct),
            PromptKind.Rename => await profileScreen.RenderAsync(chatId, profileId, Ok(ProfileRenamed(profile)), ct),
            _ => await filtersScreen.RenderAsync(chatId, profileId, Ok(FilterReply(kind, profile)), ct)
        };

        return new TextResult(pend.MessageId, screen);
    }

    private async Task<TextResult> AskAgainAsync(long chatId, PendingInput pend, string notice, CancellationToken ct) =>
        new(pend.MessageId, await promptScreen.RenderAsync(chatId, pend.Target, notice, ct));

    private static string FilterReply(PromptKind kind, SearchProfile p) => kind switch
    {
        PromptKind.Include => IncludeSet(p),
        PromptKind.Exclude => ExcludeSet(p),
        PromptKind.Companies => CompaniesSet(p),
        PromptKind.Locations => LocationsSet(p),
        _ => SalarySet(p)
    };
}