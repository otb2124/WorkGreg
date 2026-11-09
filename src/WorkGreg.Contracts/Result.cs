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
/// A failure. <see cref="Message"/> is plain text, never HTML and never a slash-command hint:
/// the adapter decides how to present it.
/// </summary>
public sealed record Error(ErrorKind Kind, string Message)
{
    public static Error Validation(string message) => new(ErrorKind.Validation, message);
    public static Error NotFound(string message) => new(ErrorKind.NotFound, message);
    public static Error Conflict(string message) => new(ErrorKind.Conflict, message);
    public static Error Busy(string message) => new(ErrorKind.Busy, message);
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