using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using CommandMessaging;

namespace CommandDashboard.Services.Messaging;

public sealed class FieldMessagingHost : IAsyncDisposable
{
    private readonly HttpListener listener = new();
    private readonly ConcurrentDictionary<string, WebSocket> clients = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> sendGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> clientRoles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, VipState> vipStateByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, VipSpeedDirective> vipSpeedDirectiveByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly string sessionId;
    private readonly CancellationTokenSource shutdownCts = new();
    private Task? acceptLoopTask;

    public FieldMessagingHost(string sessionId, string endpointPrefix = "http://127.0.0.1:8765/ws/")
    {
        this.sessionId = sessionId;
        listener.Prefixes.Add(endpointPrefix);
    }

    public event EventHandler<FieldMessageReceivedEventArgs>? MessageReceived;
    public event EventHandler? ConnectionStateChanged;

    public async Task StartAsync()
    {
        if (listener.IsListening)
            return;

        listener.Start();
        acceptLoopTask = Task.Run(() => AcceptLoopAsync(shutdownCts.Token), shutdownCts.Token);

        OnConnectionStateChanged();
        return;
    }

    public async Task StopAsync()
    {
        shutdownCts.Cancel();

        if (listener.IsListening)
        {
            listener.Stop();
        }

        if (acceptLoopTask is not null)
        {
            await acceptLoopTask.ConfigureAwait(false);
        }

        foreach (var socket in clients.Values)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Host stopping", CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
            }

