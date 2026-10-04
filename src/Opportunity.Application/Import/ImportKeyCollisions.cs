using Opportunity.Contracts.Import;

namespace Opportunity.Application.Import;

/// <summary>What the import mode does with a row, given whether its overlay key names an existing document (E08-T07).</summary>
public enum ImportKeyDecision
{
    /// <summary>Append or Append-Overlay, key not in the workspace: the row creates a document.</summary>
    Create,

    /// <summary>Overlay or Append-Overlay, key in the workspace: the row updates that document.</summary>
    Overlay,

    /// <summary>Append, key already in the workspace: row error <see cref="ImportKeyRules.KeyExists"/>.</summary>
    KeyExists,

    /// <summary>Overlay, key not in the workspace: row error <see cref="ImportKeyRules.KeyMissing"/>.</summary>
    KeyMissing,
}

/// <summary>
/// The key-collision rules of the import modes (E08-T07), shared by pre-flight validation (E08-T06, through
/// <see cref="ImportKeyCollisionChecker"/>) and the import chunk itself, so both report the same rows with the same codes:
/// Append refuses a key that exists (<c>KEY_EXISTS</c>), Overlay refuses a key that does not (<c>KEY_MISSING</c>),
/// Append-Overlay creates or overlays.
/// </summary>
public static class ImportKeyRules
{
    /// <summary>Append: a document with the row's key already exists.</summary>
    public const string KeyExists = "KEY_EXISTS";

    /// <summary>Overlay: no document has the row's key.</summary>
    public const string KeyMissing = "KEY_MISSING";

    public static ImportKeyDecision Decide(ImportMode mode, bool exists) => (mode, exists) switch
    {
        (ImportMode.Append, true) => ImportKeyDecision.KeyExists,
        (ImportMode.Overlay, false) => ImportKeyDecision.KeyMissing,
        (_, true) => ImportKeyDecision.Overlay,
        (_, false) => ImportKeyDecision.Create,
    };

    /// <summary>The row error of a refused key; null when the row is loaded.</summary>
    public static ImportRowIssue? Issue(ImportKeyDecision decision, string? key, string keyLabel = "control number") => decision switch
    {
        ImportKeyDecision.KeyExists => new ImportRowIssue(ImportIssueSeverity.Error, KeyExists,
            $"A document with {keyLabel} {key} already exists; Append loads new documents only."),
        ImportKeyDecision.KeyMissing => new ImportRowIssue(ImportIssueSeverity.Error, KeyMissing,
            $"No document with {keyLabel} {key} exists to overlay."),
        _ => null,
    };
}

/// <summary>A row's overlay key: as written in the load file and normalized (prefix applied, ADR-003 normalization).</summary>
public sealed record ImportKeyProbe(long RowNo, string? Key, string KeyNorm);

/// <summary>A row the mode refuses (<see cref="ImportKeyRules.KeyExists"/> or <see cref="ImportKeyRules.KeyMissing"/>).</summary>
public sealed record ImportKeyCollision(long RowNo, string? Key, ImportKeyDecision Decision, ImportRowIssue Issue);

/// <summary>Counts of one or more <see cref="ImportKeyCollisionChecker.CheckAsync"/> calls (pre-flight "will create / will update / already exists / not found").</summary>
public sealed record ImportKeyCheck(long WillCreate, long WillUpdate, long AlreadyExists, long NotFound, IReadOnlyList<ImportKeyCollision> Collisions)
{
    public static ImportKeyCheck Empty { get; } = new(0, 0, 0, 0, []);

    public ImportKeyCheck Add(ImportKeyCheck other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(WillCreate + other.WillCreate, WillUpdate + other.WillUpdate, AlreadyExists + other.AlreadyExists, NotFound + other.NotFound,
            [.. Collisions, .. other.Collisions]);
    }
}

/// <summary>
/// Checks overlay keys against the workspace without writing anything, for pre-flight validation (E08-T06): feed it the
/// keys of a bounded batch of rows at a time and add up the results. Duplicate keys inside the file are the caller's
/// concern (the first occurrence is the one loaded).
/// </summary>
public sealed class ImportKeyCollisionChecker(IImportBatchStore batches)
{
    /// <summary>Most keys looked up per database round trip.</summary>
    public const int LookupBatchSize = 1_000;

    public async Task<ImportKeyCheck> CheckAsync(
        Guid workspaceId, ImportMode mode, IReadOnlyList<ImportKeyProbe> probes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probes);
        long create = 0, update = 0, exists = 0, missing = 0;
        var collisions = new List<ImportKeyCollision>();
        foreach (var page in probes.Chunk(LookupBatchSize))
        {
            var found = await batches.FindDocumentIdsAsync(
                workspaceId, [.. page.Select(p => p.KeyNorm).Distinct(StringComparer.Ordinal)], cancellationToken).ConfigureAwait(false);
            foreach (var probe in page)
            {
                var decision = ImportKeyRules.Decide(mode, found.ContainsKey(probe.KeyNorm));
                switch (decision)
                {
                    case ImportKeyDecision.Create:
                        create++;
                        break;
                    case ImportKeyDecision.Overlay:
                        update++;
                        break;
                    case ImportKeyDecision.KeyExists:
                        exists++;
                        break;
                    case ImportKeyDecision.KeyMissing:
                        missing++;
                        break;
                }

                if (ImportKeyRules.Issue(decision, probe.Key) is { } issue)
                {
                    collisions.Add(new ImportKeyCollision(probe.RowNo, probe.Key, decision, issue));
                }
            }
        }

        return new ImportKeyCheck(create, update, exists, missing, collisions);
    }
}
