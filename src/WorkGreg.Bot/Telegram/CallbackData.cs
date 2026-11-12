using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using WorkGreg.Contracts;
using WorkGreg.Domain;

namespace WorkGreg.Telegram;

/// <summary>Everything a button can ask for. One value, one handler class (see <see cref="CallbackRouter"/>).</summary>
internal enum CallbackAction
{
    // screens
    Home, Help, Profiles, Profile, Filters, Sources, Delivery, DeliveryHourPicker,
    // autofill: its screens (settings, custom answers, one answer, copy picker) and the buttons that change it
    Autofill, AutofillRules, AutofillRule, AutofillCopy, AutofillClearValue, AutofillDeleteRule, AutofillDeleteDoc,
    // the buttons under a delivered vacancy: 🤖 AutoFill, ✅ Applied, ⏭ Skip (they carry a delivery id)
    AutofillStart, VacancyApplied, VacancySkip,
    // confirmations
    ResendAsk, RemoveAsk,
    // changes
    ResendConfirm, RemoveConfirm, Pause, ToggleEnabled, ToggleRemote, ToggleSource, DeliveryInstant, DeliveryHour,
    // prompts (ask the user to type a value)
    NewProfile, Ask, Clear, Cancel,
    // control
    Check, Stop
}

/// <summary>Which value a prompt is waiting for.</summary>
internal enum PromptKind
{
    NewProfile, Keyword, Rename, Include, Exclude, Companies, Locations, MinSalary,
    // autofill: one standard value (needs Key), a new custom answer, an existing one (needs ItemId), the CV file
    AutofillValue, AutofillAddRule, AutofillEditRule, AutofillCv
}

/// <summary>
/// A prompt: the kind of value, and the profile it belongs to (0 for a new profile). The autofill prompts carry one more
/// piece: <see cref="Key"/> for a standard value, <see cref="ItemId"/> (the rule id) for an edited custom answer.
/// Those two are not positional, so <c>var (kind, profileId) = target</c> keeps working.
/// </summary>
internal readonly record struct PromptTarget(PromptKind Kind, int ProfileId)
{
    /// <summary>Only for <see cref="PromptKind.AutofillValue"/>: which standard value is being typed.</summary>
    public AutofillKey? Key { get; init; }

    /// <summary>Only for <see cref="PromptKind.AutofillEditRule"/>: the id of the custom answer being edited.</summary>
    public int ItemId { get; init; }

    private static readonly (PromptKind Kind, string Code)[] Table =
    [
        (PromptKind.NewProfile, "newp"),
        (PromptKind.Keyword, "kw"),
        (PromptKind.Rename, "nm"),
        (PromptKind.Include, "inc"),
        (PromptKind.Exclude, "exc"),
        (PromptKind.Companies, "com"),
        (PromptKind.Locations, "loc"),
        (PromptKind.MinSalary, "sal"),
        (PromptKind.AutofillValue, "afv"),
        (PromptKind.AutofillAddRule, "afc"),
        (PromptKind.AutofillEditRule, "afe"),
        (PromptKind.AutofillCv, "afd"),
    ];

    private static readonly Dictionary<PromptKind, string> CodeOf = Table.ToDictionary(t => t.Kind, t => t.Code);
    private static readonly Dictionary<string, PromptKind> KindOf = Table.ToDictionary(t => t.Code, t => t.Kind, StringComparer.Ordinal);

    public static PromptTarget NewProfile() => new(PromptKind.NewProfile, 0);

    /// <summary>The prompts of the autofill screens. They have their own handling (see PendingInputHandler), not a <see cref="Menu.PromptSpecs"/> row.</summary>
    public bool IsAutofill => Kind is PromptKind.AutofillValue or PromptKind.AutofillAddRule
        or PromptKind.AutofillEditRule or PromptKind.AutofillCv;

    /// <summary>Filter prompts can be cleared with the 🗑 button.</summary>
    public bool IsFilter => !IsAutofill && Menu.PromptSpecs.Of(Kind).Clearable;

    /// <summary>The answer is a comma list (lines are joined with ", ").</summary>
    public bool IsList => !IsAutofill && Menu.PromptSpecs.Of(Kind).IsList;

    /// <summary>"newp", "kw:3", "afv:3:FirstName" or "afe:3:12".</summary>
    public string Encode() => Kind switch
    {
        PromptKind.NewProfile => CodeOf[Kind],
        PromptKind.AutofillValue => $"{CodeOf[Kind]}:{ProfileId}:{Key}",
        PromptKind.AutofillEditRule => $"{CodeOf[Kind]}:{ProfileId}:{ItemId}",
        _ => $"{CodeOf[Kind]}:{ProfileId}"
    };

    public static bool TryParse(string? text, out PromptTarget target)
    {
        target = default;
        if (text is null) return false;

        var bits = text.Split(':', 3);
        if (!KindOf.TryGetValue(bits[0], out var kind)) return false;

        if (kind == PromptKind.NewProfile)
        {
            if (bits.Length != 1) return false;
            target = NewProfile();
            return true;
        }

        if (bits.Length < 2 || !CallbackData.TryId(bits[1], out var id)) return false;

        switch (kind)
        {
            case PromptKind.AutofillValue:
                if (bits.Length != 3 || !CallbackData.TryKey(bits[2], out var key)) return false;
                target = new PromptTarget(kind, id) { Key = key };
                return true;

            case PromptKind.AutofillEditRule:
                if (bits.Length != 3 || !CallbackData.TryId(bits[2], out var ruleId)) return false;
                target = new PromptTarget(kind, id) { ItemId = ruleId };
                return true;

            default:
                if (bits.Length != 2) return false;
                target = new PromptTarget(kind, id);
                return true;
        }
    }
}

