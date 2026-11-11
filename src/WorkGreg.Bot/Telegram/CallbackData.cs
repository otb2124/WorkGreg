using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace WorkGreg.Telegram;

/// <summary>Everything a button can ask for. One value, one handler class (see <see cref="CallbackRouter"/>).</summary>
internal enum CallbackAction
{
    // screens
    Home, Help, Profiles, Profile, Filters, Sources, Delivery, DeliveryHourPicker,
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
internal enum PromptKind { NewProfile, Keyword, Rename, Include, Exclude, Companies, Locations, MinSalary }

/// <summary>A prompt: the kind of value, and the profile it belongs to (0 for a new profile).</summary>
internal readonly record struct PromptTarget(PromptKind Kind, int ProfileId)
{
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
    ];

    private static readonly Dictionary<PromptKind, string> CodeOf = Table.ToDictionary(t => t.Kind, t => t.Code);
    private static readonly Dictionary<string, PromptKind> KindOf = Table.ToDictionary(t => t.Code, t => t.Kind, StringComparer.Ordinal);

    public static PromptTarget NewProfile() => new(PromptKind.NewProfile, 0);

    /// <summary>Filter prompts can be cleared with the 🗑 button.</summary>
    public bool IsFilter => Menu.PromptSpecs.Of(Kind).Clearable;

    /// <summary>The answer is a comma list (lines are joined with ", ").</summary>
    public bool IsList => Menu.PromptSpecs.Of(Kind).IsList;

    /// <summary>"newp" or "kw:3".</summary>
    public string Encode() => Kind == PromptKind.NewProfile ? CodeOf[Kind] : $"{CodeOf[Kind]}:{ProfileId}";

    public static bool TryParse(string? text, out PromptTarget target)
    {
        target = default;
        if (text is null) return false;

        var bits = text.Split(':', 2);
        if (!KindOf.TryGetValue(bits[0], out var kind)) return false;

        if (kind == PromptKind.NewProfile)
        {
            if (bits.Length != 1) return false;
            target = NewProfile();
            return true;
        }

        if (bits.Length != 2 || !CallbackData.TryId(bits[1], out var id)) return false;
        target = new PromptTarget(kind, id);
        return true;
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

    /// <summary>The actions that open a screen. "Cancel" can only go back to one of these.</summary>
    public bool IsScreen => Action is CallbackAction.Home or CallbackAction.Help or CallbackAction.Profiles
        or CallbackAction.Profile or CallbackAction.Filters or CallbackAction.Sources or CallbackAction.Delivery;

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
                or CallbackAction.RemoveAsk or CallbackAction.RemoveConfirm => $"{code}:{ProfileId}",

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

            case CallbackAction.Profile or CallbackAction.Filters or CallbackAction.RemoveAsk or CallbackAction.RemoveConfirm:
                {
                    return TryId(arg, out var id) ? new CallbackData(action) { ProfileId = id } : null;
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