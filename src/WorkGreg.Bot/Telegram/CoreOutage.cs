using System.Net;
using Polly.CircuitBreaker;
using Polly.Timeout;
using WorkGreg.Client;
using TelegramRequestException = global::Telegram.Bot.Exceptions.RequestException;

namespace WorkGreg.Telegram;

/// <summary>
/// Tells "the core cannot be reached" apart from every other failure, so the user gets "service temporarily
/// unavailable" instead of a generic error. Telegram's own network failures are explicitly not an outage of the core.
/// </summary>
internal static class CoreOutage
{
    public static bool IsOutage(Exception ex)
    {
        // Outermost first: a Telegram failure may wrap an HttpRequestException, and that is not the core's fault.
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case TelegramRequestException:
                    return false;

                case CoreApiException core:
                    return core.StatusCode is HttpStatusCode.BadGateway
                        or HttpStatusCode.ServiceUnavailable
                        or HttpStatusCode.GatewayTimeout;

                case HttpRequestException:        // connection refused, DNS, reset
                case TimeoutRejectedException:    // the resilience handler gave up waiting
                case BrokenCircuitException:      // the circuit breaker is open after repeated failures
                case TimeoutException:
                case TaskCanceledException:       // HttpClient timeout (callers only ask when shutdown was not requested)
                    return true;
            }
        }

        return false;
    }
}