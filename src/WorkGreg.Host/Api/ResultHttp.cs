using WorkGreg.Services;

namespace WorkGreg.Api;

/// <summary>The HTTP vocabulary for service results. One place decides which error is which status code.</summary>
public static class ResultHttp
{
    public static IResult ToHttp<T>(this Result<T, Error> result, Func<T, IResult> ok) =>
        result.IsSuccess ? ok(result.Value) : result.Error.ToHttp();

    /// <summary>
    /// The status code says the class of failure (see <see cref="ErrorHttp"/>), the body says exactly which:
    /// <c>{ "kind": "Busy", "code": "check.already_running", "message": "...", "args": { ... } }</c> (kind is the
    /// <see cref="ErrorKind"/> name; args is left out when there are none). Every failed call that reaches a service
    /// has this body; 401 and 403 from the filters have none.
    /// </summary>
    public static IResult ToHttp(this Error error) => Results.Json(error, statusCode: ErrorHttp.StatusFor(error.Kind));
}