namespace Opportunity.Application.Storage;

/// <summary>
/// Presign limits. ADR-015 D12.3 supersedes ADR-011 §5.2 on the GET TTL: 60 s default, configurable 30–300 s. Only
/// natives and export/production packages may be presigned for GET (ADR-015 D12.3, stricter than ADR-011 §5.4, which
/// also allowed page images); only import upload staging may be presigned for PUT, at most 900 s (ADR-011 §5.7).
/// </summary>
public sealed class PresignPolicy
{
    public static readonly TimeSpan HardMinGetTtl = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan HardMaxGetTtl = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan HardMaxPutTtl = TimeSpan.FromSeconds(900);

    public PresignPolicy(TimeSpan? defaultGetTtl = null, TimeSpan? maxGetTtl = null, TimeSpan? defaultPutTtl = null)
    {
        MaxGetTtl = maxGetTtl ?? HardMaxGetTtl;
        DefaultGetTtl = defaultGetTtl ?? TimeSpan.FromSeconds(60);
        DefaultPutTtl = defaultPutTtl ?? TimeSpan.FromSeconds(300);
        if (MaxGetTtl < HardMinGetTtl || MaxGetTtl > HardMaxGetTtl)
        {
            throw new ArgumentOutOfRangeException(nameof(maxGetTtl), "The maximum GET TTL must be between 30 and 300 seconds.");
        }

        if (DefaultGetTtl < HardMinGetTtl || DefaultGetTtl > MaxGetTtl)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultGetTtl), "The default GET TTL must be between 30 seconds and the maximum.");
        }

        if (DefaultPutTtl <= TimeSpan.Zero || DefaultPutTtl > HardMaxPutTtl)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultPutTtl), "The default PUT TTL must be positive and at most 900 seconds.");
        }
    }

    public static PresignPolicy Default { get; } = new();

    public TimeSpan DefaultGetTtl { get; }

    public TimeSpan MaxGetTtl { get; }

    public TimeSpan DefaultPutTtl { get; }

    public static bool MayPresignGet(ObjectKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Area is ObjectArea.Native or ObjectArea.Export or ObjectArea.Production;
    }

    public TimeSpan ResolveGetTtl(ObjectKey key, TimeSpan? requested)
    {
        if (!MayPresignGet(key))
        {
            throw new ArgumentException($"Objects in area {key.Area} are streamed through the gateway, never presigned.", nameof(key));
        }

        var ttl = requested ?? DefaultGetTtl;
        if (ttl < HardMinGetTtl || ttl > MaxGetTtl)
        {
            throw new ArgumentOutOfRangeException(nameof(requested), $"A presigned GET TTL must be between {HardMinGetTtl.TotalSeconds:0} and {MaxGetTtl.TotalSeconds:0} seconds.");
        }

        return ttl;
    }

    public TimeSpan ResolvePutTtl(ObjectKey key, TimeSpan? requested)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Area != ObjectArea.ImportUpload)
        {
            throw new ArgumentException("Only import upload-staging keys may receive a presigned PUT.", nameof(key));
        }

        var ttl = requested ?? DefaultPutTtl;
        if (ttl <= TimeSpan.Zero || ttl > HardMaxPutTtl)
        {
            throw new ArgumentOutOfRangeException(nameof(requested), "A presigned PUT TTL must be positive and at most 900 seconds.");
        }

        return ttl;
    }
}
