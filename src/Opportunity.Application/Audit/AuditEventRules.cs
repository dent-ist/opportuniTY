using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Opportunity.Application.Audit;

/// <summary>
/// Envelope rules of ADR-013 §4, checked before an event reaches the store (which enforces the same limits with
/// constraints). A rejected event is a programming error: the action it records must not proceed without it.
/// </summary>
public static class AuditEventRules
{
    public const int MaxDetailsBytes = 8 * 1024;
    public const int MaxRestrictedDetailsBytes = 64 * 1024;
    public const int MaxUserAgentLength = 512;
    public const int MaxActorIdLength = 256;
    public const int MaxActorDisplayLength = 512;
    public const int MaxResourceTypeLength = 64;
    public const int MaxResourceIdLength = 256;
    public const int MaxReasonCodeLength = 128;
    public const int MaxCorrelationIdLength = 128;
    public const int SessionIdHashLength = 32;

    private static readonly JsonSerializerOptions RelaxedJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Returns the violations of <paramref name="auditEvent"/>; empty when it can be stored.</summary>
    public static IReadOnlyList<string> Validate(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        var e = auditEvent;
        var errors = new List<string>();

        if (e.EventId == Guid.Empty)
        {
            errors.Add("EventId is required.");
        }

        if (e.SchemaVersion < 1)
        {
            errors.Add("SchemaVersion must be at least 1.");
        }

        if (e.WorkspaceId == Guid.Empty)
        {
            errors.Add("WorkspaceId is null for installation-level events, never the empty GUID.");
        }

        if (!AuditTaxonomy.IsDefined(e.Category, e.Action))
        {
            errors.Add($"'{e.Category}.{e.Action}' is not in the ADR-013 taxonomy.");
        }

        Length(errors, nameof(e.ActorId), e.ActorId, 1, MaxActorIdLength);
        Length(errors, nameof(e.ActorDisplay), e.ActorDisplay, 1, MaxActorDisplayLength);
        Length(errors, nameof(e.ResourceType), e.ResourceType, 1, MaxResourceTypeLength);
        Length(errors, nameof(e.ResourceId), e.ResourceId, 1, MaxResourceIdLength);
        Length(errors, nameof(e.CorrelationId), e.CorrelationId, 1, MaxCorrelationIdLength);
        Length(errors, nameof(e.CausationId), e.CausationId, 1, MaxCorrelationIdLength);
        if (e.Outcome != AuditOutcome.Success && string.IsNullOrEmpty(e.ReasonCode))
        {
            errors.Add($"ReasonCode is required for {e.Outcome}.");
        }
        else
        {
            Length(errors, nameof(e.ReasonCode), e.ReasonCode, 1, MaxReasonCodeLength);
        }

        if (e.SessionIdHash is { Length: not SessionIdHashLength })
        {
            errors.Add("SessionIdHash is an HMAC-SHA-256 (32 bytes); never the raw session ID.");
        }

        if (e.ChunkSequence is not null && e.JobId is null)
        {
            errors.Add("ChunkSequence needs JobId.");
        }

        if (JsonBytes(e.Details) > MaxDetailsBytes)
        {
            errors.Add($"Details exceed {MaxDetailsBytes} bytes; audit carries IDs and enums only.");
        }

        if (e.RestrictedDetails is not null)
        {
            if (e.Category != AuditTaxonomy.SearchCategory)
            {
                errors.Add("RestrictedDetails are allowed for Search.* events only (Q-16).");
            }
            else if (JsonBytes(e.RestrictedDetails) > MaxRestrictedDetailsBytes)
            {
                errors.Add($"RestrictedDetails exceed {MaxRestrictedDetailsBytes} bytes.");
            }
        }

        return errors;
    }

    /// <summary>Throws <see cref="ArgumentException"/> listing every violation.</summary>
    public static void EnsureValid(AuditEvent auditEvent)
    {
        var errors = Validate(auditEvent);
        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid audit event: " + string.Join(" ", errors), nameof(auditEvent));
        }
    }

    /// <summary>Normalizes what the writer may fix rather than reject: user agent truncation (ADR-013 §4).</summary>
    public static AuditEvent Normalize(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        return auditEvent.UserAgent is { Length: > MaxUserAgentLength } userAgent
            ? auditEvent with { UserAgent = userAgent[..MaxUserAgentLength] }
            : auditEvent;
    }

    /// <summary>Size of <paramref name="values"/> as PostgreSQL prints the jsonb (", " and ": " separators).</summary>
    public static int JsonBytes(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(values, RelaxedJson)) + Math.Max(0, (2 * values.Count) - 1);
    }

    private static void Length(List<string> errors, string name, string? value, int min, int max)
    {
        if (value is not null && (value.Length < min || value.Length > max))
        {
            errors.Add($"{name} must have {min} to {max} characters.");
        }
    }
}