/// <summary>
/// What a button carries. Built with the factories below, turned into Telegram's callback string with
/// <see cref="Encode"/>, and read back with <see cref="TryParse"/>. Nothing else in the bot splits or joins these strings.
///
/// The wire format is the one the old MenuHandler used, so buttons in messages that are already in a chat keep working.
/// Telegram allows 64 bytes; <see cref="Encode"/> throws if a button would not fit.
///
///   home | help | pls | dlv | dlvd | dlvi | chk | stop | rst | rstok | np
///   pause:0|1
///   pen:&lt;id&gt;:0|1 | prem:&lt;id&gt;:0|1          the value to set (enabled / remote only), so a tap is idempotent
///   pen:&lt;id&gt; | prem:&lt;id&gt;                   old buttons without a value: still accepted, they flip the current value
///   p:&lt;id&gt; | pf:&lt;id&gt; | prem... | prem... | pdel:&lt;id&gt; | pdelok:&lt;id&gt;
///   ps:&lt;id&gt;[:&lt;region&gt;]
///   pst:&lt;id&gt;:&lt;provider&gt;[:&lt;region&gt;]
///   ask:&lt;prompt&gt; | clr:&lt;prompt&gt;          prompt = newp | kw|nm|inc|exc|com|loc|sal:&lt;id&gt;
///   cancel:&lt;screen&gt;                      screen = home | pls | dlv | p:&lt;id&gt; | pf:&lt;id&gt; | ps:&lt;id&gt;[:&lt;region&gt;] | help
///   dlvh:&lt;hour&gt;
///   afs:&lt;id&gt; | afl:&lt;id&gt; | afdd:&lt;id&gt;                autofill settings | custom answers | delete the CV
///   afr:&lt;id&gt;:&lt;rule&gt; | afrd:&lt;id&gt;:&lt;rule&gt;          one custom answer | delete it
///   afvd:&lt;id&gt;:&lt;key&gt;                          clear a standard value (key = AutofillKey name)
///   afcp:&lt;id&gt;[:&lt;from&gt;]                        pick a profile to copy autofill from | copy it
///   ask:afv:&lt;id&gt;:&lt;key&gt; | ask:afc:&lt;id&gt; | ask:afe:&lt;id&gt;:&lt;rule&gt; | ask:afd:&lt;id&gt;   autofill prompts
///   af:&lt;delivery&gt; | ad:&lt;delivery&gt; | sk:&lt;delivery&gt;    under a delivered vacancy: AutoFill | Applied | Skip.
///                                        The CORE builds these (DeliveryCallbacks, shared via Contracts), so they have no factory-only form.
/// </summary>
internal sealed record CallbackData(CallbackAction Action)
{
    public const int MaxBytes = 64;

