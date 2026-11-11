using System.Globalization;
using System.Text.Json.Serialization;

namespace WorkGreg.Services;

/// <summary>Why an operation failed. Adapters map this to their own vocabulary (HTTP status, Telegram text).</summary>
public enum ErrorKind
{
    /// <summary>The input is not acceptable (bad keyword, unknown provider, out-of-range hour...).</summary>
    Validation,

    /// <summary>The thing does not exist, or belongs to somebody else.</summary>
    NotFound,

    /// <summary>The thing already exists / the state does not allow it.</summary>
    Conflict,

    /// <summary>Something else is already running (a check).</summary>
    Busy
}

/// <summary>
/// A failure. Three parts, each with its own reader:
/// <see cref="Kind"/> is the class of failure (adapters map it to an HTTP status),
/// <see cref="Code"/> is a stable machine-readable id such as <c>profile.not_found</c> (see <see cref="ErrorCodes"/>;
/// a front end can look its own wording up by it), and <see cref="Message"/> is the English plain text for the API and the logs.
/// <see cref="Args"/> carries the values that appear in the message (<c>max</c>, <c>name</c>...), so a translated text can
/// use them without parsing the English one.
/// The message is never HTML and never a slash-command hint: the adapter decides how to present it.
/// Create errors through <see cref="Errors"/>, not by hand, so every code has exactly one message.
/// </summary>
public sealed record Error(
    ErrorKind Kind,
    string Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string>? Args = null)
{
    public static Error Validation(string code, string message) => new(ErrorKind.Validation, code, message);
    public static Error Validation(string code, string message, IReadOnlyDictionary<string, string> args) =>
        new(ErrorKind.Validation, code, message, args);

    public static Error NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);
    public static Error NotFound(string code, string message, IReadOnlyDictionary<string, string> args) =>
        new(ErrorKind.NotFound, code, message, args);

    public static Error Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);
    public static Error Conflict(string code, string message, IReadOnlyDictionary<string, string> args) =>
        new(ErrorKind.Conflict, code, message, args);

    public static Error Busy(string code, string message) => new(ErrorKind.Busy, code, message);
    public static Error Busy(string code, string message, IReadOnlyDictionary<string, string> args) =>
        new(ErrorKind.Busy, code, message, args);

    // Explicit overloads instead of params. Values are formatted with the invariant culture, so 1000 stays "1000".
    public static IReadOnlyDictionary<string, string> Arg(string key, object value) =>
        new Dictionary<string, string> { [key] = Format(value) };

    public static IReadOnlyDictionary<string, string> Arg(string key1, object value1, string key2, object value2) =>
        new Dictionary<string, string> { [key1] = Format(value1), [key2] = Format(value2) };

    private static string Format(object value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
}

/// <summary>"No value" for operations that only succeed or fail.</summary>
public readonly struct Unit
{
    public static readonly Unit Value = default;
}

/// <summary>Either a value or an error. Services return it instead of throwing for expected failures.</summary>
public readonly struct Result<T, TError>
{
    private readonly T? _value;
    private readonly TError? _error;

    private Result(bool isSuccess, T? value, TError? error)
    {
        IsSuccess = isSuccess;
        _value = value;
        _error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("The result is a failure and has no value.");

    public TError Error => !IsSuccess
        ? _error!
        : throw new InvalidOperationException("The result is a success and has no error.");

    public static Result<T, TError> Ok(T value) => new(true, value, default);
    public static Result<T, TError> Fail(TError error) => new(false, default, error);

    public static implicit operator Result<T, TError>(T value) => Ok(value);
    public static implicit operator Result<T, TError>(TError error) => Fail(error);

    public TOut Match<TOut>(Func<T, TOut> ok, Func<TError, TOut> fail) =>
        IsSuccess ? ok(_value!) : fail(_error!);

    public Result<TOut, TError> Map<TOut>(Func<T, TOut> map) =>
        IsSuccess ? Result<TOut, TError>.Ok(map(_value!)) : Result<TOut, TError>.Fail(_error!);
}
