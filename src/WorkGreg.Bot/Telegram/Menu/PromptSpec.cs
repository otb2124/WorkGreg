using WorkGreg.Client;
using WorkGreg.Contracts;
using WorkGreg.Services;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Menu;

/// <summary>Which screen follows a successful answer.</summary>
internal enum PromptReturn
{
    /// <summary>The profile that was just created (its id comes from the result).</summary>
    NewProfile,

    /// <summary>The profile page of the profile the prompt belonged to.</summary>
    Profile,

    /// <summary>The filters page of that profile.</summary>
    Filters
}

/// <summary>
/// Everything the bot knows about one <see cref="PromptKind"/>, in one row: which client call saves the answer, how a
/// comma list is read, whether the 🗑 button exists, what is said afterwards and where it goes. The text handler
/// (<see cref="PendingInputHandler"/>) and the Clear button (<see cref="ClearHandler"/>) both read this table,
/// so a new prompt kind is added here once.
/// </summary>
/// <param name="Save">
/// Sends the answer to the core. A null value means "clear" and is only ever passed for a <see cref="Clearable"/> kind.
/// Validation is the core's job; the one thing checked here is that a salary is a number, because the client call takes an int.
/// </param>
/// <param name="Done">The confirmation shown with the next screen.</param>
internal sealed record PromptSpec(
    PromptKind Kind,
    Func<IWorkGregClient, long, int, string?, CancellationToken, Task<Result<ProfileDto, Error>>> Save,
    Func<ProfileDto, string> Done,
    PromptReturn Return,
    bool Clearable = false,
    bool IsList = false);

internal static class PromptSpecs
{
    private static readonly PromptSpec[] All =
    [
        new(PromptKind.NewProfile,
            (c, chat, _, v, ct) => c.CreateProfileAsync(chat, v ?? "", ct),
            p => ProfileAdded(p), PromptReturn.NewProfile),

        new(PromptKind.Keyword,
            (c, chat, id, v, ct) => c.SetKeywordAsync(chat, id, v, ct),
            p => KeywordChanged(p), PromptReturn.Profile),

        new(PromptKind.Rename,
            (c, chat, id, v, ct) => c.RenameProfileAsync(chat, id, v, ct),
            p => ProfileRenamed(p), PromptReturn.Profile),

        new(PromptKind.Include,
            (c, chat, id, v, ct) => c.SetIncludeAsync(chat, id, v, ct),
            p => IncludeSet(p), PromptReturn.Filters, Clearable: true, IsList: true),

        new(PromptKind.Exclude,
            (c, chat, id, v, ct) => c.SetExcludeAsync(chat, id, v, ct),
            p => ExcludeSet(p), PromptReturn.Filters, Clearable: true, IsList: true),

        new(PromptKind.Companies,
            (c, chat, id, v, ct) => c.SetCompaniesAsync(chat, id, v, ct),
            p => CompaniesSet(p), PromptReturn.Filters, Clearable: true, IsList: true),

        new(PromptKind.Locations,
            (c, chat, id, v, ct) => c.SetLocationsAsync(chat, id, v, ct),
            p => LocationsSet(p), PromptReturn.Filters, Clearable: true, IsList: true),

        new(PromptKind.MinSalary, SaveSalaryAsync,
            p => SalarySet(p), PromptReturn.Filters, Clearable: true),
    ];

    private static readonly Dictionary<PromptKind, PromptSpec> ByKind = All.ToDictionary(s => s.Kind);

    public static PromptSpec Of(PromptKind kind) =>
        ByKind.TryGetValue(kind, out var spec)
            ? spec
            : throw new InvalidOperationException($"Prompt kind {kind} has no spec.");

    private static Task<Result<ProfileDto, Error>> SaveSalaryAsync(
        IWorkGregClient client, long chatId, int profileId, string? value, CancellationToken ct)
    {
        if (value is null) return client.SetMinSalaryAsync(chatId, profileId, null, ct);

        if (!int.TryParse(value, out var salary) || salary <= 0)
            return Task.FromResult<Result<ProfileDto, Error>>(Errors.PositiveWholeNumberExpected);

        return client.SetMinSalaryAsync(chatId, profileId, salary, ct);
    }
}
