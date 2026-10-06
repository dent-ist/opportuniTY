using Npgsql;

using NpgsqlTypes;

using Opportunity.Core.Documents;
using Opportunity.Core.Fields;

namespace Opportunity.Data.Relationships;

/// <summary>What one application of a dedupe policy found and changed.</summary>
internal sealed record DuplicateGroupingResult(
    long FamiliesCompared,
    long FamiliesWithoutHash,
    long FamiliesWithoutCustodian,
    long FamiliesWithUpstreamGroup,
    long Groups,
    long DocumentsGrouped,
    IReadOnlyList<Guid> Changed);

/// <summary>
/// Computed duplicate grouping inside the caller's transaction (E09-T04, Q-09, Q-63, ADR-009 R14-R16). Set-based over
/// the whole workspace, so memory holds only the group keys (one per group), never the documents:
/// <list type="number">
/// <item>Every live top-level parent (a standalone document is a family of one) whose own group is not an upstream group
/// gets its key: the policy's hash (lower-case hex) and, in Custodial scope, its custodian value.</item>
/// <item>Keys shared by two or more families become groups, <c>DuplicateGroupId = UUIDv5(workspace, kind[, custodian],
/// hash)</c> (<see cref="RelationshipIds"/>), so a re-run or re-import yields the same ids.</item>
/// <item>Every live document without an upstream group takes its family's group (attachments inherit the parent's, so a
/// group is a set of families) or none; only documents whose group changes are written, with a DocumentVersion bump.</item>
/// <item>The groups that gained or lost members are recounted and their primaries re-elected through
/// <see cref="RelationshipWriter"/> (earliest FamilyDate, then lowest ControlNumberSortKey, R15); empty ones are removed.</item>
/// </list>
/// Documents with an upstream group are never touched (Q-63); nothing is hidden or coded (labels only, R16). The caller
/// holds <see cref="FamilyWriter.LockAsync"/> so imports cannot change families mid-run.
/// </summary>
internal static class DuplicateGroupingWriter
{
    // Whole-workspace statements: no 30 s default (a large workspace takes longer); the job lease bounds the run.
    private const int CommandTimeoutSeconds = 3_600;

    private const string LiveDocuments =
        """
        opportunity.document d
        JOIN opportunity.document_projection_state s ON s.workspace_id = d.workspace_id AND s.document_id = d.document_id
        LEFT JOIN opportunity.duplicate_group g ON g.workspace_id = d.workspace_id AND g.duplicate_group_id = d.duplicate_group_id
        """;

