using System.Text.Json.Nodes;

using Opportunity.Application.Authorization;
using Opportunity.Application.Coding;
using Opportunity.Core.Fields;
using Opportunity.Core.SearchWork;
using Opportunity.Data.Fields;
using Opportunity.Data.SearchWork;

namespace Opportunity.Data.Security;

/// <summary>
/// Document-side security state maintained inside the caller's transaction (ADR-015 D6, §24 rule 1): restriction
/// classes derived from the stored coding rules (V0044 <c>restriction_class_rule</c>), wall coverage
/// (<c>opportunity.sync_document_walls</c>) and the security-lane re-projection of documents whose visibility changed.
/// </summary>
internal static class DocumentSecuritySql
{
    /// <summary>The stored class rules of the workspace as a binding, combined with <paramref name="configured"/> (if any).</summary>
    public static async Task<IRestrictionClassBinding> BindingAsync(
        WorkspaceTransaction tx, IRestrictionClassBinding? configured, CancellationToken cancellationToken)
    {
        var rules = new List<(string ClassKey, int FieldId, int ChoiceId)>();
        await using (var command = tx.Command(
            "SELECT class_key, field_id, choice_id FROM opportunity.restriction_class_rule WHERE workspace_id = @ws ORDER BY class_key, field_id, choice_id"))
        {
            command.Parameters.AddWithValue("ws", tx.WorkspaceId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rules.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)));
            }
        }

        var stored = new ChoiceRuleRestrictionClassBinding(rules);
        return configured is null or NoRestrictionClassBinding ? stored : new CombinedRestrictionClassBinding(stored, configured);
    }

    /// <summary>
    /// Brings <c>document_wall</c> in line with every wall's scope for <paramref name="documentIds"/> (null: the whole
    /// workspace) and returns the documents whose coverage changed. A workspace without wall scopes is left untouched
    /// (walls without a scope cover nothing; deleting a wall removes its coverage with it).
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> SyncWallsAsync(WorkspaceTransaction tx, Guid[]? documentIds, CancellationToken cancellationToken)
    {
        if (documentIds is { Length: 0 })
        {
            return [];
        }

        await using (var any = tx.Command("SELECT EXISTS (SELECT FROM opportunity.ethical_wall_scope WHERE workspace_id = @ws)"))
        {
            any.Parameters.AddWithValue("ws", tx.WorkspaceId);
            if (!(bool)(await any.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
            {
                return [];
            }
        }

        var catalog = await FieldCatalogRepository.LoadCatalogAsync(tx, tx.WorkspaceId, false, cancellationToken).ConfigureAwait(false);
        var custodianFields = CustodianFieldIds(catalog);
        var changed = new List<Guid>();
        await using var command = tx.Command("SELECT changed_document_id FROM opportunity.sync_document_walls(@ws, @docs, @fields) ORDER BY 1");
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.Add(new Npgsql.NpgsqlParameter("docs", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid)
        {
            Value = (object?)documentIds ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("fields", custodianFields);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            changed.Add(reader.GetGuid(0));
        }

        return changed;
    }

    /// <summary>
    /// The restriction classes and wall coverage of <paramref name="documentId"/> as this transaction sees them (the PDP's
    /// document attributes, <c>PostgresSecurityStateReader</c>); null when the document does not exist or is deleted.
    /// </summary>
    public static async Task<DocumentSecurityAttributes?> AttributesAsync(WorkspaceTransaction tx, Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = tx.Command(
            """
            SELECT ARRAY(SELECT r.class_key FROM opportunity.document_restriction r
                         WHERE r.workspace_id = d.workspace_id AND r.document_id = d.document_id),
                   ARRAY(SELECT dw.wall_id FROM opportunity.document_wall dw
                         WHERE dw.workspace_id = d.workspace_id AND dw.document_id = d.document_id)
            FROM opportunity.document d
            LEFT JOIN opportunity.document_projection_state s
                   ON s.workspace_id = d.workspace_id AND s.document_id = d.document_id
            WHERE d.workspace_id = @ws AND d.document_id = @doc AND s.is_deleted IS NOT TRUE
            """);
        command.Parameters.AddWithValue("ws", tx.WorkspaceId);
        command.Parameters.AddWithValue("doc", documentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new DocumentSecurityAttributes(reader.GetFieldValue<string[]>(0), reader.GetFieldValue<Guid[]>(1))
            : null;
    }

    /// <summary>The metadata fields whose values are custodians: the custodian field(s) and All Custodians.</summary>
    public static int[] CustodianFieldIds(FieldCatalog catalog) =>
        [.. catalog.Fields
            .Where(f => !f.IsDeleted && (f.FieldId == SystemFields.AllCustodians || SearchFieldExpansion.IsCustodian(f)))
            .Select(f => f.FieldId)
            .Order()];

    /// <summary>
    /// Re-derives the stored-rule class <paramref name="classKey"/> for every document of the workspace (after its rules
    /// changed or it was created); the coding adapter owns the query because it reads the coding tables.
    /// </summary>
    public static Task<IReadOnlyList<Guid>> ResyncClassAsync(WorkspaceTransaction tx, string classKey, CancellationToken cancellationToken) =>
        Coding.CodingRepository.ResyncRestrictionClassAsync(tx, classKey, cancellationToken);

    /// <summary>
    /// Bumps the projection version of <paramref name="documentIds"/> and writes their security-lane SearchOutbox rows
    /// (ADR-001 §5.3 L0, Q-10). Ends with the late-lock generation increment: call it last, then commit.
    /// </summary>
    public static async Task ReprojectAsync(WorkspaceTransaction tx, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken)
    {
        if (documentIds.Count == 0)
        {
            return;
        }

        var versions = new List<(Guid DocumentId, long DocumentVersion)>(documentIds.Count);
        await using (var bump = tx.Command(
            """
            UPDATE opportunity.document_projection_state s SET document_version = s.document_version + 1
            WHERE s.workspace_id = @ws AND s.document_id = ANY (@ids)
            RETURNING s.document_id, s.document_version
            """))
        {
            bump.Parameters.AddWithValue("ws", tx.WorkspaceId);
            bump.Parameters.AddWithValue("ids", documentIds.Distinct().Order().ToArray());
            await using var reader = await bump.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                versions.Add((reader.GetGuid(0), reader.GetInt64(1)));
            }
        }

        if (versions.Count > 0)
        {
            await SearchWorkSql.AddOutboxRowsAsync(tx, versions, SearchChangeMask.Security, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>A document carries a class while one of its coded choices is a rule of the class (V0044).</summary>
internal sealed class ChoiceRuleRestrictionClassBinding : IRestrictionClassBinding
{
    private readonly HashSet<string> _bound;
    private readonly Dictionary<(int FieldId, int ChoiceId), List<string>> _byChoice = [];

    public ChoiceRuleRestrictionClassBinding(IEnumerable<(string ClassKey, int FieldId, int ChoiceId)> rules)
    {
        _bound = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (classKey, fieldId, choiceId) in rules)
        {
            _bound.Add(classKey);
            if (!_byChoice.TryGetValue((fieldId, choiceId), out var classes))
            {
                _byChoice[(fieldId, choiceId)] = classes = [];
            }

            classes.Add(classKey);
        }
    }

    public IReadOnlySet<string> BoundClasses(FieldCatalog catalog) => _bound;

    public IReadOnlySet<string> Derive(FieldCatalog catalog, IReadOnlyDictionary<int, JsonNode> securityValues)
    {
        ArgumentNullException.ThrowIfNull(securityValues);
        var classes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (fieldId, value) in securityValues)
        {
            foreach (var choiceId in FieldValues.ChoiceIds(value))
            {
                if (_byChoice.TryGetValue((fieldId, choiceId), out var bound))
                {
                    classes.UnionWith(bound);
                }
            }
        }

        return classes;
    }
}

/// <summary>Two bindings side by side: a class bound by either is derived from both.</summary>
internal sealed class CombinedRestrictionClassBinding(IRestrictionClassBinding first, IRestrictionClassBinding second) : IRestrictionClassBinding
{
    public IReadOnlySet<string> BoundClasses(FieldCatalog catalog) =>
        first.BoundClasses(catalog).Union(second.BoundClasses(catalog), StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);

    public IReadOnlySet<string> Derive(FieldCatalog catalog, IReadOnlyDictionary<int, JsonNode> securityValues) =>
        first.Derive(catalog, securityValues).Union(second.Derive(catalog, securityValues), StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
}
