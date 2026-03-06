using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using CommandMessaging;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;

namespace CommandDashboard.Services.Messaging;

public sealed class FieldMessagingHost : IAsyncDisposable
{
    private const double EscortSafetyRadiusMeters = 25.0;
    private const double EscortRouteSpeedMetersPerSecond = 1.35;
    private const double EscortMinRouteSpeedMetersPerSecond = 0.0;
    private const double EscortMaxRouteSpeedMetersPerSecond = 2.1;
    private const double EscortTargetCentroidDistanceMeters = 12.0;
    private const double EscortCentroidControlGain = 0.045;
    private const double EscortCentroidLeadControlGain = 0.04;
    private const double VipBaseRouteSpeedMetersPerSecond = 1.0;
    private const double VipSpeedSpreadMetersPerSecond = 0.2;
    private const double VipInitialSpacingMeters = 18.0;
    private const double RouteDensifySegmentMeters = 2.0;
    private readonly HttpListener listener = new();
    private readonly ConcurrentDictionary<string, WebSocket> clients = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> sendGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> clientRoles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, VipState> vipStateByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, VipSpeedDirective> vipSpeedDirectiveByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly string sessionId;
    private readonly CancellationTokenSource shutdownCts = new();
    private Task? acceptLoopTask;
    private Polyline? escortRoute = null;
    private List<Polyline> routePolylines = [];
    private List<ExcursionRouteWindow> excursionRouteWindows = [];
    private double escortRouteDistanceMeters;
    private double escortRouteLengthMeters = 0;

    public FieldMessagingHost(string sessionId, string endpointPrefix = "http://127.0.0.1:8765/ws/")
    {
        this.sessionId = sessionId;
        listener.Prefixes.Add(endpointPrefix);
    }

    public event EventHandler<FieldMessageReceivedEventArgs>? MessageReceived;
    public event EventHandler? ConnectionStateChanged;

