using Microsoft.Playwright;
using WorkGreg.Domain;

namespace WorkGreg.Applying;

/// <param name="Request">What to fill with (read-only).</param>
/// <param name="Matcher">Already built from the request's values and rules.</param>
/// <param name="Logger">Log labels, keys, counts and ids only. Never an entered value.</param>
/// <param name="Ct">Cancelled when the run times out or is stopped.</param>
public sealed record FillContext(FillRequest Request, FieldMatcher Matcher, ILogger Logger, CancellationToken Ct);

/// <summary>
/// Knows one ATS's form. The page is already open and loaded when <see cref="FillAsync"/> is called; the adapter fills it,
/// takes the screenshot and returns the report. It must never submit. Adapters that need nothing special can call
/// <see cref="FormFillEngine.RunAsync"/>.
/// </summary>
public interface IAtsAdapter
{
    bool Matches(Uri url);
    Task<FillReport> FillAsync(IPage page, FillContext ctx);
}
