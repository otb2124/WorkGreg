using WorkGreg.Domain;

namespace WorkGreg.Filtering;

/// <summary>
/// Compatibility facade over <see cref="ProfileCriteria"/> for callers that still pass a profile per call.
/// New code should build a <see cref="ProfileCriteria"/> once per profile. Delete this file when nothing uses it.
/// </summary>
public static class FilterEngine
{
    public static bool Matches(Job job, SearchProfile profile) => ProfileCriteria.For(profile).Matches(job);

    public static List<string> Explain(Job job, SearchProfile profile) => ProfileCriteria.For(profile).Explain(job);

    public static bool IsRemote(Job job) => RemoteCriterion.LooksRemote(job);
}