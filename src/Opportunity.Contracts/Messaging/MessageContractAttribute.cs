namespace Opportunity.Contracts.Messaging;

/// <summary>
/// Declares a payload type as a message contract: its stable dotted <c>messageType</c> (no version) and the schema
/// version it implements (ADR-019 §3.2). A new major is a new CLR type with the same <c>messageType</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class MessageContractAttribute(string messageType, int major, int minor) : Attribute
{
    public string MessageType { get; } = messageType;

    public int Major { get; } = major;

    public int Minor { get; } = minor;

    public SchemaVersion Version => new(Major, Minor);
}
