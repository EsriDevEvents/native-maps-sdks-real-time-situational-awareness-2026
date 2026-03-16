using CommandMessaging;

namespace CommandDashboard.Services.Messaging;

public sealed class FieldMessageReceivedEventArgs : EventArgs
{
    public FieldMessageReceivedEventArgs(FieldMessageEnvelope envelope)
    {
        Envelope = envelope;
    }

    public FieldMessageEnvelope Envelope { get; }
}
