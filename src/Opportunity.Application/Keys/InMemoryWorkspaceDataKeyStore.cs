using Opportunity.Application.Audit;

namespace Opportunity.Application.Keys;

/// <summary>Keeps workspace data keys in memory, for tests and local diagnostics. Not a durable key store.</summary>
public sealed class InMemoryWorkspaceDataKeyStore(TimeProvider? time = null) : IWorkspaceDataKeyStore
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly List<WorkspaceDataKeyRecord> _rows = [];
    private readonly List<AuditEvent> _audit = [];

    /// <summary>Workspaces whose keys refuse destruction, as a preservation lock does in PostgreSQL.</summary>
    public HashSet<Guid> Held { get; } = [];

    public IReadOnlyList<AuditEvent> AuditEvents
    {
        get
        {
            lock (_gate)
            {
                return [.. _audit];
            }
        }
    }

    public Task<WorkspaceDataKeyRecord?> GetActiveAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.SingleOrDefault(r => r.WorkspaceId == workspaceId && r.State == WorkspaceDataKeyState.Active));
        }
    }

    public Task<WorkspaceDataKeyRecord?> GetAsync(Guid workspaceId, int version, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.SingleOrDefault(r => r.WorkspaceId == workspaceId && r.Version == version));
        }
    }

    public Task<IReadOnlyList<WorkspaceDataKeyRecord>> ListAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<WorkspaceDataKeyRecord>>([.. _rows.Where(r => r.WorkspaceId == workspaceId).OrderBy(r => r.Version)]);
        }
    }

    public Task<WorkspaceDataKeyRecord> CreateInitialAsync(Guid workspaceId, WrappedKey wrapped, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wrapped);
        lock (_gate)
        {
            if (!_rows.Any(r => r.WorkspaceId == workspaceId))
            {
                _rows.Add(Row(workspaceId, 1, wrapped));
                _audit.Add(audit);
            }

            return Task.FromResult(_rows.SingleOrDefault(r => r.WorkspaceId == workspaceId && r.State == WorkspaceDataKeyState.Active)
                ?? throw new KeyUnavailableException("The workspace's data keys were destroyed."));
        }
    }

    public Task<bool> RotateAsync(Guid workspaceId, int expectedActiveVersion, WrappedKey wrapped, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wrapped);
        lock (_gate)
        {
            var index = _rows.FindIndex(r => r.WorkspaceId == workspaceId && r.Version == expectedActiveVersion && r.State == WorkspaceDataKeyState.Active);
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            _rows[index] = _rows[index] with { State = WorkspaceDataKeyState.Retired };
            _rows.Add(Row(workspaceId, expectedActiveVersion + 1, wrapped));
            _audit.Add(audit);
            return Task.FromResult(true);
        }
    }

    public Task<bool> RewrapAsync(
        Guid workspaceId,
        int version,
        string expectedKekId,
        int expectedKekVersion,
        WrappedKey replacement,
        AuditEvent audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        lock (_gate)
        {
            var index = _rows.FindIndex(r => r.WorkspaceId == workspaceId && r.Version == version && r.State != WorkspaceDataKeyState.Destroyed
                && r.KekId == expectedKekId && r.KekVersion == expectedKekVersion);
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            _rows[index] = _rows[index] with
            {
                KekId = replacement.KekId,
                KekVersion = replacement.KekVersion,
                WrappedKey = replacement.Ciphertext,
                RewrappedAt = _time.GetUtcNow(),
            };
            _audit.Add(audit);
            return Task.FromResult(true);
        }
    }

    public Task<int> DestroyAllAsync(Guid workspaceId, AuditEvent audit, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (Held.Contains(workspaceId))
            {
                throw new Workspaces.PreservationLockedException(workspaceId, "workspace_data_key");
            }

            var count = 0;
            for (var i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].WorkspaceId == workspaceId && _rows[i].State != WorkspaceDataKeyState.Destroyed)
                {
                    _rows[i] = _rows[i] with { State = WorkspaceDataKeyState.Destroyed, WrappedKey = null, DestroyedAt = _time.GetUtcNow() };
                    count++;
                }
            }

            _audit.Add(audit);
            return Task.FromResult(count);
        }
    }

    public Task<IReadOnlyList<Guid>> ListWorkspaceIdsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Guid>>([.. _rows.Select(r => r.WorkspaceId).Distinct().Order()]);
        }
    }

    /// <summary>Replaces a stored row (tests that tamper with the store, e.g. move a wrapped key to another workspace).</summary>
    public void Replace(WorkspaceDataKeyRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (_gate)
        {
            var index = _rows.FindIndex(r => r.WorkspaceId == row.WorkspaceId && r.Version == row.Version);
            if (index < 0)
            {
                _rows.Add(row);
            }
            else
            {
                _rows[index] = row;
            }
        }
    }

    private WorkspaceDataKeyRecord Row(Guid workspaceId, int version, WrappedKey wrapped) =>
        new(workspaceId, version, WorkspaceDataKeyState.Active, wrapped.KekId, wrapped.KekVersion, wrapped.Ciphertext, _time.GetUtcNow(), null, null);
}
