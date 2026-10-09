using Npgsql;

using NpgsqlTypes;

using Opportunity.Application.Audit.Chain;

namespace Opportunity.Data.Audit;

/// <summary>
/// The SQL shared by the sealer and the verifier: both read rows in exactly the same text forms, so the canonical
/// envelope they hash is identical (<see cref="AuditChainFormat"/>).
/// </summary>
internal static class AuditChainSql
{
    /// <summary>Every hashed column, in <see cref="Read"/> order, then the stored chain columns.</summary>
    public const string EnvelopeColumns =
        """
        event_id, schema_version, workspace_id, occurred_at, recorded_at, category, action, actor_type, actor_id,
        actor_display, on_behalf_of, access_path, client_ip::text, user_agent, session_id_hash, resource_type,
        resource_id, outcome, reason_code, correlation_id, causation_id, job_id, chunk_sequence, snapshot_id,
        search_generation, details::text, restricted_details::text, sequence, prev_hash, event_hash, sealed_at
        """;

    /// <summary>The predicate of one chain (a separate form for the system chain, so both use the partial indexes).</summary>
    public static string ChainPredicate(Guid chainId) =>
        chainId == AuditChainFormat.SystemChainId ? "workspace_id IS NULL" : "workspace_id = @chain_ws";

    public static void AddChain(NpgsqlCommand command, Guid chainId)
    {
        if (chainId != AuditChainFormat.SystemChainId)
        {
            command.Parameters.Add(new NpgsqlParameter("chain_ws", NpgsqlDbType.Uuid) { Value = chainId });
        }
    }

    public static StoredChainRow Read(NpgsqlDataReader r)
    {
        var envelope = new AuditChainEnvelope
        {
            EventId = r.GetGuid(0),
            SchemaVersion = r.GetInt16(1),
            WorkspaceId = r.IsDBNull(2) ? null : r.GetGuid(2),
            OccurredAt = r.GetDateTime(3),
            RecordedAt = r.GetDateTime(4),
            Category = r.GetString(5),
            Action = r.GetString(6),
            ActorType = r.GetString(7),
            ActorId = r.GetString(8),
            ActorDisplay = r.GetString(9),
            OnBehalfOf = r.IsDBNull(10) ? null : r.GetGuid(10),
            AccessPath = r.GetString(11),
            ClientIp = r.IsDBNull(12) ? null : r.GetString(12),
            UserAgent = r.IsDBNull(13) ? null : r.GetString(13),
            SessionIdHash = r.IsDBNull(14) ? null : r.GetFieldValue<byte[]>(14),
            ResourceType = r.IsDBNull(15) ? null : r.GetString(15),
            ResourceId = r.IsDBNull(16) ? null : r.GetString(16),
            Outcome = r.GetString(17),
            ReasonCode = r.IsDBNull(18) ? null : r.GetString(18),
            CorrelationId = r.GetString(19),
            CausationId = r.IsDBNull(20) ? null : r.GetString(20),
            JobId = r.IsDBNull(21) ? null : r.GetGuid(21),
            ChunkSequence = r.IsDBNull(22) ? null : r.GetInt32(22),
            SnapshotId = r.IsDBNull(23) ? null : r.GetGuid(23),
            SearchGeneration = r.IsDBNull(24) ? null : r.GetInt64(24),
            Details = r.GetString(25),
            RestrictedDetails = r.IsDBNull(26) ? null : r.GetString(26),
            Sequence = r.IsDBNull(27) ? 0 : r.GetInt64(27),
            PrevHash = r.IsDBNull(28) ? AuditChainFormat.Genesis : r.GetFieldValue<byte[]>(28),
        };
        return new StoredChainRow(
            envelope,
            r.IsDBNull(27) ? null : r.GetInt64(27),
            r.IsDBNull(28) ? null : r.GetFieldValue<byte[]>(28),
            r.IsDBNull(29) ? null : r.GetFieldValue<byte[]>(29),
            r.IsDBNull(30) ? null : r.GetDateTime(30));
    }

    public const string CheckpointColumns =
        "chain_id, from_sequence, sequence, event_hash, merkle_root, reason, created_at, key_id, algorithm, signature";

    public static AuditCheckpoint ReadCheckpoint(NpgsqlDataReader r) => new(
        new AuditCheckpointData(
            r.GetGuid(0),
            r.GetInt64(1),
            r.GetInt64(2),
            r.GetFieldValue<byte[]>(3),
            r.GetFieldValue<byte[]>(4),
            Enum.Parse<AuditCheckpointReason>(r.GetString(5)),
            r.GetDateTime(6)),
        r.GetString(7),
        r.GetString(8),
        r.GetFieldValue<byte[]>(9));
}

/// <summary>A row as stored: the envelope plus the chain columns the sealer wrote (null while unsealed).</summary>
internal sealed record StoredChainRow(AuditChainEnvelope Envelope, long? Sequence, byte[]? PrevHash, byte[]? EventHash, DateTime? SealedAt);
