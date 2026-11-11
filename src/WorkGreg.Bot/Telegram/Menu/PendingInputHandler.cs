using WorkGreg.Client;
using WorkGreg.Contracts;
using WorkGreg.Services;
using static WorkGreg.Telegram.Replies;

namespace WorkGreg.Telegram.Menu;

/// <summary>
/// Plain text as the answer to a prompt ("send me your keywords"). Validation and saving are the core's job;
/// what each prompt kind calls and where it goes afterwards is one row of <see cref="PromptSpecs"/>.
/// </summary>
internal sealed class PendingInputHandler(
    IWorkGregClient client,
    PendingInputStore pending,
    PromptScreen promptScreen,
    ProfileScreen profileScreen,
    FiltersScreen filtersScreen)
{
    /// <summary>Returns null when nothing was waiting for an answer (or the prompt has expired).</summary>
    public async Task<TextResult?> HandleAsync(long chatId, string text, CancellationToken ct)
    {
        var pend = pending.Get(chatId);
        if (pend is null) return null;

        var (kind, profileId) = pend.Target;
        var spec = PromptSpecs.Of(kind);

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var input = string.Join(spec.IsList ? ", " : " ", lines);
        var clear = spec.Clearable && input is "-" or "clear" or "off";

        var result = await spec.Save(client, chatId, profileId, clear ? null : input, ct);

        // A validation error: ask again.
        if (result.IsFailure)
            return await AskAgainAsync(chatId, pend, Fail(result.Error), ct);

        pending.Clear(chatId);
        var profile = result.Value;
        var notice = Ok(spec.Done(profile));

        Screen screen = spec.Return switch
        {
            PromptReturn.NewProfile => await profileScreen.RenderAsync(chatId, profile.Id, notice, ct),
            PromptReturn.Profile => await profileScreen.RenderAsync(chatId, profileId, notice, ct),
            _ => await filtersScreen.RenderAsync(chatId, profileId, notice, ct)
        };

        return new TextResult(pend.MessageId, screen);
    }

    private async Task<TextResult> AskAgainAsync(long chatId, PendingInput pend, string notice, CancellationToken ct)
    {
        pending.Set(chatId, pend);   // the question is asked again, so it gets a fresh time limit
        return new(pend.MessageId, await promptScreen.RenderAsync(chatId, pend.Target, notice, ct));
    }
}