using System.Reflection;

namespace Opportunity.Contracts.Messaging;

/// <summary>A payload CLR type and the contract it implements.</summary>
public sealed record MessageContractDescriptor(Type PayloadType, string MessageType, SchemaVersion Version)
{
    public static MessageContractDescriptor For(Type payloadType)
    {
        ArgumentNullException.ThrowIfNull(payloadType);
        var attribute = payloadType.GetCustomAttribute<MessageContractAttribute>()
            ?? throw new ArgumentException($"{payloadType} has no [MessageContract] attribute.", nameof(payloadType));
        return new MessageContractDescriptor(payloadType, attribute.MessageType, attribute.Version);
    }
}

/// <summary>How a reader supports one (messageType, major) pair.</summary>
public sealed record SupportedMessageContract(
    MessageContractDescriptor Wire,
    MessageContractDescriptor Current,
    Func<object, object>? Upgrade);

/// <summary>
/// The message contracts a process can write and read. Each <c>messageType</c> has one current contract (major N,
/// written by producers) and optionally its previous major N-1 with an upgrade to N, so consumers accept N and N-1
/// (ADR-019 §3.5). Any other major is unsupported and goes to parking. Build at startup; not thread-safe for writes.
/// </summary>
public sealed class MessageTypeRegistry
{
    private readonly Dictionary<Type, MessageContractDescriptor> _byClrType = [];
    private readonly Dictionary<string, MessageContractDescriptor> _current = new(StringComparer.Ordinal);
    private readonly Dictionary<(string MessageType, int Major), SupportedMessageContract> _readable = [];

    /// <summary>Current contracts (the ones producers write).</summary>
    public IReadOnlyCollection<MessageContractDescriptor> Current => _current.Values;

    /// <summary>Every readable (messageType, major), current and previous.</summary>
    public IReadOnlyCollection<SupportedMessageContract> Readable => _readable.Values;

    /// <summary>Registers <typeparamref name="TPayload"/> as the current contract of its <c>messageType</c>.</summary>
    public MessageTypeRegistry Register<TPayload>()
        where TPayload : class
    {
        var descriptor = MessageContractDescriptor.For(typeof(TPayload));
        if (_current.TryGetValue(descriptor.MessageType, out var existing))
        {
            throw new InvalidOperationException(
                $"'{descriptor.MessageType}' already has the current contract {existing.PayloadType} ({existing.Version}).");
        }

        _byClrType.Add(descriptor.PayloadType, descriptor);
        _current.Add(descriptor.MessageType, descriptor);
        _readable.Add((descriptor.MessageType, descriptor.Version.Major), new SupportedMessageContract(descriptor, descriptor, null));
        return this;
    }

    /// <summary>
    /// Registers <typeparamref name="TPrevious"/> as major N-1 of the current <typeparamref name="TCurrent"/>, read and
    /// upgraded with <paramref name="upgrade"/>. Register the current contract first.
    /// </summary>
    public MessageTypeRegistry RegisterPrevious<TPrevious, TCurrent>(Func<TPrevious, TCurrent> upgrade)
        where TPrevious : class
        where TCurrent : class
    {
        ArgumentNullException.ThrowIfNull(upgrade);
        var previous = MessageContractDescriptor.For(typeof(TPrevious));
        var current = Describe<TCurrent>();
        if (!_current.TryGetValue(current.MessageType, out var registered) || registered != current)
        {
            throw new InvalidOperationException($"{typeof(TCurrent)} is not the current contract of '{current.MessageType}'.");
        }

        if (previous.MessageType != current.MessageType || previous.Version.Major != current.Version.Major - 1)
        {
            throw new InvalidOperationException(
                $"{typeof(TPrevious)} ({previous.MessageType} {previous.Version}) is not major N-1 of " +
                $"{current.MessageType} {current.Version}.");
        }

        _byClrType.Add(previous.PayloadType, previous);
        _readable.Add(
            (previous.MessageType, previous.Version.Major),
            new SupportedMessageContract(previous, current, payload => upgrade((TPrevious)payload)));
        return this;
    }

    public MessageContractDescriptor Describe(Type payloadType) =>
        _byClrType.TryGetValue(payloadType, out var descriptor)
            ? descriptor
            : throw new InvalidOperationException($"{payloadType} is not a registered message contract.");

    public MessageContractDescriptor Describe<TPayload>() => Describe(typeof(TPayload));

    public bool IsKnown(string messageType) => _current.ContainsKey(messageType);

    /// <summary>Resolves the reader for <paramref name="messageType"/> at <paramref name="version"/>; newer minors are accepted.</summary>
    public bool TryResolve(string messageType, SchemaVersion version, out SupportedMessageContract contract) =>
        _readable.TryGetValue((messageType, version.Major), out contract!);
}
