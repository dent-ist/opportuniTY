namespace Opportunity.DataGenerator.Corpus;

/// <summary>Identity of the generation algorithm recorded in every corpus manifest.</summary>
public static class GeneratorInfo
{
    public const string Name = "Opportunity.DataGenerator";

    /// <summary>
    /// Bump whenever a change alters generated output for an unchanged (seed, profile): major for model changes,
    /// minor for new optional profile settings whose defaults keep output stable, patch for non-output changes.
    /// Corpus caches key on this value.
    /// </summary>
    public const string Version = "1.0.0";
}