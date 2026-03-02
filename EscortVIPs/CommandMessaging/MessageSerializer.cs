using System.Text.Json;

namespace CommandMessaging;

public static class MessageSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string SerializeEnvelope(FieldMessageEnvelope envelope)
    {
        return JsonSerializer.Serialize(envelope, SerializerOptions);
    }

    public static FieldMessageEnvelope? DeserializeEnvelope(string json)
    {
        return JsonSerializer.Deserialize<FieldMessageEnvelope>(json, SerializerOptions);
    }

    public static FieldMessageEnvelope CreateEnvelope<TPayload>(
        string type,
        string sessionId,
        string deviceId,
        TPayload payload,
        string? correlationId = null)
    {
        return new FieldMessageEnvelope
        {
            Type = type,
            SessionId = sessionId,
            DeviceId = deviceId,
            CorrelationId = correlationId,
            Payload = JsonSerializer.SerializeToElement(payload, SerializerOptions)
        };
    }

    public static TPayload? DeserializePayload<TPayload>(FieldMessageEnvelope envelope)
    {
        if (envelope.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return default;

        return envelope.Payload.Deserialize<TPayload>(SerializerOptions);
    }
}