            socket.Dispose();
        }

        clients.Clear();
        foreach (var gate in sendGates.Values)
        {
            gate.Dispose();
        }

        sendGates.Clear();
        clientRoles.Clear();
        vipStateByDevice.Clear();
        vipSpeedDirectiveByDevice.Clear();
        OnConnectionStateChanged();
    }

    public Task<bool> SendVipStopAsync(string vipDeviceId, CancellationToken cancellationToken = default)
    {
        return SendVipControlSignalAsync(
            vipDeviceId,
            VipSpeedDirective.Stop,
            "STOP",
            "Hold position until escort catches up.",
            cancellationToken);
    }

    public Task<bool> SendVipHurryUpAsync(string vipDeviceId, CancellationToken cancellationToken = default)
    {
        return SendVipControlSignalAsync(
            vipDeviceId,
            VipSpeedDirective.Hurry,
            "HURRY",
            "Increase pace and rejoin escort.",
            cancellationToken);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (HttpListenerException)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                continue;
            }

            if (context is null)
                continue;

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(context, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleClientAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        WebSocket? socket = null;
        string? registeredDeviceId = null;

        try
        {
            var wsContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
            socket = wsContext.WebSocket;

            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var json = await ReadTextMessageAsync(socket, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                    continue;

                var envelope = MessageSerializer.DeserializeEnvelope(json);
                if (envelope is null)
                    continue;

                if (!string.IsNullOrWhiteSpace(envelope.DeviceId))
                {
                    registeredDeviceId = envelope.DeviceId;
                    clients[registeredDeviceId] = socket;
                }

                UpdateConnectionMetadata(envelope);
                await RelayIfNeededAsync(envelope, cancellationToken).ConfigureAwait(false);
                MessageReceived?.Invoke(this, new FieldMessageReceivedEventArgs(envelope));
            }
        }
        catch
        {
        }
        finally
        {
            if (registeredDeviceId is not null)
            {
                var shouldRemoveMapping = false;
                if (socket is not null && clients.TryGetValue(registeredDeviceId, out var mappedSocket))
                {
                    shouldRemoveMapping = ReferenceEquals(mappedSocket, socket);
                }

                if (shouldRemoveMapping)
                {
                    clients.TryRemove(registeredDeviceId, out _);
                    if (sendGates.TryRemove(registeredDeviceId, out var gate))
                    {
                        gate.Dispose();
                    }

                    clientRoles.TryRemove(registeredDeviceId, out _);
                    vipStateByDevice.TryRemove(registeredDeviceId, out _);
                    vipSpeedDirectiveByDevice.TryRemove(registeredDeviceId, out _);
                    OnConnectionStateChanged();
                }
            }

            if (socket is not null)
            {
                if (socket.State == WebSocketState.Open)
                {
                    try
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                socket.Dispose();
            }
        }
    }
    
    public (bool isRunning, int vipCount, int escortCount) GetConnectionState()
    {
        var vipCount = clientRoles.Values.Count(x => string.Equals(x, "VIP", StringComparison.OrdinalIgnoreCase));
        var escortCount = clientRoles.Values.Count(x => string.Equals(x, "Escort", StringComparison.OrdinalIgnoreCase));
        return (listener.IsListening, vipCount, escortCount);
    }

    public IReadOnlyCollection<string> GetConnectedDeviceIds()
    {
        return clients
            .Where(x => x.Value.State == WebSocketState.Open)
            .Select(x => x.Key)
            .ToArray();
    }

    private static async Task<string?> ReadTextMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4 * 1024];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            stream.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
                break;
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void UpdateConnectionMetadata(FieldMessageEnvelope envelope)
    {
        if (envelope.Type != MessageTypes.Register)
            return;

        var registerPayload = MessageSerializer.DeserializePayload<RegisterPayload>(envelope);
        if (registerPayload is null || string.IsNullOrWhiteSpace(envelope.DeviceId))
            return;

        clientRoles[envelope.DeviceId] = registerPayload.Role;
        OnConnectionStateChanged();
    }

    private async Task RelayIfNeededAsync(FieldMessageEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!clientRoles.TryGetValue(envelope.DeviceId, out var role))
            return;

        if (string.Equals(role, "Escort", StringComparison.OrdinalIgnoreCase)
            && envelope.Type == MessageTypes.EscortPosition)
        {
            await BroadcastToRoleAsync("VIP", envelope, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(role, "VIP", StringComparison.OrdinalIgnoreCase))
            return;

        if (envelope.Type == MessageTypes.LocationUpdate)
        {
            var payload = MessageSerializer.DeserializePayload<LocationUpdatePayload>(envelope);
            if (payload is null)
                return;

            var current = vipStateByDevice.GetOrAdd(envelope.DeviceId, _ => new VipState());
            current.Latitude = payload.Latitude;
            current.Longitude = payload.Longitude;
            if (payload.DistanceMeters.HasValue)
            {
                current.DistanceMeters = payload.DistanceMeters.Value;
            }

            if (!string.IsNullOrWhiteSpace(payload.DirectionToEscort))
            {
                current.DirectionToEscort = payload.DirectionToEscort;
            }

            var telemetryPayload = new VipTelemetryPayload(
                envelope.DeviceId,
                current.Latitude,
                current.Longitude,
                current.Status,
                current.DistanceMeters,
                current.DirectionToEscort);

            var relayEnvelope = MessageSerializer.CreateEnvelope(MessageTypes.VipTelemetry, sessionId, envelope.DeviceId, telemetryPayload);
            await BroadcastToRoleAsync("Escort", relayEnvelope, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (envelope.Type == MessageTypes.StatusUpdate)
        {
            var payload = MessageSerializer.DeserializePayload<StatusUpdatePayload>(envelope);
            if (payload is null)
                return;

            var current = vipStateByDevice.GetOrAdd(envelope.DeviceId, _ => new VipState());
            current.PreviousStatus = current.Status;
            current.Status = payload.Status;

            var telemetryPayload = new VipTelemetryPayload(
                envelope.DeviceId,
                current.Latitude,
                current.Longitude,
                current.Status,
                current.DistanceMeters,
                current.DirectionToEscort);

            var relayEnvelope = MessageSerializer.CreateEnvelope(MessageTypes.VipTelemetry, sessionId, envelope.DeviceId, telemetryPayload);
            await BroadcastToRoleAsync("Escort", relayEnvelope, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task BroadcastToRoleAsync(string role, FieldMessageEnvelope envelope, CancellationToken cancellationToken)
    {
        var targets = GetClientDeviceIdsByRole(role);

        foreach (var deviceId in targets)
        {
            await SendToDeviceAsync(deviceId, envelope, cancellationToken).ConfigureAwait(false);
        }
    }

    private List<string> GetClientDeviceIdsByRole(string role)
    {
        return clientRoles
            .Where(x => string.Equals(x.Value, role, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Key)
            .ToList();
    }

    private async Task<bool> SendToDeviceAsync(string deviceId, FieldMessageEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!clients.TryGetValue(deviceId, out var socket) || socket.State != WebSocketState.Open)
            return false;

        var sendGate = sendGates.GetOrAdd(deviceId, _ => new SemaphoreSlim(1, 1));
        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!clients.TryGetValue(deviceId, out socket) || socket.State != WebSocketState.Open)
                return false;

            var bytes = Encoding.UTF8.GetBytes(MessageSerializer.SerializeEnvelope(envelope));
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            sendGate.Release();
        }
    }

    private async Task<bool> SendVipControlSignalAsync(
        string vipDeviceId,
        VipSpeedDirective directive,
        string signal,
        string message,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(vipDeviceId))
            return false;

        if (directive == VipSpeedDirective.Normal)
        {
            vipSpeedDirectiveByDevice.TryRemove(vipDeviceId, out _);
        }
        else
        {
            vipSpeedDirectiveByDevice[vipDeviceId] = directive;
        }

        var payload = new VipControlPayload(
            vipDeviceId,
            signal,
            message,
            directive != VipSpeedDirective.Normal);
        var envelope = MessageSerializer.CreateEnvelope(MessageTypes.VipControl, sessionId, "dashboard", payload);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var sent = await SendToDeviceAsync(vipDeviceId, envelope, cancellationToken).ConfigureAwait(false);
            if (sent)
                return true;

            if (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    private sealed class VipState
    {
        public double Latitude { get; set; }

        public double Longitude { get; set; }

        public double? RouteDistanceMeters { get; set; }

        public string? PreviousStatus { get; set; } = "Unknown";

        public string Status { get; set; } = "Unknown";

        public double? DistanceMeters { get; set; }

        public string? DirectionToEscort { get; set; }
    }

    private enum VipSpeedDirective
    {
        Normal = 0,
        Stop = 1,
        Hurry = 2
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        listener.Close();
        shutdownCts.Dispose();
    }

    private void OnConnectionStateChanged()
    {
        ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
    }
}
