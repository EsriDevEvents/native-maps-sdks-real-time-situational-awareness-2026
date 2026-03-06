using System.Net.WebSockets;
using System.Text;
using CommandMessaging;
using Esri.ArcGISRuntime.Location;
using FieldMobileApp.Location;

namespace FieldMobileApp;

public partial class MainPage
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1.5);
    private readonly SemaphoreSlim socketSendGate = new(1, 1);
    private readonly SemaphoreSlim simulationConnectGate = new(1, 1);
    private readonly SemaphoreSlim vipControlRelayGate = new(1, 1);

    private ClientWebSocket? socket;
    private ClientWebSocket? simulationSocket;
    private CancellationTokenSource? receiveCts;
    private Task? receiveTask;
    private Task? simulationReceiveTask;
    private Task? simulationConnectTask;
    private Task? sendTask;
    private CancellationTokenSource? watchdogCts;
    private Task? watchdogTask;
    private bool reconnectLoopActive;
    private bool simulationReconnectLoopActive;
    private bool handlingConnectionDrop;
    private string? connectedDeviceId;
    private string? connectedSessionId;
    private double? latestEscortLatitude;
    private double? latestEscortLongitude;
    private DateTimeOffset lastInboundUtc;
    private DateTimeOffset lastOutboundUtc;
    private bool hasSentOutbound;
    private VipControlPayload? pendingVipControlPayload;

    private SimulationLocationDataSource? ActiveSimulationLocationDataSource => locationDataSource as SimulationLocationDataSource;

    private async Task<bool> ConnectAsync()
    {
        if (isShuttingDown)
            return false;

        if (socket is { State: WebSocketState.Open })
            return true;

        var role = configuredRole;
        var deviceId = configuredDeviceId;
        var sessionId = configuredSessionId;
        var url = configuredHostUrl;

        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        try
        {
            socket = new ClientWebSocket();
            receiveCts = new CancellationTokenSource();

            await EnsureEscortVipDynamicEntityDataSourceAsync(role);
            await StartLocationDataSourceAsync();

            await socket.ConnectAsync(new Uri(url), receiveCts.Token);

            var registerEnvelope = MessageSerializer.CreateEnvelope(
                MessageTypes.Register,
                sessionId,
                deviceId,
                new RegisterPayload(deviceId, role, "field-app"));

            var registerSent = await SendAsync(registerEnvelope, receiveCts.Token);
            if (!registerSent)
                throw new InvalidOperationException("Failed to send register envelope because websocket is not open.");
            connectedDeviceId = deviceId;
            connectedSessionId = sessionId;
            vipStatus = null;
            UpdateVipCommandBanner(null, null, false);
            displayedDistance = "  --.- m";
            displayedDirection = "-";
            lastRenderedDetailText = null;
            UpdateEscortMetricsDetail();
            lastInboundUtc = DateTimeOffset.UtcNow;
            lastOutboundUtc = DateTimeOffset.UtcNow;
            hasSentOutbound = false;
            LogDiagnostic($"Connected socket role={role} device={deviceId}");

            await EnsurePerimeterMonitorsAsync(role);
            ApplyStatusVisual(null, role == "Escort" ? "Monitoring VIP statuses" : "Waiting for perimeter state");

            receiveTask = Task.Run(() => ReceiveLoopAsync(role, deviceId, receiveCts.Token), receiveCts.Token);
            simulationReceiveTask = null;
            simulationConnectTask = null;
            sendTask = Task.Run(() => SendLoopAsync(role, deviceId, sessionId, receiveCts.Token), receiveCts.Token);

            if (simulatedMode)
            {
                StartSimulationReconnectLoop(role, deviceId, sessionId, receiveCts.Token);
            }

            watchdogCts?.Cancel();
            watchdogCts?.Dispose();
            watchdogCts = new CancellationTokenSource();
            watchdogTask = Task.Run(() => WatchdogLoopAsync(role, watchdogCts.Token), watchdogCts.Token);
            return true;
        }
        catch (Exception ex)
        {
            LogDiagnostic($"ConnectAsync failed: {ex}");
            ApplyStatusVisual(null, "Connection failed");
            await DisconnectAsync();
            return false;
        }
    }

    private async void OnPageLoaded(object? sender, EventArgs e)
    {
        await EnsureConnectedLoopAsync();
    }

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

                lastInboundUtc = DateTimeOffset.UtcNow;

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (envelope.Type == MessageTypes.AssignedLocation)
                    {
                        var assigned = MessageSerializer.DeserializePayload<AssignedLocationPayload>(envelope);
                        if (assigned is not null
                            && string.Equals(assigned.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                        {
                            ActiveSimulationLocationDataSource?.SetAssignedLocation(assigned.Latitude, assigned.Longitude);
                        }
                    }

                    if (role == "VIP" && envelope.Type == MessageTypes.AssignedLocation)
                    {
                        var payload = MessageSerializer.DeserializePayload<AssignedLocationPayload>(envelope);
                        if (payload is null || !string.Equals(payload.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                            return;
                        ApplyAssignedLocationPayload(payload, role, deviceId);
                        return;
                    }

                    if (role == "VIP" && envelope.Type == MessageTypes.EscortPosition)
                    {
                        var payload = MessageSerializer.DeserializePayload<EscortPositionPayload>(envelope);
                        if (payload is null)
                            return;

                        latestEscortLatitude = payload.Latitude;
                        latestEscortLongitude = payload.Longitude;
                        _ = UpdateEscortFenceAsync(payload.Latitude, payload.Longitude);
                        return;
                    }

                    if (role == "VIP" && envelope.Type == MessageTypes.VipTelemetry)
                    {
                        var payload = MessageSerializer.DeserializePayload<VipTelemetryPayload>(envelope);
                        if (payload is null || !string.Equals(payload.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                            return;
                        return;
                    }

                    if (role == "VIP" && envelope.Type == MessageTypes.VipControl)
                    {
                        var payload = MessageSerializer.DeserializePayload<VipControlPayload>(envelope);
                        if (payload is null || !string.Equals(payload.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                            return;

                        UpdateVipCommandBanner(payload.Signal, payload.Message, payload.IsActive);
                        pendingVipControlPayload = payload;
                        _ = RelayVipControlToSimulationEngineAsync(payload);
                        return;
                    }

                    if (role == "Escort" && envelope.Type == MessageTypes.VipTelemetry)
                    {
                        var payload = MessageSerializer.DeserializePayload<VipTelemetryPayload>(envelope);
                        if (payload is null)
                            return;

                        escortVipDynamicEntityDataSource.PublishVipTelemetry(payload);
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

    private async Task ConnectSimulationAsync(string role, string deviceId, string sessionId, CancellationToken cancellationToken)
    {
        await DisconnectSimulationSocketAsync();
        simulationSocket = new ClientWebSocket();
        await simulationSocket.ConnectAsync(new Uri(configuredSimulationUrl), cancellationToken);

        var registerEnvelope = MessageSerializer.CreateEnvelope(
            MessageTypes.Register,
            sessionId,
            deviceId,
            new RegisterPayload(deviceId, role, "field-app"));

        var registerSent = await SendSimulationAsync(registerEnvelope, cancellationToken);
        if (!registerSent)
            throw new InvalidOperationException("Failed to register with simulation engine because websocket is not open.");
    }

    private void StartSimulationReconnectLoop(string role, string deviceId, string sessionId, CancellationToken cancellationToken)
    {
        if (!simulatedMode || isShuttingDown)
            return;

        if (simulationConnectTask is { IsCompleted: false })
            return;

        simulationConnectTask = Task.Run(
            () => EnsureSimulationConnectedAsync(role, deviceId, sessionId, cancellationToken),
            cancellationToken);
    }

    private async Task EnsureSimulationConnectedAsync(string role, string deviceId, string sessionId, CancellationToken cancellationToken)
    {
        if (!simulatedMode || isShuttingDown)
            return;

        if (simulationSocket is { State: WebSocketState.Open } && simulationReceiveTask is { IsCompleted: false })
            return;

        if (simulationReconnectLoopActive)
            return;

        simulationReconnectLoopActive = true;
        try
        {
            while (!isShuttingDown && !cancellationToken.IsCancellationRequested)
            {
                if (simulationSocket is { State: WebSocketState.Open } && simulationReceiveTask is { IsCompleted: false })
                    return;

                await simulationConnectGate.WaitAsync(cancellationToken);
                try
                {
                    if (simulationSocket is { State: WebSocketState.Open } && simulationReceiveTask is { IsCompleted: false })
                        return;

                    try
                    {
                        await ConnectSimulationAsync(role, deviceId, sessionId, cancellationToken);
                        simulationReceiveTask = Task.Run(() => SimulationReceiveLoopAsync(role, deviceId, cancellationToken), cancellationToken);
                        LogDiagnostic("Simulation socket connected.");
                        _ = FlushPendingVipControlToSimulationAsync();
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        LogDiagnostic($"Simulation connect attempt failed: {ex.Message}");
                    }
                }
                finally
                {
                    simulationConnectGate.Release();
                }

                await Task.Delay(ReconnectDelay, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            simulationReconnectLoopActive = false;
        }
    }

    private async Task SimulationReceiveLoopAsync(string role, string deviceId, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && simulationSocket is { State: WebSocketState.Open })
            {
                var message = await ReadTextMessageAsync(simulationSocket, cancellationToken);
                if (message is null)
                    break;

                if (string.IsNullOrWhiteSpace(message))
                    continue;

                var envelope = MessageSerializer.DeserializeEnvelope(message);
                if (envelope is null)
                    continue;

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (envelope.Type == MessageTypes.RouteSnapshot)
                    {
                        _ = RelayRouteSnapshotToDashboardAsync(envelope);
                        return;
                    }

                    if (envelope.Type == MessageTypes.AssignedLocation)
                    {
                        var payload = MessageSerializer.DeserializePayload<AssignedLocationPayload>(envelope);
                        if (payload is null || !string.Equals(payload.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                            return;
                        ApplyAssignedLocationPayload(payload, role, deviceId);
                        return;
                    }

                    if (role == "VIP" && envelope.Type == MessageTypes.VipControl)
                    {
                        var payload = MessageSerializer.DeserializePayload<VipControlPayload>(envelope);
                        if (payload is null || !string.Equals(payload.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                            return;

                        UpdateVipCommandBanner(payload.Signal, payload.Message, payload.IsActive);
                        _ = RelayVipControlToDashboardAsync(envelope);
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogDiagnostic($"SimulationReceiveLoop fatal error: {ex}");
        }
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

    private void ApplyAssignedLocationPayload(AssignedLocationPayload payload, string role, string deviceId)
    {
        ActiveSimulationLocationDataSource?.SetAssignedLocation(payload.Latitude, payload.Longitude);

        if (!string.Equals(role, "VIP", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(payload.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
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

    private async Task RelayVipControlToSimulationEngineAsync(VipControlPayload payload)
    {
        pendingVipControlPayload = payload;
        await FlushPendingVipControlToSimulationAsync();
    }

    private async Task FlushPendingVipControlToSimulationAsync()
    {
        if (!simulatedMode)
            return;

        await vipControlRelayGate.WaitAsync();
        try
        {
            var payload = pendingVipControlPayload;
            if (payload is null)
                return;

        var sessionId = connectedSessionId ?? configuredSessionId;
        var deviceId = connectedDeviceId ?? configuredDeviceId;
        var cancellationToken = receiveCts?.Token ?? CancellationToken.None;

        if (simulationSocket is null || simulationSocket.State != WebSocketState.Open)
        {
            try
            {
                await EnsureSimulationConnectedAsync(configuredRole, deviceId, sessionId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogDiagnostic($"RelayVipControlToSimulationEngineAsync reconnect failed: {ex.Message}");
            }
        }

        if (simulationSocket is null || simulationSocket.State != WebSocketState.Open)
        {
            LogDiagnostic("RelayVipControlToSimulationEngineAsync skipped because simulation websocket is not open.");
            return;
        }

        var envelope = MessageSerializer.CreateEnvelope(
            MessageTypes.VipControl,
            sessionId,
            deviceId,
            payload);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var sent = await SendSimulationAsync(envelope, cancellationToken);
            if (sent)
            {
                if (Equals(pendingVipControlPayload, payload))
                    pendingVipControlPayload = null;
                return;
            }

            if (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken);
            }
        }

        LogDiagnostic("RelayVipControlToSimulationEngineAsync failed after retries.");
        }
        finally
        {
            vipControlRelayGate.Release();
        }
    }

    private async Task SendLoopAsync(string role, string deviceId, string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket is { State: WebSocketState.Open })
            {
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

                        hasSentOutbound = true;
                        lastOutboundUtc = DateTimeOffset.UtcNow;
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

                        hasSentOutbound = true;
                        lastOutboundUtc = DateTimeOffset.UtcNow;
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

    private async Task<bool> SendSimulationAsync(FieldMessageEnvelope envelope, CancellationToken cancellationToken)
    {
        if (simulationSocket is null || simulationSocket.State != WebSocketState.Open)
            return false;

        await socketSendGate.WaitAsync(cancellationToken);
        try
        {
            if (simulationSocket is null || simulationSocket.State != WebSocketState.Open)
                return false;

            var json = MessageSerializer.SerializeEnvelope(envelope);
            var bytes = Encoding.UTF8.GetBytes(json);
            await simulationSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
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

    private async Task DisconnectAsync()
    {
        if (receiveCts is not null)
        {
            receiveCts.Cancel();
            receiveCts.Dispose();
            receiveCts = null;
        }

        if (watchdogCts is not null)
        {
            watchdogCts.Cancel();
            watchdogCts.Dispose();
            watchdogCts = null;
        }

        receiveTask = null;
        simulationReceiveTask = null;
        simulationConnectTask = null;
        sendTask = null;
        watchdogTask = null;

        if (escortPerimeterMonitor is not null)
        {
            escortPerimeterMonitor.Stop();
            escortPerimeterMonitor = null;
        }

        if (warningRingMonitor is not null)
        {
            warningRingMonitor.Stop();
            warningRingMonitor = null;
        }

        escortPerimeterFence = null;
        escortPerimeterFenceTable = null;
        vipInsideEscortPerimeter = null;
        vipInsideWarningRing = null;
        vipStatus = null;
        connectedDeviceId = null;
        connectedSessionId = null;
        UpdateVipCommandBanner(null, null, false);

        if (locationDataSource is not null)
        {
            locationDataSource.LocationChanged -= OnLocationChanged;

            if (locationDataSource.Status == LocationDataSourceStatus.Started)
            {
                await locationDataSource.StopAsync();
            }
        }

        locationDataSource = null;
        lock (latestLocationGate)
        {
            latestLocation = null;
        }

        if (socket is not null)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnect", CancellationToken.None);
                }
            }
            catch
            {
            }

            socket.Dispose();
            socket = null;
        }

        await DisconnectSimulationSocketAsync();

        if (!isShuttingDown)
        {
            ApplyStatusVisual(null, "Disconnected");
        }

        displayedDistance = "  --.- m";
        displayedDirection = "-";
        UpdateEscortMetricsDetail();
    }

    private async Task HandleConnectionDropAsync(string source)
    {
        if (isShuttingDown)
            return;

        if (handlingConnectionDrop)
            return;

        handlingConnectionDrop = true;

        try
        {
            LogDiagnostic($"Connection drop detected by {source}. Beginning reconnect sequence.");
            ApplyStatusVisual(null, "Reconnecting");

            try
            {
                await DisconnectAsync();
            }
            catch (Exception ex)
            {
                LogDiagnostic($"Disconnect during reconnect failed: {ex}");
            }

            await Task.Delay(ReconnectDelay);

            try
            {
                await EnsureConnectedLoopAsync();
            }
            catch (Exception ex)
            {
                LogDiagnostic($"EnsureConnectedLoopAsync failed during reconnect: {ex}");
            }
        }
        finally
        {
            LogDiagnostic($"Reconnect sequence from {source} completed. SocketOpen={socket is { State: WebSocketState.Open }}");
            handlingConnectionDrop = false;
        }
    }

    private async Task EnsureConnectedLoopAsync()
    {
        if (reconnectLoopActive || isShuttingDown)
            return;

        reconnectLoopActive = true;
        try
        {
            while (!isShuttingDown)
            {
                if (socket is { State: WebSocketState.Open })
                {
                    LogDiagnostic("EnsureConnectedLoopAsync: socket already open.");
                    return;
                }

                var connected = await ConnectAsync();
                if (connected)
                {
                    LogDiagnostic("EnsureConnectedLoopAsync: connected successfully.");
                    return;
                }

                LogDiagnostic("EnsureConnectedLoopAsync: connect attempt failed; retrying.");
                await Task.Delay(ReconnectDelay);
            }
        }
        finally
        {
            reconnectLoopActive = false;
        }
    }

    private async Task WatchdogLoopAsync(string role, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !isShuttingDown)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (socket is not { State: WebSocketState.Open })
                continue;

            if (receiveTask is { IsCompleted: true } || sendTask is { IsCompleted: true })
            {
                LogDiagnostic("Watchdog detected completed receive/send task while socket is open.");
                await HandleConnectionDropAsync("Watchdog-CompletedTask");
                continue;
            }

            if (string.Equals(role, "VIP", StringComparison.OrdinalIgnoreCase)
                && hasSentOutbound
                && DateTimeOffset.UtcNow - lastOutboundUtc > TimeSpan.FromSeconds(8))
            {
                LogDiagnostic($"Watchdog detected outbound stall. Last outbound age={(DateTimeOffset.UtcNow - lastOutboundUtc).TotalSeconds:F1}s");
                await HandleConnectionDropAsync("Watchdog-OutboundStall");
                continue;
            }

            if (simulatedMode)
            {
                if (simulationReceiveTask is { IsCompleted: true })
                {
                    LogDiagnostic("Watchdog detected completed simulation receive task. Reconnecting simulation socket.");
                    StartSimulationReconnectLoop(role, configuredDeviceId, configuredSessionId, cancellationToken);
                    continue;
                }

                if (simulationSocket is not { State: WebSocketState.Open })
                {
                    StartSimulationReconnectLoop(role, configuredDeviceId, configuredSessionId, cancellationToken);
                }
            }
        }
    }

    private async Task DisconnectSimulationSocketAsync()
    {
        if (simulationSocket is null)
            return;

        try
        {
            if (simulationSocket.State == WebSocketState.Open)
            {
                await simulationSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnect", CancellationToken.None);
            }
        }
        catch
        {
        }

        simulationSocket.Dispose();
        simulationSocket = null;
    }
}