    public int ProfileId { get; init; }
    public string? Provider { get; init; }
    public string? Region { get; init; }
    public PromptTarget? Prompt { get; init; }
    public int Hour { get; init; }
    public bool Flag { get; init; }

    /// <summary>
    /// For <see cref="CallbackAction.ToggleEnabled"/> and <see cref="CallbackAction.ToggleRemote"/>: the value the button
    /// sets. Null only for a button that was sent before values were added; its handler flips the current value instead.
    /// </summary>
    public bool? Desired { get; init; }

    /// <summary>For <see cref="CallbackAction.Cancel"/>: the screen to go back to.</summary>
    public CallbackData? Back { get; init; }

    /// <summary>The custom answer a button is about (<see cref="CallbackAction.AutofillRule"/>, <see cref="CallbackAction.AutofillDeleteRule"/>).</summary>
    public int ItemId { get; init; }

    /// <summary>For <see cref="CallbackAction.AutofillClearValue"/>: the standard value to clear.</summary>
    public AutofillKey? Key { get; init; }

    /// <summary>For <see cref="CallbackAction.AutofillCopy"/>: the profile to copy from. 0 while the user is still choosing one.</summary>
    public int FromProfileId { get; init; }

    /// <summary>For the buttons under a vacancy (<see cref="CallbackAction.AutofillStart"/>, <see cref="CallbackAction.VacancyApplied"/>, <see cref="CallbackAction.VacancySkip"/>): the vacancy's delivery id.</summary>
    public int DeliveryId { get; init; }

    // ------------------------------------------------------------------ factories

    public static CallbackData Home => new(CallbackAction.Home);
    public static CallbackData Help => new(CallbackAction.Help);
    public static CallbackData Profiles => new(CallbackAction.Profiles);
    public static CallbackData Delivery => new(CallbackAction.Delivery);
    public static CallbackData DeliveryHourPicker => new(CallbackAction.DeliveryHourPicker);
    public static CallbackData DeliveryInstant => new(CallbackAction.DeliveryInstant);
    public static CallbackData Check => new(CallbackAction.Check);
    public static CallbackData Stop => new(CallbackAction.Stop);
    public static CallbackData ResendAsk => new(CallbackAction.ResendAsk);
    public static CallbackData ResendConfirm => new(CallbackAction.ResendConfirm);
    public static CallbackData NewProfile => new(CallbackAction.NewProfile);

    public static CallbackData Pause(bool pause) => new(CallbackAction.Pause) { Flag = pause };
    public static CallbackData Profile(int id) => new(CallbackAction.Profile) { ProfileId = id };
    public static CallbackData Filters(int id) => new(CallbackAction.Filters) { ProfileId = id };
    public static CallbackData Sources(int id, string? region = null) => new(CallbackAction.Sources) { ProfileId = id, Region = region };
    public static CallbackData ToggleSource(int id, string provider, string? region = null) =>
        new(CallbackAction.ToggleSource) { ProfileId = id, Provider = provider, Region = region };

    /// <summary>The button says what the profile's enabled state should become, not "flip it".</summary>
    public static CallbackData ToggleEnabled(int id, bool enable) =>
        new(CallbackAction.ToggleEnabled) { ProfileId = id, Desired = enable };

    /// <summary>The button says what the remote-only filter should become, not "flip it".</summary>
    public static CallbackData ToggleRemote(int id, bool on) =>
        new(CallbackAction.ToggleRemote) { ProfileId = id, Desired = on };

    public static CallbackData RemoveAsk(int id) => new(CallbackAction.RemoveAsk) { ProfileId = id };
    public static CallbackData RemoveConfirm(int id) => new(CallbackAction.RemoveConfirm) { ProfileId = id };
    public static CallbackData DeliveryHour(int hour) => new(CallbackAction.DeliveryHour) { Hour = hour };

