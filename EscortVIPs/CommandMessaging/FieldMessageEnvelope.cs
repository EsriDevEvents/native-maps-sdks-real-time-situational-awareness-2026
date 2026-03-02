using System.Text.Json;

namespace CommandMessaging;

public sealed class FieldMessageEnvelope
{
    public string MessageId { get; set; } = Guid.NewGuid().ToString("N");

    public string SessionId { get; set; } = string.Empty;

    public string DeviceId { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;

    public string? CorrelationId { get; set; }

    public JsonElement Payload { get; set; }
}
