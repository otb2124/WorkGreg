namespace WorkGreg.Contracts;

/// <summary>
/// The callback data of the buttons under a delivered vacancy. The core builds these strings (it sends the message), the
/// bot reads them (it handles the tap), so the codes live here, in the one project both see. Format: "code:deliveryId",
/// at most a few bytes, far below Telegram's 64.
/// </summary>
public static class DeliveryCallbacks
{
    public const string AutofillCode = "af";
    public const string AppliedCode = "ad";
    public const string SkipCode = "sk";

    public static string Autofill(int deliveryId) => $"{AutofillCode}:{deliveryId}";
    public static string Applied(int deliveryId) => $"{AppliedCode}:{deliveryId}";
    public static string Skip(int deliveryId) => $"{SkipCode}:{deliveryId}";
}