    public static CallbackData Ask(PromptTarget target) => new(CallbackAction.Ask) { Prompt = target };
    public static CallbackData Ask(PromptKind kind, int profileId) => Ask(new PromptTarget(kind, profileId));
    public static CallbackData Clear(PromptTarget target) => new(CallbackAction.Clear) { Prompt = target };
    public static CallbackData Cancel(CallbackData back) => new(CallbackAction.Cancel) { Back = back };

    // autofill
    public static CallbackData Autofill(int id) => new(CallbackAction.Autofill) { ProfileId = id };
    public static CallbackData AutofillRules(int id) => new(CallbackAction.AutofillRules) { ProfileId = id };
    public static CallbackData AutofillRule(int id, int ruleId) => new(CallbackAction.AutofillRule) { ProfileId = id, ItemId = ruleId };
    public static CallbackData AutofillDeleteRule(int id, int ruleId) => new(CallbackAction.AutofillDeleteRule) { ProfileId = id, ItemId = ruleId };
    public static CallbackData AutofillClearValue(int id, AutofillKey key) => new(CallbackAction.AutofillClearValue) { ProfileId = id, Key = key };
    public static CallbackData AutofillDeleteDoc(int id) => new(CallbackAction.AutofillDeleteDoc) { ProfileId = id };
    public static CallbackData AutofillCopy(int id, int fromId = 0) => new(CallbackAction.AutofillCopy) { ProfileId = id, FromProfileId = fromId };

    // under a delivered vacancy
    public static CallbackData AutofillStart(int deliveryId) => new(CallbackAction.AutofillStart) { DeliveryId = deliveryId };
    public static CallbackData VacancyApplied(int deliveryId) => new(CallbackAction.VacancyApplied) { DeliveryId = deliveryId };
    public static CallbackData VacancySkip(int deliveryId) => new(CallbackAction.VacancySkip) { DeliveryId = deliveryId };

    public static CallbackData AskValue(int id, AutofillKey key) =>
        Ask(new PromptTarget(PromptKind.AutofillValue, id) { Key = key });
    public static CallbackData AskAddRule(int id) => Ask(PromptKind.AutofillAddRule, id);
    public static CallbackData AskEditRule(int id, int ruleId) =>
        Ask(new PromptTarget(PromptKind.AutofillEditRule, id) { ItemId = ruleId });
    public static CallbackData AskCv(int id) => Ask(PromptKind.AutofillCv, id);

    /// <summary>The actions that open a screen. "Cancel" can only go back to one of these.</summary>
    public bool IsScreen => Action is CallbackAction.Home or CallbackAction.Help or CallbackAction.Profiles
        or CallbackAction.Profile or CallbackAction.Filters or CallbackAction.Sources or CallbackAction.Delivery
        or CallbackAction.Autofill or CallbackAction.AutofillRules or CallbackAction.AutofillRule;

    // ------------------------------------------------------------------ wire format

    private static readonly (CallbackAction Action, string Code)[] Table =
    [
        (CallbackAction.Home, "home"),
        (CallbackAction.Help, "help"),
        (CallbackAction.Profiles, "pls"),
        (CallbackAction.Profile, "p"),
        (CallbackAction.Filters, "pf"),
        (CallbackAction.Sources, "ps"),
        (CallbackAction.Delivery, "dlv"),
        (CallbackAction.DeliveryHourPicker, "dlvd"),
        (CallbackAction.Autofill, "afs"),
        (CallbackAction.AutofillRules, "afl"),
        (CallbackAction.AutofillRule, "afr"),
        (CallbackAction.AutofillCopy, "afcp"),
        (CallbackAction.AutofillClearValue, "afvd"),
        (CallbackAction.AutofillDeleteRule, "afrd"),
        (CallbackAction.AutofillDeleteDoc, "afdd"),
        (CallbackAction.AutofillStart, DeliveryCallbacks.AutofillCode),
        (CallbackAction.VacancyApplied, DeliveryCallbacks.AppliedCode),
        (CallbackAction.VacancySkip, DeliveryCallbacks.SkipCode),
        (CallbackAction.ResendAsk, "rst"),
        (CallbackAction.RemoveAsk, "pdel"),
        (CallbackAction.ResendConfirm, "rstok"),
        (CallbackAction.RemoveConfirm, "pdelok"),
        (CallbackAction.Pause, "pause"),
        (CallbackAction.ToggleEnabled, "pen"),
        (CallbackAction.ToggleRemote, "prem"),
        (CallbackAction.ToggleSource, "pst"),
        (CallbackAction.DeliveryInstant, "dlvi"),
        (CallbackAction.DeliveryHour, "dlvh"),
        (CallbackAction.NewProfile, "np"),
        (CallbackAction.Ask, "ask"),
        (CallbackAction.Clear, "clr"),
        (CallbackAction.Cancel, "cancel"),
        (CallbackAction.Check, "chk"),
        (CallbackAction.Stop, "stop"),
    ];

