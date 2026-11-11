namespace WorkGreg.Services;

/// <summary>
/// Which HTTP status stands for which <see cref="ErrorKind"/>. The core's endpoints answer with it and the bot's HTTP
/// client reads it, so the two can never disagree. Every kind has its own status; the body
/// ({ kind, code, message, args }) says exactly which error it is.
/// </summary>
public static class ErrorHttp
{
    public const int BadRequest = 400;
    public const int NotFound = 404;
    public const int Conflict = 409;

    /// <summary>
    /// Something else is running (a check). 423 Locked and not 409, so a client can tell "busy, try later" from
    /// "already exists" by the status alone.
    /// </summary>
    public const int Busy = 423;

    public static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.NotFound => NotFound,
        ErrorKind.Conflict => Conflict,
        ErrorKind.Busy => Busy,
        _ => BadRequest
    };

    /// <summary>True for a status the core uses to carry an error body (as opposed to 401, 403, 5xx...).</summary>
    public static bool IsErrorStatus(int status) => status is BadRequest or NotFound or Conflict or Busy;
}
