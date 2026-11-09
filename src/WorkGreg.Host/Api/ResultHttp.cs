using WorkGreg.Services;

namespace WorkGreg.Api;

/// <summary>The HTTP vocabulary for service results. One place decides which error is which status code.</summary>
public static class ResultHttp
{
    public static IResult ToHttp<T>(this Result<T, Error> result, Func<T, IResult> ok) =>
        result.IsSuccess ? ok(result.Value) : result.Error.ToHttp();

    public static IResult ToHttp(this Error error) => error.Kind switch
    {
        ErrorKind.NotFound => Results.NotFound(),
        ErrorKind.Conflict or ErrorKind.Busy => Results.Conflict(error.Message),
        _ => Results.BadRequest(error.Message)
    };
}