    private static readonly Dictionary<CallbackAction, string> CodeOf = Table.ToDictionary(t => t.Action, t => t.Code);
    private static readonly Dictionary<string, CallbackAction> ActionOf = Table.ToDictionary(t => t.Code, t => t.Action, StringComparer.Ordinal);

    /// <summary>Called at startup: every action must have a wire code, or a button for it could not be built.</summary>
    public static void VerifyWireCodes()
    {
        var missing = Enum.GetValues<CallbackAction>().Where(a => !CodeOf.ContainsKey(a)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"CallbackData has no wire code for: {string.Join(", ", missing)}.");
    }

    public string Encode()
    {
        var code = CodeOf[Action];

        var text = Action switch
        {
            CallbackAction.Pause => $"{code}:{(Flag ? 1 : 0)}",

            CallbackAction.ToggleEnabled or CallbackAction.ToggleRemote =>
                Desired is { } want ? $"{code}:{ProfileId}:{(want ? 1 : 0)}" : $"{code}:{ProfileId}",

            CallbackAction.Profile or CallbackAction.Filters
                or CallbackAction.RemoveAsk or CallbackAction.RemoveConfirm
                or CallbackAction.Autofill or CallbackAction.AutofillRules or CallbackAction.AutofillDeleteDoc => $"{code}:{ProfileId}",

            CallbackAction.AutofillStart or CallbackAction.VacancyApplied or CallbackAction.VacancySkip => $"{code}:{DeliveryId}",

            CallbackAction.AutofillRule or CallbackAction.AutofillDeleteRule => $"{code}:{ProfileId}:{ItemId}",
            CallbackAction.AutofillClearValue => $"{code}:{ProfileId}:{Key}",
            CallbackAction.AutofillCopy => FromProfileId == 0 ? $"{code}:{ProfileId}" : $"{code}:{ProfileId}:{FromProfileId}",

            CallbackAction.Sources => Region is null ? $"{code}:{ProfileId}" : $"{code}:{ProfileId}:{Region}",

            CallbackAction.ToggleSource => Region is null
                ? $"{code}:{ProfileId}:{Provider}"
                : $"{code}:{ProfileId}:{Provider}:{Region}",

            CallbackAction.Ask or CallbackAction.Clear => $"{code}:{Prompt!.Value.Encode()}",
            CallbackAction.Cancel => $"{code}:{Back!.Encode()}",
            CallbackAction.DeliveryHour => $"{code}:{Hour}",

            _ => code
        };

        if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
            throw new InvalidOperationException($"Callback data '{text}' is longer than Telegram's {MaxBytes} bytes.");

        return text;
    }

    /// <summary>False for anything this bot did not produce (old, edited or made-up data). Never throws.</summary>
    public static bool TryParse(string? data, [NotNullWhen(true)] out CallbackData? result)
    {
        result = null;
        if (string.IsNullOrEmpty(data) || Encoding.UTF8.GetByteCount(data) > MaxBytes) return false;

        var split = data.Split(':', 2);
        if (!ActionOf.TryGetValue(split[0], out var action)) return false;

        result = Build(action, split.Length > 1 ? split[1] : null);
        return result is not null;
    }

