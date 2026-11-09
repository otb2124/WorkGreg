using WorkGreg.Domain;

namespace WorkGreg.Filtering;

/// <summary>
/// One rule a vacancy has to satisfy (specification pattern). The test and its explanation live on the same class,
/// so "why it matched" can never describe a different rule than the one that decided.
/// </summary>
public interface IJobCriterion
{
    bool Matches(Job job);

    /// <summary>
    /// Plain-text line saying how the job satisfied this criterion (the caller HTML-encodes it).
    /// Meant for jobs where <see cref="Matches"/> is true.
    /// </summary>
    string Explain(Job job);
}