    public bool IsRunning => listener.IsListening;

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
        excursionRouteWindows = [];
        OnConnectionStateChanged();
    }

    public async Task<bool> SendAssignedLocationAsync(string targetDeviceId, AssignedLocationPayload payload, CancellationToken cancellationToken = default)
    {
        var envelope = MessageSerializer.CreateEnvelope(MessageTypes.AssignedLocation, sessionId, "dashboard", payload);
        return await SendToDeviceAsync(targetDeviceId, envelope, cancellationToken).ConfigureAwait(false);
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

    private async Task BroadcastToRolesAsync(IEnumerable<string> roles, FieldMessageEnvelope envelope, CancellationToken cancellationToken)
    {
        var roleSet = new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase);

        var targets = clientRoles
            .Where(x => roleSet.Contains(x.Value))
            .Select(x => x.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var deviceId in targets)
        {
            await SendToDeviceAsync(deviceId, envelope, cancellationToken).ConfigureAwait(false);
        }
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

    private bool TryGetEscortPosition(out double latitude, out double longitude)
    {
        return TryGetEscortPosition(1.0, out latitude, out longitude);
    }

    private bool TryGetEscortPosition(double deltaSeconds, out double latitude, out double longitude)
    {
        if (escortRoute is not null)
        {
            var escortSpeedMetersPerSecond = ComputeAdaptiveEscortRouteSpeedMetersPerSecond(escortLatitude: out _, escortLongitude: out _);
            escortRouteDistanceMeters = NormalizeDistance(
                escortRouteDistanceMeters + escortSpeedMetersPerSecond * deltaSeconds,
                escortRouteLengthMeters);

            var escortPoint = PointAlongPolylineGeodetic(escortRoute, escortRouteDistanceMeters);

            latitude = escortPoint.Y;
            longitude = escortPoint.X;
            return true;
        }

        latitude = default;
        longitude = default;
        return false;
    }

    private static double ComputeVipRouteSpeedMetersPerSecond(int vipIndex)
    {
        var offset = (vipIndex % 3) - 1;
        return Math.Max(0.5, VipBaseRouteSpeedMetersPerSecond + offset * VipSpeedSpreadMetersPerSecond);
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

    private static double ApplyVipSpeedDirective(double baseSpeedMetersPerSecond, VipSpeedDirective directive)
    {
        return directive switch
        {
            VipSpeedDirective.Stop => 0.0,
            VipSpeedDirective.Hurry => baseSpeedMetersPerSecond * 1.85,
            _ => baseSpeedMetersPerSecond
        };
    }

    private double ComputeAdaptiveEscortRouteSpeedMetersPerSecond(out double? escortLatitude, out double? escortLongitude)
    {
        escortLatitude = null;
        escortLongitude = null;

        if (escortRoute is null)
            return EscortRouteSpeedMetersPerSecond;

        if (vipStateByDevice.IsEmpty)
            return EscortRouteSpeedMetersPerSecond;

        var vipSnapshots = vipStateByDevice.Values
            .Where(vip => !double.IsNaN(vip.Latitude) && !double.IsNaN(vip.Longitude))
            .ToList();

        if (vipSnapshots.Count == 0)
            return EscortRouteSpeedMetersPerSecond;

        var centroidLatitude = vipSnapshots.Average(vip => vip.Latitude);
        var centroidLongitude = vipSnapshots.Average(vip => vip.Longitude);

        var escortPoint = PointAlongPolylineGeodetic(escortRoute, escortRouteDistanceMeters);

        escortLatitude = escortPoint.Y;
        escortLongitude = escortPoint.X;

        var centroidPoint = new MapPoint(centroidLongitude, centroidLatitude, SpatialReferences.Wgs84);
        var centroidDistanceMeters = CalculateDistanceMeters(escortPoint, centroidPoint);
        var centroidDistanceAlongRouteMeters = EstimateNearestDistanceAlongRoute(
            escortRoute,
            escortRouteLengthMeters,
            centroidPoint);
        var escortLeadMeters = ComputeEscortLeadMeters(
            escortRouteDistanceMeters,
            centroidDistanceAlongRouteMeters,
            escortRouteLengthMeters);

        var distanceAdjustment = (centroidDistanceMeters - EscortTargetCentroidDistanceMeters) * EscortCentroidControlGain;
        var leadAdjustment = -escortLeadMeters * EscortCentroidLeadControlGain;
        var speedAdjustment = distanceAdjustment + leadAdjustment;
        return Math.Clamp(
            EscortRouteSpeedMetersPerSecond + speedAdjustment,
            EscortMinRouteSpeedMetersPerSecond,
            EscortMaxRouteSpeedMetersPerSecond);
    }

    private static double ComputeEscortLeadMeters(double escortDistanceMeters, double targetDistanceMeters, double routeLengthMeters)
    {
        if (routeLengthMeters <= 0)
            return 0;

        var targetToEscort = ForwardDistanceAlongRoute(targetDistanceMeters, escortDistanceMeters, routeLengthMeters);
        if (targetToEscort <= routeLengthMeters / 2.0)
            return targetToEscort;

        return -(routeLengthMeters - targetToEscort);
    }

    private async Task PublishEscortSnapshotAsync(double escortLatitude, double escortLongitude, CancellationToken cancellationToken)
    {
        var escortDeviceIds = GetClientDeviceIdsByRole("Escort");
        const string escortSourceDeviceId = "Escort";

        var escortEnvelope = MessageSerializer.CreateEnvelope(
            MessageTypes.EscortPosition,
            sessionId,
            escortSourceDeviceId,
            new EscortPositionPayload(escortLatitude, escortLongitude));

        await BroadcastToRoleAsync("VIP", escortEnvelope, cancellationToken).ConfigureAwait(false);

        foreach (var escortDeviceId in escortDeviceIds)
        {
            var escortAssignedPayload = new AssignedLocationPayload(
                escortDeviceId,
                "Escort",
                escortLatitude,
                escortLongitude,
                "In",
                0,
                "-");

            await SendAssignedLocationAsync(escortDeviceId, escortAssignedPayload, cancellationToken).ConfigureAwait(false);
        }
    }

    private ExcursionRouteWindow? TryGetActiveExcursionWindow(
        double mainRouteDistanceMeters,
        double mainRouteLengthMeters,
        string vipDeviceId,
        IReadOnlyList<string> orderedVipDeviceIds)
    {
        if (excursionRouteWindows.Count == 0)
            return null;

        if (orderedVipDeviceIds.Count == 0)
            return null;

        foreach (var excursion in excursionRouteWindows)
        {
            var assignedVipDeviceId = orderedVipDeviceIds[excursion.WindowIndex % orderedVipDeviceIds.Count];
            if (!string.Equals(assignedVipDeviceId, vipDeviceId, StringComparison.OrdinalIgnoreCase))
                continue;

            var offset = ForwardDistanceAlongRoute(
                excursion.MainStartDistanceMeters,
                mainRouteDistanceMeters,
                mainRouteLengthMeters);
            if (offset <= excursion.MainWindowLengthMeters + 0.001)
                return excursion;
        }

        return null;
    }

    private List<ExcursionRouteWindow> BuildExcursionRouteWindows(
        Polyline mainRoute,
        double mainRouteLengthMeters,
        List<Polyline> excursionRoutes)
    {
        var windows = new List<ExcursionRouteWindow>();
        if (mainRouteLengthMeters <= 0 || excursionRoutes.Count == 0)
            return windows;

        for (var index = 0; index < excursionRoutes.Count; index++)
        {
            var excursionRoute = excursionRoutes[index];
            var excursionLengthMeters = CalculatePolylineLengthMeters(excursionRoute);
            if (excursionLengthMeters <= 1)
                continue;

            var startPoint = excursionRoute.Parts.FirstOrDefault()?.StartPoint;
            var endPoint = excursionRoute.Parts.LastOrDefault()?.EndPoint;
            if (startPoint is null || endPoint is null)
                continue;

            var mainStartDistanceMeters = EstimateNearestDistanceAlongRoute(mainRoute, mainRouteLengthMeters, startPoint);
            var mainEndDistanceMeters = EstimateNearestDistanceAlongRoute(mainRoute, mainRouteLengthMeters, endPoint);
            var mainWindowLengthMeters = ForwardDistanceAlongRoute(
                mainStartDistanceMeters,
                mainEndDistanceMeters,
                mainRouteLengthMeters);

            if (mainWindowLengthMeters <= 1)
                continue;

            windows.Add(new ExcursionRouteWindow(
                index,
                excursionRoute,
                excursionLengthMeters,
                mainStartDistanceMeters,
                mainEndDistanceMeters,
                mainWindowLengthMeters));
        }

        return windows
            .OrderBy(window => window.MainStartDistanceMeters)
            .ToList();
    }

    private static double EstimateNearestDistanceAlongRoute(Polyline route, double routeLengthMeters, MapPoint targetPoint)
    {
        if (routeLengthMeters <= 0)
            return 0;

        var metricRoute = ProjectPolylineToMetric(route);
        var metricTarget = ProjectPointToMetric(targetPoint);

        var bestDistanceAlong = 0.0;
        var bestDistanceMeters = double.MaxValue;

        var stepMeters = Math.Max(2.0, routeLengthMeters / 250.0);
        for (var distanceAlong = 0.0; distanceAlong <= routeLengthMeters; distanceAlong += stepMeters)
        {
            var candidatePoint = GeometryEngine.CreatePointAlong(metricRoute, distanceAlong);
            if (candidatePoint is null)
                continue;

            var candidateDistance = GeometryEngine.Distance(metricTarget, candidatePoint);
            if (candidateDistance < bestDistanceMeters)
            {
                bestDistanceMeters = candidateDistance;
                bestDistanceAlong = distanceAlong;
            }
        }

        for (var refine = 0; refine < 3; refine++)
        {
            stepMeters = Math.Max(0.5, stepMeters / 4.0);
            var windowStart = Math.Max(0, bestDistanceAlong - (stepMeters * 4.0));
            var windowEnd = Math.Min(routeLengthMeters, bestDistanceAlong + (stepMeters * 4.0));

            for (var distanceAlong = windowStart; distanceAlong <= windowEnd; distanceAlong += stepMeters)
            {
                var candidatePoint = GeometryEngine.CreatePointAlong(metricRoute, distanceAlong);
                if (candidatePoint is null)
                    continue;

                var candidateDistance = GeometryEngine.Distance(metricTarget, candidatePoint);
                if (candidateDistance < bestDistanceMeters)
                {
                    bestDistanceMeters = candidateDistance;
                    bestDistanceAlong = distanceAlong;
                }
            }
        }

        return NormalizeDistance(bestDistanceAlong, routeLengthMeters);
    }

    private static async Task<List<Polyline>> LoadRoutePolylinesAsync(string routePath, string routeLayerName)
    {
        var routeFileExtension = Path.GetExtension(routePath);
        if (!routeFileExtension.Equals(".geodatabase", StringComparison.OrdinalIgnoreCase))
            return [];

        var geodatabase = await Geodatabase.OpenAsync(routePath).ConfigureAwait(false);
        try
        {
            var routesTable = geodatabase.GetGeodatabaseFeatureTable(routeLayerName)
                ?? throw new InvalidOperationException($"Geodatabase does not contain a table named '{routeLayerName}'.");

            try
            {
                await routesTable.LoadAsync().ConfigureAwait(false);
                var routes = await ReadPolylinesFromFeatureTableAsync(routesTable).ConfigureAwait(false);
                if (routes.Count > 0)
                    return routes;
            }
            catch
            {
            }

            return [];
        }
        finally
        {
            geodatabase.Close();
        }
    }

    private static async Task<List<Polyline>> ReadPolylinesFromFeatureTableAsync(FeatureTable featureTable)
    {
        var routes = new List<Polyline>();
        var queryParameters = new QueryParameters { WhereClause = "1=1" };
        var features = await featureTable.QueryFeaturesAsync(queryParameters).ConfigureAwait(false);
        foreach (var feature in features)
        {
            if (feature.Geometry is not Polyline polyline)
                continue;

            var wgs84Geometry = polyline.SpatialReference == SpatialReferences.Wgs84
                ? polyline
                : GeometryEngine.Project(polyline, SpatialReferences.Wgs84) as Polyline;

            if (wgs84Geometry is null)
                continue;

            var normalizedRoute = DensifyRouteWgs84(wgs84Geometry, RouteDensifySegmentMeters);

            var partCount = normalizedRoute.Parts.Count();
            if (partCount != 1)
            {
                throw new InvalidOperationException(
                    $"Route data must be single-part polylines. Found a feature with {partCount} parts.");
            }

            routes.Add(normalizedRoute);
        }

        return routes;
    }

    private static Polyline DensifyRouteWgs84(Polyline routeWgs84, double maxSegmentMeters)
    {
        var metricRoute = ProjectPolylineToMetric(routeWgs84);
        var densifiedMetric = GeometryEngine.Densify(metricRoute, maxSegmentMeters) as Polyline;
        if (densifiedMetric is null)
            return routeWgs84;

        var densifiedWgs84 = GeometryEngine.Project(densifiedMetric, SpatialReferences.Wgs84) as Polyline;
        return densifiedWgs84 ?? routeWgs84;
    }

    private static double NormalizeDistance(double value, double lengthMeters)
    {
        if (lengthMeters <= 0)
            return 0;

        var remainder = value % lengthMeters;
        return remainder < 0 ? remainder + lengthMeters : remainder;
    }

    private static double ForwardDistanceAlongRoute(double fromDistanceMeters, double toDistanceMeters, double routeLengthMeters)
    {
        if (routeLengthMeters <= 0)
            return 0;

        var from = NormalizeDistance(fromDistanceMeters, routeLengthMeters);
        var to = NormalizeDistance(toDistanceMeters, routeLengthMeters);
        var distance = to - from;
        return distance < 0 ? distance + routeLengthMeters : distance;
    }

    private static bool IsTransitionToIn(string? previousStatus, string? currentStatus)
    {
        var previous = previousStatus?.Trim();
        var current = currentStatus?.Trim();
        return !string.Equals(previous, "In", StringComparison.OrdinalIgnoreCase)
            && string.Equals(current, "In", StringComparison.OrdinalIgnoreCase);
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

    private static double CalculatePolylineLengthMeters(Polyline polyline)
    {
        var metricRoute = ProjectPolylineToMetric(polyline);
        var length = GeometryEngine.Length(metricRoute);
        return double.IsNaN(length) || length <= 0 ? 0 : length;
    }

    private static MapPoint PointAlongPolylineGeodetic(Polyline polyline, double distanceMeters)
    {
        var routeLengthMeters = CalculatePolylineLengthMeters(polyline);
        if (routeLengthMeters <= 0)
        {
            var firstPart = polyline.Parts.FirstOrDefault();
            return NormalizeForGeodetic(firstPart?.StartPoint ?? new MapPoint(0, 0, SpatialReferences.Wgs84));
        }

        var metricRoute = ProjectPolylineToMetric(polyline);
        var normalizedDistanceMeters = NormalizeDistance(distanceMeters, routeLengthMeters);
        var pointAlong = GeometryEngine.CreatePointAlong(metricRoute, normalizedDistanceMeters);
        if (pointAlong is not null)
            return NormalizeForGeodetic(pointAlong);

        var lastPart = metricRoute.Parts.LastOrDefault();
        return NormalizeForGeodetic(lastPart?.EndPoint ?? new MapPoint(0, 0, SpatialReferences.Wgs84));
    }

    private static string ComputeDirectionCardinal(double fromLatitude, double fromLongitude, double toLatitude, double toLongitude)
    {
        var fromPoint = new MapPoint(fromLongitude, fromLatitude, SpatialReferences.Wgs84);
        var toPoint = new MapPoint(toLongitude, toLatitude, SpatialReferences.Wgs84);
        var bearing = ComputeBearingDegrees(fromPoint, toPoint);

        var cardinals = new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        var index = (int)Math.Round(bearing / 45.0, MidpointRounding.AwayFromZero) % 8;
        return cardinals[index];
    }

    private static double CalculateDistanceMeters(MapPoint fromPoint, MapPoint toPoint)
    {
        var start = NormalizeForGeodetic(fromPoint);
        var end = NormalizeForGeodetic(toPoint);
        var geodeticResult = GeometryEngine.DistanceGeodetic(
            start,
            end,
            LinearUnits.Meters,
            AngularUnits.Degrees,
            GeodeticCurveType.Geodesic);

        return double.IsNaN(geodeticResult.Distance) ? 0.0 : geodeticResult.Distance;
    }

    private static double ComputeBearingDegrees(MapPoint fromPoint, MapPoint toPoint)
    {
        var start = NormalizeForGeodetic(fromPoint);
        var end = NormalizeForGeodetic(toPoint);
        var geodeticResult = GeometryEngine.DistanceGeodetic(
            start,
            end,
            LinearUnits.Meters,
            AngularUnits.Degrees,
            GeodeticCurveType.Geodesic);

        var azimuth = double.IsNaN(geodeticResult.Azimuth1) ? 0.0 : geodeticResult.Azimuth1;
        var normalized = azimuth % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private static Polyline ProjectPolylineToMetric(Polyline polyline)
    {
        if (polyline.SpatialReference == SpatialReferences.WebMercator)
            return polyline;

        return GeometryEngine.Project(polyline, SpatialReferences.WebMercator) as Polyline
            ?? polyline;
    }

    private static MapPoint ProjectPointToMetric(MapPoint point)
    {
        if (point.SpatialReference == SpatialReferences.WebMercator)
            return point;

        return GeometryEngine.Project(point, SpatialReferences.WebMercator) as MapPoint
            ?? new MapPoint(point.X, point.Y, SpatialReferences.WebMercator);
    }

    private static MapPoint NormalizeForGeodetic(MapPoint point)
    {
        if (point.SpatialReference is null)
            return new MapPoint(point.X, point.Y, SpatialReferences.Wgs84);

        if (point.SpatialReference == SpatialReferences.Wgs84)
            return point;

        return GeometryEngine.Project(point, SpatialReferences.Wgs84) as MapPoint
            ?? new MapPoint(point.X, point.Y, SpatialReferences.Wgs84);
    }

    private sealed record ExcursionRouteWindow(
        int WindowIndex,
        Polyline ExcursionRoute,
        double ExcursionLengthMeters,
        double MainStartDistanceMeters,
        double MainEndDistanceMeters,
        double MainWindowLengthMeters);

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
