using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Opportunity.Application.Workspaces.Deletion;

/// <summary>
/// The destruction certificate of a workspace deletion (ADR-014 §8): who asked, who approved, when it ran, what every
/// store held before and after, what was kept and what the platform cannot purge (residuals). Names, identifiers,
/// dates and counts only — never document content or metadata values. The bytes are canonical (fixed property order,
/// no whitespace), so a rebuilt certificate of the same run hashes the same.
/// </summary>
public static class DestructionCertificate
{
    public const int FormatVersion = 1;
    public const string MediaType = "application/json";

    /// <summary>The certificate document and its canonical UTF-8 bytes with their SHA-256.</summary>
    public sealed record Built(JsonObject Document, byte[] Bytes, byte[] Sha256)
    {
        public string Sha256Hex => Convert.ToHexStringLower(Sha256);
    }

    /// <param name="deletion">The run being certified (Running, with every step but the certification done).</param>
    /// <param name="steps">The run's step records; Inventory holds the counts before, Verification the counts after.</param>
    /// <param name="residuals">Verification still found data in some store.</param>
    public static Built Build(
        WorkspaceDeletion deletion, IReadOnlyList<WorkspaceDeletionStepRecord> steps, bool residuals, DateTimeOffset issuedAt,
        WorkspaceDeletionOptions options)
    {
        ArgumentNullException.ThrowIfNull(deletion);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(options);
        var byStep = steps.ToDictionary(s => s.Step);
        var before = byStep.GetValueOrDefault(DeletionStep.Inventory)?.Counts ?? [];
        var after = byStep.GetValueOrDefault(DeletionStep.Verification)?.Counts ?? [];
        var purgeAll = deletion.RetentionProfile == DeletionRetentionProfile.PurgeAll;
        var keyStep = byStep.GetValueOrDefault(DeletionStep.KeyDestruction)?.Counts;
        var keysDestroyed = purgeAll && Long(keyStep?["destroyed"]) > 0;

        var document = new JsonObject
        {
            ["type"] = "opportuniTY workspace destruction certificate",
            ["formatVersion"] = FormatVersion,
            ["certificateId"] = deletion.DeletionId.ToString("D"),
            ["issuedAt"] = Time(issuedAt),
            ["outcome"] = residuals ? nameof(WorkspaceDeletionStatus.CompletedWithResiduals) : nameof(WorkspaceDeletionStatus.Completed),
            ["workspace"] = new JsonObject
            {
                ["id"] = deletion.WorkspaceId.ToString("D"),
                ["name"] = deletion.WorkspaceName,
                ["matterNumber"] = deletion.MatterNumber,
            },
            ["retentionProfile"] = deletion.RetentionProfile.ToString(),
            ["request"] = new JsonObject
            {
                ["requestedBy"] = Person(deletion.RequestedBy, deletion.RequestedByName),
                ["requestedAt"] = Time(deletion.RequestedAt),
                ["reason"] = deletion.Reason,
                ["externalReference"] = deletion.ExternalReference,
            },
            ["approval"] = new JsonObject
            {
                ["approvedBy"] = deletion.ApprovedBy is { } approver ? Person(approver, deletion.ApprovedByName) : null,
                ["approvedAt"] = deletion.ApprovedAt is { } approvedAt ? Time(approvedAt) : null,
                ["note"] = deletion.ApprovalNote,
                ["runNotBefore"] = deletion.RunNotBefore is { } notBefore ? Time(notBefore) : null,
            },
            ["run"] = new JsonObject
            {
                ["startedAt"] = deletion.StartedAt is { } started ? Time(started) : null,
                ["finishedAt"] = Time(issuedAt),
                ["fenceEpoch"] = deletion.FenceEpoch,
                ["steps"] = new JsonArray([.. steps.OrderBy(s => s.Step).Select(s => (JsonNode)new JsonObject
                {
                    ["step"] = s.Step.ToString(),
                    ["attempt"] = s.Attempt,
                    ["startedAt"] = Time(s.StartedAt),
                    ["finishedAt"] = s.FinishedAt is { } f ? Time(f) : null,
                    ["outcome"] = s.Outcome,
                })]),
            },
            ["stores"] = new JsonArray(
                PostgresStore(before, after),
                SearchStore(before, after, byStep.GetValueOrDefault(DeletionStep.SearchPurge)?.Counts),
                ObjectStore(before, after, byStep.GetValueOrDefault(DeletionStep.StoragePurge)?.Counts, purgeAll),
                KeyStore(before, after, keyStep, purgeAll)),
            ["residuals"] = Residuals(deletion, after, byStep.GetValueOrDefault(DeletionStep.StoragePurge)?.Counts, residuals, keysDestroyed, issuedAt, options),
            ["softwareVersion"] = SoftwareVersion(),
        };

        var bytes = Encoding.UTF8.GetBytes(document.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        return new Built(document, bytes, SHA256.HashData(bytes));
    }

    private static JsonObject PostgresStore(JsonObject before, JsonObject after)
    {
        var tables = new JsonObject();
        long totalBefore = 0, totalAfter = 0, retained = 0;
        var beforeTables = before["postgres"]?.AsObject() ?? [];
        var afterTables = after["postgres"]?.AsObject() ?? [];
        foreach (var name in beforeTables.Select(p => p.Key).Union(afterTables.Select(p => p.Key)).Order(StringComparer.Ordinal))
        {
            var b = Long(beforeTables[name]?["total"]);
            var a = Long(afterTables[name]?["total"]);
            var r = Long(afterTables[name]?["retained"]);
            if (b == 0 && a == 0)
            {
                continue;
            }

            totalBefore += b;
            totalAfter += a;
            retained += r;
            tables[name] = new JsonObject { ["before"] = b, ["after"] = a, ["retained"] = r };
        }

        return new JsonObject
        {
            ["store"] = "PostgreSQL",
            ["unit"] = "rows",
            ["before"] = totalBefore,
            ["after"] = totalAfter,
            ["retained"] = retained,
            ["notPurged"] = "the workspace registry row (tombstone), its legal hold records, its data key records and its audit trail",
            ["tables"] = tables,
        };
    }

    private static JsonObject SearchStore(JsonObject before, JsonObject after, JsonObject? purge) => new()
    {
        ["store"] = "OpenSearch",
        ["unit"] = "documents",
        ["before"] = Long(before["openSearch"]?["documents"]),
        ["after"] = Long(after["openSearch"]?["documents"]),
        ["dedicatedIndexesBefore"] = Long(before["openSearch"]?["dedicatedIndexes"]),
        ["dedicatedIndexesAfter"] = Long(after["openSearch"]?["dedicatedIndexes"]),
        ["documentsDeleted"] = Long(purge?["documentsDeleted"]) + Long(after["openSearch"]?["secondPassDocumentsDeleted"]),
        ["indexesDeleted"] = Long(purge?["indexesDeleted"]) + Long(after["openSearch"]?["secondPassIndexesDeleted"]),
        ["passes"] = 2,
    };

    private static JsonObject ObjectStore(JsonObject before, JsonObject after, JsonObject? purge, bool purgeAll) => new()
    {
        ["store"] = "ObjectStorage",
        ["unit"] = "objects",
        ["before"] = Long(before["objects"]?["objects"]),
        ["bytesBefore"] = Long(before["objects"]?["bytes"]),
        ["after"] = Long(after["objects"]?["objects"]),
        ["retained"] = Long(after["objects"]?["retainedObjects"]),
        ["objectsDeleted"] = Long(purge?["objects"]),
        ["bytesDeleted"] = Long(purge?["bytes"]),
        ["areasPurged"] = purge?["areas"]?.DeepClone() ?? new JsonArray(),
        ["areasRetained"] = purgeAll ? new JsonArray() : new JsonArray("productions"),
    };

    private static JsonObject KeyStore(JsonObject before, JsonObject after, JsonObject? keys, bool purgeAll) => new()
    {
        ["store"] = "Keys",
        ["unit"] = "dataKeyVersions",
        ["before"] = Long(before["keys"]?["total"]),
        ["destroyed"] = Long(keys?["destroyed"]),
        ["usableAfter"] = Long(after["keys"]?["usable"]),
        ["dedicatedKekDestroyed"] = keys?["dedicatedKekDestroyed"]?.ToJsonString() == "true",
        ["status"] = purgeAll ? "Destroyed" : "RetainedForRetainedRecords",
    };

    private static JsonArray Residuals(
        WorkspaceDeletion deletion, JsonObject after, JsonObject? storage, bool residuals, bool keysDestroyed, DateTimeOffset finishedAt,
        WorkspaceDeletionOptions options)
    {
        var backupsExpireBy = finishedAt + options.BackupRetention;
        var list = new JsonArray
        {
            new JsonObject
            {
                ["kind"] = "Backups",
                ["description"] = keysDestroyed
                    ? "Database backups, point-in-time recovery archives and object storage copies taken before the deletion may still hold the workspace's data until they expire; its encrypted objects in them are unreadable because the workspace's data keys were destroyed."
                    : "Database backups, point-in-time recovery archives and object storage copies taken before the deletion may still hold the workspace's data, readable, until they expire.",
                ["expiresBy"] = Time(backupsExpireBy),
            },
            new JsonObject
            {
                ["kind"] = "OffSiteCopies",
                ["description"] = "Copies the operator keeps outside this installation (off-site disaster recovery, Q-40) are not reached by the deletion; the operator's retention schedule applies.",
            },
            new JsonObject
            {
                ["kind"] = "DeliveredProductions",
                ["description"] = "Productions and exports already delivered to other parties are outside the platform.",
            },
            new JsonObject
            {
                ["kind"] = "AuditTrail",
                ["description"] = "The workspace's audit trail, including the record of this deletion, is kept for the audit retention period after the matter closed (Q-16).",
            },
        };

        if (deletion.RetentionProfile == DeletionRetentionProfile.RetainRecords)
        {
            list.Add(new JsonObject
            {
                ["kind"] = "RetainedRecords",
                ["description"] = "Kept by the RetainRecords profile: productions with their members, Bates ranges and volume outputs, the snapshots they were made from, and the jobs and Redaction Sets they name. A production can still be downloaded but no longer re-run; deleting these records needs a separate approved request.",
                ["rows"] = after["postgres"]?.AsObject().Sum(p => Long(p.Value?["retained"])) ?? 0,
                ["objects"] = Long(after["objects"]?["retainedObjects"]),
            });
        }

        if (storage?["providerResiduals"] is JsonArray provider && provider.Count > 0)
        {
            list.Add(new JsonObject
            {
                ["kind"] = "StorageProviderCopies",
                ["description"] = "Copies the storage provider reported it could not remove (non-current versions, soft-deleted blobs); they expire under the provider's own retention.",
                ["count"] = provider.Count,
            });
        }

        if (residuals)
        {
            list.Add(new JsonObject
            {
                ["kind"] = "VerificationResiduals",
                ["description"] = "The final verification still counted data of the workspace in at least one store (see the stores' after counts); an operator must investigate.",
            });
        }

        list.Add(new JsonObject
        {
            ["kind"] = "Messaging",
            ["description"] = "Queued or dead-lettered messages naming the workspace are discarded by the workers' fences; they are not counted.",
        });

        return list;
    }

    private static JsonObject Person(Guid id, string? name) => new() { ["userId"] = id.ToString("D"), ["displayName"] = name };

    private static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static long Long(JsonNode? node) =>
        node is JsonValue value && long.TryParse(value.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static string SoftwareVersion() =>
        typeof(DestructionCertificate).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(DestructionCertificate).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