    public static async Task<DuplicateGroupingResult> ApplyAsync(WorkspaceTransaction tx, DedupePolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(policy);
        var custodial = policy.Scope == DuplicateGroupScope.Custodial;
        if (custodial && policy.Enabled && policy.CustodianFieldId is null)
        {
            throw new ArgumentException("A custodial run needs its resolved custodian field.", nameof(policy));
        }

        // 1. Family keys. '' stands for "no custodian" so the joins below are plain equalities.
        var (kind, hash) = KeyExpressions(policy.HashSource);
        await ExecuteAsync(tx,
            $"""
            CREATE TEMP TABLE dedupe_parent (family_id uuid PRIMARY KEY, kind smallint, hash text, custodian text NOT NULL) ON COMMIT DROP;
            CREATE TEMP TABLE dedupe_key (kind smallint NOT NULL, hash text NOT NULL, custodian text NOT NULL, group_id uuid NOT NULL,
                                          PRIMARY KEY (kind, hash, custodian)) ON COMMIT DROP;
            CREATE TEMP TABLE dedupe_target (document_id uuid PRIMARY KEY, old_group uuid, new_group uuid) ON COMMIT DROP;
            INSERT INTO dedupe_parent (family_id, kind, hash, custodian)
            SELECT d.family_id, {kind}, {hash}, {(custodial ? Custodian : "''")}
            FROM {LiveDocuments}
            WHERE d.workspace_id = @ws AND d.family_sequence = 0 AND NOT s.is_deleted AND g.source IS DISTINCT FROM @upstream AND @enabled;
            ANALYZE dedupe_parent;
            """,
            c =>
            {
                c.Parameters.AddWithValue("enabled", policy.Enabled);
                c.Parameters.AddWithValue("custodian_key", CustodianKey(policy.CustodianFieldId));
            },
            cancellationToken).ConfigureAwait(false);

        // 2. Keys shared by two or more families: ids computed here (UUIDv5 needs SHA-1, which PostgreSQL lacks).
        var keys = new List<(short Kind, string Hash, string Custodian)>();
        await using (var read = Command(tx,
            """
            SELECT kind, hash, custodian FROM dedupe_parent
            WHERE hash IS NOT NULL AND (NOT @custodial OR custodian <> '')
            GROUP BY kind, hash, custodian HAVING count(*) > 1
            """))
        {
            read.Parameters.AddWithValue("custodial", custodial);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                keys.Add((reader.GetInt16(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        if (keys.Count > 0)
        {
            await using var importer = await tx.Connection.BeginBinaryImportAsync(
                "COPY dedupe_key (kind, hash, custodian, group_id) FROM STDIN (FORMAT BINARY)", cancellationToken).ConfigureAwait(false);
            foreach (var (k, h, c) in keys)
            {
                var hashKind = (DuplicateHashKind)k;
                await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(k, NpgsqlDbType.Smallint, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(h, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(c, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(custodial
                    ? RelationshipIds.CustodialDuplicateGroup(tx.WorkspaceId, hashKind, h, c)
                    : RelationshipIds.DuplicateGroup(tx.WorkspaceId, hashKind, h), NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
            }

            await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        // 3. Documents whose group changes; group rows exist before the documents reference them (deferred FK anyway).
        await ExecuteAsync(tx,
            $"""
            ANALYZE dedupe_key;
            INSERT INTO dedupe_target (document_id, old_group, new_group)
            SELECT d.document_id, d.duplicate_group_id, k.group_id
            FROM {LiveDocuments}
            LEFT JOIN dedupe_parent p ON p.family_id = d.family_id
            LEFT JOIN dedupe_key k ON k.kind = p.kind AND k.hash = p.hash AND k.custodian = p.custodian
            WHERE d.workspace_id = @ws AND NOT s.is_deleted AND (d.duplicate_group_id IS NULL OR g.source = @computed)
              AND d.duplicate_group_id IS DISTINCT FROM k.group_id;
            INSERT INTO opportunity.duplicate_group (workspace_id, duplicate_group_id, source, hash_kind, hash_value, scope, custodian)
            SELECT @ws, k.group_id, @computed, k.kind, k.hash, @scope, nullif(k.custodian, '')
            FROM dedupe_key k
            WHERE k.group_id IN (SELECT new_group FROM dedupe_target)
            ORDER BY k.group_id
            ON CONFLICT (workspace_id, duplicate_group_id) DO NOTHING;
            """,
            c => c.Parameters.AddWithValue("scope", (short)policy.Scope),
            cancellationToken).ConfigureAwait(false);

        var changed = new Dictionary<Guid, long>();
        await using (var update = Command(tx,
            """
            WITH changed AS (
                UPDATE opportunity.document d
                SET duplicate_group_id = t.new_group, is_duplicate_primary = d.is_duplicate_primary AND t.new_group IS NOT NULL, updated_at = now()
                FROM dedupe_target t
                WHERE d.workspace_id = @ws AND d.document_id = t.document_id
                RETURNING d.document_id)
            UPDATE opportunity.document_projection_state s SET document_version = s.document_version + 1
            FROM changed c
            WHERE s.workspace_id = @ws AND s.document_id = c.document_id
            RETURNING s.document_id
            """))
        {
            await using var reader = await update.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                changed[reader.GetGuid(0)] = 0;
            }
        }

        // 4. Recount and re-elect the groups that gained or lost members (old and new), drop the empty ones.
        var touched = new List<Guid>();
        await using (var groups = Command(tx,
            "SELECT old_group FROM dedupe_target WHERE old_group IS NOT NULL UNION SELECT new_group FROM dedupe_target WHERE new_group IS NOT NULL"))
        {
            await using var reader = await groups.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                touched.Add(reader.GetGuid(0));
            }
        }

        foreach (var (documentId, _) in await RelationshipWriter.RecomputeDuplicateGroupsAsync(tx, [.. touched], cancellationToken).ConfigureAwait(false))
        {
            changed[documentId] = 0;
        }

        // 5. What the workspace looks like now.
        await using var stats = Command(tx,
            $"""
            SELECT (SELECT count(*) FROM dedupe_parent WHERE hash IS NOT NULL AND (NOT @custodial OR custodian <> '')),
                   (SELECT count(*) FROM dedupe_parent WHERE hash IS NULL),
                   (SELECT count(*) FROM dedupe_parent WHERE hash IS NOT NULL AND @custodial AND custodian = ''),
                   (SELECT count(*) FROM {LiveDocuments}
                    WHERE d.workspace_id = @ws AND d.family_sequence = 0 AND NOT s.is_deleted AND g.source = @upstream),
                   (SELECT count(*) FROM dedupe_key),
                   (SELECT count(*) FROM {LiveDocuments}
                    WHERE d.workspace_id = @ws AND NOT s.is_deleted AND g.source = @computed)
            """);
        stats.Parameters.AddWithValue("custodial", custodial);
        await using var r = await stats.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await r.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new DuplicateGroupingResult(
            r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), [.. changed.Keys.Order()]);
    }

    /// <summary>The key a parent is compared on: (hash kind, lower-case hex hash), null hash when it has none.</summary>
    private static (string Kind, string Hash) KeyExpressions(DedupeHashSource source) => source switch
    {
        DedupeHashSource.Sha256 => ($"{(short)DuplicateHashKind.Sha256Native}::smallint", "encode(d.sha256, 'hex')"),
        DedupeHashSource.Md5 => ($"{(short)DuplicateHashKind.Md5}::smallint", "encode(d.md5, 'hex')"),
        DedupeHashSource.Sha1 => ($"{(short)DuplicateHashKind.Sha1}::smallint", "encode(d.sha1, 'hex')"),
        DedupeHashSource.UpstreamHash => ("d.upstream_dedupe_hash_kind", "d.upstream_dedupe_hash"),
        // Q-09 / R14: the upstream dedupe (else email) hash when present, otherwise SHA-256 of the native.
        DedupeHashSource.Auto => (
            $"CASE WHEN d.upstream_dedupe_hash IS NOT NULL THEN d.upstream_dedupe_hash_kind ELSE {(short)DuplicateHashKind.Sha256Native}::smallint END",
            "coalesce(d.upstream_dedupe_hash, encode(d.sha256, 'hex'))"),
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    // A single-value Keyword/Text value (a JSON string), trimmed, control characters replaced, within the 255 limit.
    private const string Custodian =
        """
        coalesce(left(nullif(btrim(regexp_replace(
            CASE WHEN jsonb_typeof(d.metadata -> @custodian_key) = 'string' THEN d.metadata ->> @custodian_key END,
            '[[:cntrl:]]', ' ', 'g')), ''), 255), '')
        """;

    private static string CustodianKey(int? fieldId) => fieldId is { } id ? FieldKey.For(id) : "-";

    private static NpgsqlCommand Command(WorkspaceTransaction tx, string sql)
    {
        var command = tx.Command(sql);
        command.CommandTimeout = CommandTimeoutSeconds;
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("upstream", (short)DuplicateGroupSource.Upstream);
        command.Parameters.AddWithValue("computed", (short)DuplicateGroupSource.Computed);
        return command;
    }

    private static async Task ExecuteAsync(WorkspaceTransaction tx, string sql, Action<NpgsqlCommand> bind, CancellationToken cancellationToken)
    {
        await using var command = Command(tx, sql);
        bind(command);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