    /// <summary>A profile id as it appears in callback data: digits only, greater than zero.</summary>
    internal static bool TryId(string? text, out int id)
    {
        id = 0;
        return text is not null
               && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id)
               && id > 0;
    }

    /// <summary>An <see cref="AutofillKey"/> by its name, as <see cref="Encode"/> writes it. Numbers and unknown names are refused.</summary>
    internal static bool TryKey(string? text, out AutofillKey key)
    {
        key = default;
        return !string.IsNullOrEmpty(text)
               && !char.IsDigit(text[0])
               && Enum.TryParse(text, ignoreCase: false, out key)
               && Enum.IsDefined(key);
    }

    /// <param name="arg">Everything after the first ":", or null when there was no ":".</param>
    private static CallbackData? Build(CallbackAction action, string? arg)
    {
        switch (action)
        {
            case CallbackAction.Pause:
                return arg is "0" or "1" ? Pause(arg == "1") : null;

            case CallbackAction.ToggleEnabled or CallbackAction.ToggleRemote:
                {
                    var bits = arg?.Split(':', 2);
                    if (bits is null || !TryId(bits[0], out var id)) return null;

                    // "pen:3" is a button from before values were added: no Desired, the handler flips the current value.
                    if (bits.Length == 1) return new CallbackData(action) { ProfileId = id };

                    return bits[1] is "0" or "1"
                        ? new CallbackData(action) { ProfileId = id, Desired = bits[1] == "1" }
                        : null;
                }

            case CallbackAction.Profile or CallbackAction.Filters or CallbackAction.RemoveAsk or CallbackAction.RemoveConfirm
                or CallbackAction.Autofill or CallbackAction.AutofillRules or CallbackAction.AutofillDeleteDoc:
                {
                    return TryId(arg, out var id) ? new CallbackData(action) { ProfileId = id } : null;
                }

            case CallbackAction.AutofillStart or CallbackAction.VacancyApplied or CallbackAction.VacancySkip:
                {
                    return TryId(arg, out var deliveryId) ? new CallbackData(action) { DeliveryId = deliveryId } : null;
                }

            case CallbackAction.AutofillRule or CallbackAction.AutofillDeleteRule:
                {
                    var bits = arg?.Split(':', 2);
                    return bits is { Length: 2 } && TryId(bits[0], out var id) && TryId(bits[1], out var ruleId)
                        ? new CallbackData(action) { ProfileId = id, ItemId = ruleId }
                        : null;
                }

            case CallbackAction.AutofillClearValue:
                {
                    var bits = arg?.Split(':', 2);
                    return bits is { Length: 2 } && TryId(bits[0], out var id) && TryKey(bits[1], out var key)
                        ? AutofillClearValue(id, key)
                        : null;
                }

            case CallbackAction.AutofillCopy:
                {
                    var bits = arg?.Split(':', 2);
                    if (bits is null || !TryId(bits[0], out var id)) return null;
                    if (bits.Length == 1) return AutofillCopy(id);

                    return TryId(bits[1], out var source) ? AutofillCopy(id, source) : null;
                }

            case CallbackAction.Sources:
                {
                    var bits = arg?.Split(':', 2);
                    if (bits is null || !TryId(bits[0], out var id)) return null;

                    var region = bits.Length > 1 ? bits[1] : null;
                    return region is { Length: 0 } ? null : Sources(id, region);
                }

            case CallbackAction.ToggleSource:
                {
                    var bits = arg?.Split(':', 3);
                    if (bits is null || bits.Length < 2 || !TryId(bits[0], out var id) || bits[1].Length == 0) return null;

                    var region = bits.Length > 2 ? bits[2] : null;
                    return region is { Length: 0 } ? null : ToggleSource(id, bits[1], region);
                }

            case CallbackAction.Ask or CallbackAction.Clear:
                {
                    if (!PromptTarget.TryParse(arg, out var target)) return null;
                    if (action == CallbackAction.Clear && !target.IsFilter) return null;
                    return new CallbackData(action) { Prompt = target };
                }

            case CallbackAction.Cancel:
                {
                    // "Back" must be a screen. That also rules out cancel inside cancel.
                    return arg is not null && TryParse(arg, out var back) && back.IsScreen ? Cancel(back) : null;
                }

            case CallbackAction.DeliveryHour:
                {
                    return arg is not null && int.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out var hour)
                        ? DeliveryHour(hour)
                        : null;
                }

            default:
                return arg is null ? new CallbackData(action) : null;
        }
    }
}

