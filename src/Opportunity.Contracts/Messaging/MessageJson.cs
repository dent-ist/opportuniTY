using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Opportunity.Contracts.Messaging;

/// <summary>
/// JSON rules for message payloads (ADR-019 §2.9, §3.4): camelCase names, enums as camelCase strings (an unknown value
/// fails the read, so the message is dead-lettered with a reason), unknown members ignored, C# <c>required</c> members
/// enforced, no number-from-string coercion.
/// </summary>
public static class MessageJson
{
    public static JsonSerializerOptions PayloadOptions { get; } = CreatePayloadOptions();

    private static JsonSerializerOptions CreatePayloadOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly();
        return options;
    }
}
