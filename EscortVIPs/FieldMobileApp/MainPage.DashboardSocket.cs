using System.Net.WebSockets;
using System.Text;
using CommandMessaging;
using FieldMobileApp.Location;

namespace FieldMobileApp;

public partial class MainPage
{
    private readonly SemaphoreSlim socketSendGate = new(1, 1);
    private double? latestEscortLatitude;
    private double? latestEscortLongitude;

    private async Task ReceiveLoopAsync(string role, string deviceId, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket is { State: WebSocketState.Open })
            {
                string? message;
                try
                {
                    message = await ReadTextMessageAsync(socket, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogDiagnostic($"ReceiveLoop read error: {ex.Message}");
                    if (socket is not { State: WebSocketState.Open })
                        break;

                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                    continue;
                }

                if (message is null)
                {
                    LogDiagnostic("ReceiveLoop close frame received.");
                    break;
                }

                if (string.IsNullOrWhiteSpace(message))
                    continue;

                FieldMessageEnvelope? envelope;
                try
                {
                    envelope = MessageSerializer.DeserializeEnvelope(message);
                }
                catch (Exception ex)
                {
                    LogDiagnostic($"ReceiveLoop deserialize error: {ex.Message}");
                    continue;
                }

                if (envelope is null)
                    continue;

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (role == "VIP" && envelope.Type == MessageTypes.EscortPosition)
                    {
                        var payload = MessageSerializer.DeserializePayload<EscortPositionPayload>(envelope);
                        if (payload is null)
                            return;

                        latestEscortLatitude = payload.Latitude;
                        latestEscortLongitude = payload.Longitude;

                        // If geotrigger startup had a transient failure, retry monitor initialization
                        // as soon as escort positions begin arriving.
                        _ = EnsurePerimeterMonitorsAsync(configuredRole);

                        _ = UpdateEscortFenceAsync(payload.Latitude, payload.Longitude);
                        return;
                    }

                    if (role == "VIP" && envelope.Type == MessageTypes.VipControl)
                    {
                        var payload = MessageSerializer.DeserializePayload<VipControlPayload>(envelope);
                        if (payload is null || !string.Equals(payload.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                            return;

                        UpdateVipCommandBanner(payload.Signal, payload.Message, payload.IsActive);
                        _ = RelayVipControlToSimulationEngineAsync(payload);
                        return;
                    }

                    if (role == "Escort" && envelope.Type == MessageTypes.VipTelemetry)
                    {
                        var payload = MessageSerializer.DeserializePayload<VipTelemetryPayload>(envelope);
                        if (payload is null)
                            return;

                        try
                        {
                            escortVipDynamicEntityDataSource.PushVipObservation(payload);
                        }
                        catch (Exception ex)
                        {
                            LogDiagnostic($"Skipping VIP telemetry publish because dynamic entity data source is unavailable: {ex.Message}");
                        }
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogDiagnostic($"ReceiveLoop fatal error: {ex}");
        }

        await HandleConnectionDropAsync("ReceiveLoop");
    }

    private async Task RelayRouteSnapshotToDashboardAsync(FieldMessageEnvelope envelope)
    {
        if (socket is null || socket.State != WebSocketState.Open)
            return;

        await SendAsync(envelope, receiveCts?.Token ?? CancellationToken.None);
    }

    private async Task RelayVipControlToDashboardAsync(FieldMessageEnvelope envelope)
    {
        if (socket is null || socket.State != WebSocketState.Open)
            return;

        await SendAsync(envelope, receiveCts?.Token ?? CancellationToken.None);
    }

    private void ApplySimulationAssignedLocationPayload(AssignedLocationPayload payload)
    {
        var deviceId = connectedDeviceId ?? configuredDeviceId;
        if (!string.Equals(payload.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            return;

        // SimulationEngine is the only authority for assigned positions in simulated mode.
        ActiveSimulationLocationDataSource?.SetAssignedLocation(payload.Latitude, payload.Longitude);

        if (!IsVipRole())
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(payload.DirectionToEscort))
        {
            displayedDirection = payload.DirectionToEscort;
        }

        displayedDistance = payload.DistanceMeters.HasValue
            ? $"{payload.DistanceMeters.Value,6:F1} m"
            : "  --.- m";
        UpdateEscortMetricsDetail();
    }

    private async Task SendLoopAsync(string role, string deviceId, string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            await SendRegisterAsync(role, deviceId, sessionId, cancellationToken);

            while (!cancellationToken.IsCancellationRequested && socket is { State: WebSocketState.Open })
            {
                // In simulation mode, SimulationEngine is authoritative for both escort and VIP positions.
                // This client relays assigned positions received from SimulationEngine instead of publishing local simulation points.
                if (simulatedMode)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    continue;
                }

                var location = GetLatestLocation();
                if (location is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    continue;
                }

                var latitude = location.Position.Y;
                var longitude = location.Position.X;

                if (string.Equals(role, "Escort", StringComparison.OrdinalIgnoreCase))
                {
                    var escortEnvelope = MessageSerializer.CreateEnvelope(
                        MessageTypes.EscortPosition,
                        sessionId,
                        deviceId,
                        new EscortPositionPayload(latitude, longitude));

                    try
                    {
                        var sent = await SendAsync(escortEnvelope, cancellationToken);
                        if (!sent)
                            throw new WebSocketException("Escort send skipped because websocket is not open.");

                        MarkOutboundActivity();
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        LogDiagnostic($"SendLoop escort send error: {ex.Message}");
                        if (socket is not { State: WebSocketState.Open })
                            break;

                        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                        continue;
                    }
                }
                else
                {
                    double? distanceMeters = null;
                    string? directionToEscort = null;
                    if (latestEscortLatitude.HasValue && latestEscortLongitude.HasValue)
                    {
                        distanceMeters = RuntimeGeoMath.CalculateDistanceMeters(
                            latitude,
                            longitude,
                            latestEscortLatitude.Value,
                            latestEscortLongitude.Value);

                        directionToEscort = RuntimeGeoMath.ComputeDirectionCardinal(
                            latitude,
                            longitude,
                            latestEscortLatitude.Value,
                            latestEscortLongitude.Value);
                    }

                    var locationEnvelope = MessageSerializer.CreateEnvelope(
                        MessageTypes.LocationUpdate,
                        sessionId,
                        deviceId,
                        new LocationUpdatePayload(latitude, longitude, distanceMeters, directionToEscort));

                    try
                    {
                        var sent = await SendAsync(locationEnvelope, cancellationToken);
                        if (!sent)
                            throw new WebSocketException("VIP send skipped because websocket is not open.");

                        MarkOutboundActivity();
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        LogDiagnostic($"SendLoop VIP send error: {ex.Message}");
                        if (socket is not { State: WebSocketState.Open })
                            break;

                        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                        continue;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogDiagnostic($"SendLoop fatal error: {ex}");
        }

        await HandleConnectionDropAsync("SendLoop");
    }

    private async Task SendRegisterAsync(string role, string deviceId, string sessionId, CancellationToken cancellationToken)
    {
        var registerEnvelope = MessageSerializer.CreateEnvelope(
            MessageTypes.Register,
            sessionId,
            deviceId,
            new RegisterPayload(deviceId, role, "field-app"));

        var sent = await SendAsync(registerEnvelope, cancellationToken);
        if (sent)
        {
            MarkOutboundActivity();
            return;
        }

        LogDiagnostic("Register send skipped because dashboard websocket is not open.");
    }

    private async Task RelayAssignedLocationToDashboardAsync(
        AssignedLocationPayload payload,
        string role,
        string deviceId,
        CancellationToken cancellationToken)
    {
        if (!simulatedMode)
            return;

        if (!string.Equals(payload.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            return;

        var sessionId = connectedSessionId ?? configuredSessionId;
        var senderDeviceId = connectedDeviceId ?? configuredDeviceId;

        if (string.Equals(role, "Escort", StringComparison.OrdinalIgnoreCase))
        {
            var escortEnvelope = MessageSerializer.CreateEnvelope(
                MessageTypes.EscortPosition,
                sessionId,
                senderDeviceId,
                new EscortPositionPayload(payload.Latitude, payload.Longitude));

            var escortSent = await SendAsync(escortEnvelope, cancellationToken);
            if (escortSent)
                MarkOutboundActivity();

            return;
        }

        if (!string.Equals(role, "VIP", StringComparison.OrdinalIgnoreCase))
            return;

        var locationEnvelope = MessageSerializer.CreateEnvelope(
            MessageTypes.LocationUpdate,
            sessionId,
            senderDeviceId,
            new LocationUpdatePayload(payload.Latitude, payload.Longitude, payload.DistanceMeters, payload.DirectionToEscort));

        var locationSent = await SendAsync(locationEnvelope, cancellationToken);

        if (!string.IsNullOrWhiteSpace(payload.Status))
        {
            var statusEnvelope = MessageSerializer.CreateEnvelope(
                MessageTypes.StatusUpdate,
                sessionId,
                senderDeviceId,
                new StatusUpdatePayload(payload.Status));

            var statusSent = await SendAsync(statusEnvelope, cancellationToken);
            if (locationSent || statusSent)
                MarkOutboundActivity();
            return;
        }

        if (locationSent)
            MarkOutboundActivity();
    }

    private void MarkOutboundActivity()
    {
        hasSentOutbound = true;
        lastOutboundUtc = DateTimeOffset.UtcNow;
    }

    private async Task<bool> SendAsync(FieldMessageEnvelope envelope, CancellationToken cancellationToken)
    {
        if (socket is null || socket.State != WebSocketState.Open)
            return false;

        await socketSendGate.WaitAsync(cancellationToken);
        try
        {
            if (socket is null || socket.State != WebSocketState.Open)
                return false;

            var json = MessageSerializer.SerializeEnvelope(envelope);
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            return true;
        }
        finally
        {
            socketSendGate.Release();
        }
    }

    private static async Task<string?> ReadTextMessageAsync(ClientWebSocket websocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4 * 1024];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await websocket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                break;
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
