using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using CommandMessaging;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;

var engine = new SimulationEngineHost();
Console.CancelKeyPress += async (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    await engine.StopAsync().ConfigureAwait(false);
};

await engine.StartAsync().ConfigureAwait(false);
Console.WriteLine("SimulationEngine running on ws://127.0.0.1:8775/ws/ . Press Ctrl+C to stop.");
await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);

internal sealed class SimulationEngineHost
{
    private const string SessionId = "DEVSUMMIT-2026";
    private const string EndpointPrefix = "http://127.0.0.1:8775/ws/";
    private const string RouteLayerName = "CampusRoute";
    private static readonly string RouteGeodatabasePath = ResolveRouteGeodatabasePath();
    private const int TickMilliseconds = 250;
    private const double EscortRouteSpeedMetersPerSecond = 1.35;
    private const double EscortMinRouteSpeedMetersPerSecond = 0.0;
    private const double EscortMaxRouteSpeedMetersPerSecond = 2.1;
    private const double EscortSpeedSmoothingPerSecond = 3.0;
    private const double EscortTargetCentroidDistanceMeters = 12.0;
    private const double EscortCentroidControlGain = 0.045;
    private const double EscortCentroidLeadControlGain = 0.04;
    private const double VipBaseRouteSpeedMetersPerSecond = 1.0;
    private const double VipSpeedSpreadMetersPerSecond = 0.2;
    private const double VipInitialSpacingMeters = 18.0;
    private const double VipWanderMaxOffsetMeters = 2.4;
    private const double VipWanderResponsePerSecond = 0.65;
    private const double VipWanderRetargetMinSeconds = 3.2;
    private const double VipWanderRetargetMaxSeconds = 6.8;

    private readonly HttpListener listener = new();
    private readonly ConcurrentDictionary<string, WebSocket> clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> sendGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> clientRoles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, VipState> vipStateByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, VipSpeedDirective> vipSpeedDirectiveByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim publishGate = new(1, 1);
    private readonly CancellationTokenSource shutdownCts = new();
    private readonly Random wanderRandom = new();

    private Task? acceptLoopTask;
    private Task? simulationLoopTask;
    private Polyline? escortRoute;
    private List<Polyline> routePolylines = [];
    private List<ExcursionRouteWindow> excursionRouteWindows = [];
    private double escortRouteDistanceMeters;
    private double escortRouteLengthMeters;
    private double currentEscortRouteSpeedMetersPerSecond = EscortRouteSpeedMetersPerSecond;
    private DateTimeOffset? lastEscortRouteStepUtc;

    public SimulationEngineHost()
    {
        listener.Prefixes.Add(EndpointPrefix);
    }

    public async Task StartAsync()
    {
        await InitializeEscortRouteAsync().ConfigureAwait(false);
        listener.Start();
        acceptLoopTask = Task.Run(() => AcceptLoopAsync(shutdownCts.Token), shutdownCts.Token);
        simulationLoopTask = Task.Run(() => RunSimulationLoopAsync(shutdownCts.Token), shutdownCts.Token);
    }

    public async Task StopAsync()
    {
        shutdownCts.Cancel();
        if (listener.IsListening)
            listener.Stop();

        if (acceptLoopTask is not null)
            await acceptLoopTask.ConfigureAwait(false);

        if (simulationLoopTask is not null)
        {
            try
            {
                await simulationLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        foreach (var socket in clients.Values)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stopping", CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }

            socket.Dispose();
        }

        clients.Clear();
        foreach (var gate in sendGates.Values)
            gate.Dispose();

        sendGates.Clear();
        clientRoles.Clear();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext? context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
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
            var wsContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
            socket = wsContext.WebSocket;

            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var json = await ReadTextMessageAsync(socket, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                    continue;

                var envelope = MessageSerializer.DeserializeEnvelope(json);
                if (envelope is null || string.IsNullOrWhiteSpace(envelope.DeviceId))
                    continue;

                registeredDeviceId = envelope.DeviceId;
                clients[registeredDeviceId] = socket;

                if (envelope.Type == MessageTypes.Register)
                {
                    var registerPayload = MessageSerializer.DeserializePayload<RegisterPayload>(envelope);
                    if (registerPayload is not null)
                        clientRoles[registeredDeviceId] = registerPayload.Role;

                    await SendRouteSnapshotAsync(registeredDeviceId, cancellationToken).ConfigureAwait(false);

                    continue;
                }

                if (envelope.Type == MessageTypes.VipControl)
                {
                    var controlPayload = MessageSerializer.DeserializePayload<VipControlPayload>(envelope);
                    if (controlPayload is null || string.IsNullOrWhiteSpace(controlPayload.DeviceId))
                        continue;

                    var directive = controlPayload.Signal.Trim().ToUpperInvariant() switch
                    {
                        "STOP" => VipSpeedDirective.Stop,
                        "HURRY" => VipSpeedDirective.Hurry,
                        _ => VipSpeedDirective.Normal
                    };

                    if (directive == VipSpeedDirective.Normal)
                        vipSpeedDirectiveByDevice.TryRemove(controlPayload.DeviceId, out _);
                    else
                        vipSpeedDirectiveByDevice[controlPayload.DeviceId] = directive;

                    continue;
                }

                if (envelope.Type == MessageTypes.StatusUpdate)
                {
                    var statusPayload = MessageSerializer.DeserializePayload<StatusUpdatePayload>(envelope);
                    if (statusPayload is null)
                        continue;

                    var current = vipStateByDevice.GetOrAdd(envelope.DeviceId, _ => new VipState());
                    current.PreviousStatus = current.Status;
                    current.Status = statusPayload.Status;

                    if (IsTransitionToIn(current.PreviousStatus, statusPayload.Status)
                        && vipSpeedDirectiveByDevice.TryRemove(envelope.DeviceId, out var priorDirective)
                        && priorDirective != VipSpeedDirective.Normal)
                    {
                        var resumePayload = new VipControlPayload(
                            DeviceId: envelope.DeviceId,
                            Signal: "RESUME",
                            Message: "Escort reached. Resume normal speed.",
                            IsActive: false);

                        var resumeEnvelope = MessageSerializer.CreateEnvelope(
                            MessageTypes.VipControl,
                            SessionId,
                            "simulation-engine",
                            resumePayload);

                        await SendToDeviceAsync(envelope.DeviceId, resumeEnvelope, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch
        {
        }
        finally
        {
            if (registeredDeviceId is not null)
            {
                clients.TryRemove(registeredDeviceId, out _);
                clientRoles.TryRemove(registeredDeviceId, out _);
                vipStateByDevice.TryRemove(registeredDeviceId, out _);
                if (sendGates.TryRemove(registeredDeviceId, out var gate))
                    gate.Dispose();
            }

            if (socket is not null)
            {
                try
                {
                    if (socket.State == WebSocketState.Open)
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }

                socket.Dispose();
            }
        }
    }

    private async Task RunSimulationLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PublishAssignedLocationsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }

            await Task.Delay(TickMilliseconds, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishAssignedLocationsAsync(CancellationToken cancellationToken)
    {
        if (escortRoute is null)
            return;

        await publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var vipDeviceIds = GetClientDeviceIdsByRole("VIP")
                .OrderBy(deviceId => deviceId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var nowUtc = DateTimeOffset.UtcNow;
            var deltaSeconds = lastEscortRouteStepUtc.HasValue
                ? Math.Clamp((nowUtc - lastEscortRouteStepUtc.Value).TotalSeconds, 0.05, 1.0)
                : TickMilliseconds / 1000.0;
            lastEscortRouteStepUtc = nowUtc;

            for (var vipIndex = 0; vipIndex < vipDeviceIds.Count; vipIndex++)
            {
                var vipDeviceId = vipDeviceIds[vipIndex];
                var current = vipStateByDevice.GetOrAdd(vipDeviceId, _ => new VipState());
                var mainRouteLengthMeters = Math.Max(escortRouteLengthMeters, 1);
                var startingDistanceMeters = current.RouteDistanceMeters
                    ?? NormalizeDistance(vipIndex * VipInitialSpacingMeters, mainRouteLengthMeters);
                var vipSpeedMetersPerSecond = ComputeVipRouteSpeedMetersPerSecond(vipIndex);
                var vipDirective = vipSpeedDirectiveByDevice.GetValueOrDefault(vipDeviceId, VipSpeedDirective.Normal);
                vipSpeedMetersPerSecond = ApplyVipSpeedDirective(vipSpeedMetersPerSecond, vipDirective);
                var updatedDistanceMeters = NormalizeDistance(startingDistanceMeters + vipSpeedMetersPerSecond * deltaSeconds, mainRouteLengthMeters);

                var vipPoint = PointAlongPolylineGeodetic(escortRoute, updatedDistanceMeters);
                var activeRoute = escortRoute;
                var activeRouteDistanceMeters = updatedDistanceMeters;
                var activeRouteLengthMeters = mainRouteLengthMeters;
                var activeExcursion = TryGetActiveExcursionWindow(updatedDistanceMeters, mainRouteLengthMeters, vipDeviceId, vipDeviceIds);
                if (activeExcursion is not null)
                {
                    var excursionMainOffsetMeters = ForwardDistanceAlongRoute(
                        activeExcursion.MainStartDistanceMeters,
                        updatedDistanceMeters,
                        mainRouteLengthMeters);
                    var excursionProgress = activeExcursion.MainWindowLengthMeters <= 0
                        ? 0
                        : Math.Clamp(excursionMainOffsetMeters / activeExcursion.MainWindowLengthMeters, 0, 1);
                    var excursionDistanceMeters = excursionProgress * activeExcursion.ExcursionLengthMeters;
                    vipPoint = PointAlongPolylineGeodetic(activeExcursion.ExcursionRoute, excursionDistanceMeters);
                    activeRoute = activeExcursion.ExcursionRoute;
                    activeRouteDistanceMeters = excursionDistanceMeters;
                    activeRouteLengthMeters = activeExcursion.ExcursionLengthMeters;
                }

                vipPoint = ApplyVipWanderOffset(
                    vipPoint,
                    activeRoute,
                    activeRouteDistanceMeters,
                    activeRouteLengthMeters,
                    current,
                    nowUtc,
                    deltaSeconds);

                current.RouteDistanceMeters = updatedDistanceMeters;
                current.Latitude = vipPoint.Y;
                current.Longitude = vipPoint.X;
            }

            if (!TryGetEscortPosition(deltaSeconds, out var escortLatitude, out var escortLongitude))
                return;

            var escortAssignedPayload = new AssignedLocationPayload(
                DeviceId: "Escort",
                Role: "Escort",
                Latitude: escortLatitude,
                Longitude: escortLongitude,
                Status: "In",
                DistanceMeters: 0,
                DirectionToEscort: "-");

            foreach (var escortDeviceId in GetClientDeviceIdsByRole("Escort"))
            {
                var envelope = MessageSerializer.CreateEnvelope(MessageTypes.AssignedLocation, SessionId, "simulation-engine", escortAssignedPayload);
                await SendToDeviceAsync(escortDeviceId, envelope, cancellationToken).ConfigureAwait(false);
            }

            for (var vipIndex = 0; vipIndex < vipDeviceIds.Count; vipIndex++)
            {
                var vipDeviceId = vipDeviceIds[vipIndex];
                if (!vipStateByDevice.TryGetValue(vipDeviceId, out var current))
                    continue;

                var vipPoint = new MapPoint(current.Longitude, current.Latitude, SpatialReferences.Wgs84);
                var escortPoint = new MapPoint(escortLongitude, escortLatitude, SpatialReferences.Wgs84);
                var distanceMeters = CalculateDistanceMeters(vipPoint, escortPoint);
                current.DistanceMeters = distanceMeters;
                current.DirectionToEscort = ComputeDirectionCardinal(current.Latitude, current.Longitude, escortLatitude, escortLongitude);

                var assignedPayload = new AssignedLocationPayload(
                    DeviceId: vipDeviceId,
                    Role: "VIP",
                    Latitude: current.Latitude,
                    Longitude: current.Longitude,
                    Status: "Unknown",
                    DistanceMeters: current.DistanceMeters,
                    DirectionToEscort: current.DirectionToEscort);

                var envelope = MessageSerializer.CreateEnvelope(MessageTypes.AssignedLocation, SessionId, "simulation-engine", assignedPayload);
                await SendToDeviceAsync(vipDeviceId, envelope, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            publishGate.Release();
        }
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

    private List<string> GetClientDeviceIdsByRole(string role)
    {
        return clientRoles
            .Where(x => string.Equals(x.Value, role, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
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
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
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

    private async Task SendRouteSnapshotAsync(string deviceId, CancellationToken cancellationToken)
    {
        if (escortRoute is null)
            return;

        var points = escortRoute.Parts
            .SelectMany(part => part.Points)
            .Select(point => point.SpatialReference == SpatialReferences.Wgs84
                ? point
                : GeometryEngine.Project(point, SpatialReferences.Wgs84) as MapPoint)
            .Where(point => point is not null)
            .Cast<MapPoint>()
            .Select(point => new RoutePointPayload(point.Y, point.X))
            .ToList();

        if (points.Count < 2)
            return;

        var payload = new RouteSnapshotPayload("main", points);
        var envelope = MessageSerializer.CreateEnvelope(
            MessageTypes.RouteSnapshot,
            SessionId,
            "simulation-engine",
            payload);

        await SendToDeviceAsync(deviceId, envelope, cancellationToken).ConfigureAwait(false);
    }

    private bool TryGetEscortPosition(double deltaSeconds, out double latitude, out double longitude)
    {
        if (escortRoute is not null)
        {
            var escortTargetSpeedMetersPerSecond = ComputeAdaptiveEscortRouteSpeedMetersPerSecond();
            var speedLerpFactor = Math.Clamp(deltaSeconds * EscortSpeedSmoothingPerSecond, 0.05, 1.0);
            currentEscortRouteSpeedMetersPerSecond += (escortTargetSpeedMetersPerSecond - currentEscortRouteSpeedMetersPerSecond) * speedLerpFactor;
            currentEscortRouteSpeedMetersPerSecond = Math.Clamp(
                currentEscortRouteSpeedMetersPerSecond,
                EscortMinRouteSpeedMetersPerSecond,
                EscortMaxRouteSpeedMetersPerSecond);

            escortRouteDistanceMeters = NormalizeDistance(
                escortRouteDistanceMeters + currentEscortRouteSpeedMetersPerSecond * deltaSeconds,
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

    private static double ApplyVipSpeedDirective(double baseSpeedMetersPerSecond, VipSpeedDirective directive)
    {
        return directive switch
        {
            VipSpeedDirective.Stop => 0.0,
            VipSpeedDirective.Hurry => baseSpeedMetersPerSecond * 2.15,
            _ => baseSpeedMetersPerSecond
        };
    }

    private double ComputeAdaptiveEscortRouteSpeedMetersPerSecond()
    {
        if (escortRoute is null || vipStateByDevice.IsEmpty)
            return EscortRouteSpeedMetersPerSecond;

        var vipSnapshots = vipStateByDevice.Values
            .Where(vip => !double.IsNaN(vip.Latitude) && !double.IsNaN(vip.Longitude))
            .ToList();

        if (vipSnapshots.Count == 0)
            return EscortRouteSpeedMetersPerSecond;

        var centroidLatitude = vipSnapshots.Average(vip => vip.Latitude);
        var centroidLongitude = vipSnapshots.Average(vip => vip.Longitude);

        var escortPoint = PointAlongPolylineGeodetic(escortRoute, escortRouteDistanceMeters);
        var centroidPoint = new MapPoint(centroidLongitude, centroidLatitude, SpatialReferences.Wgs84);

        var centroidDistanceMeters = CalculateDistanceMeters(escortPoint, centroidPoint);
        var centroidDistanceAlongRouteMeters = EstimateNearestDistanceAlongRoute(escortRoute, escortRouteLengthMeters, centroidPoint);
        var escortLeadMeters = ComputeEscortLeadMeters(escortRouteDistanceMeters, centroidDistanceAlongRouteMeters, escortRouteLengthMeters);

        var distanceAdjustment = (centroidDistanceMeters - EscortTargetCentroidDistanceMeters) * EscortCentroidControlGain;
        var leadAdjustment = -escortLeadMeters * EscortCentroidLeadControlGain;
        var speedAdjustment = distanceAdjustment + leadAdjustment;

        return Math.Clamp(EscortRouteSpeedMetersPerSecond + speedAdjustment, EscortMinRouteSpeedMetersPerSecond, EscortMaxRouteSpeedMetersPerSecond);
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

    private async Task InitializeEscortRouteAsync()
    {
        if (!File.Exists(RouteGeodatabasePath))
            throw new InvalidOperationException($"Simulation route geodatabase not found at '{RouteGeodatabasePath}'.");

        var routes = await LoadRoutePolylinesAsync(RouteGeodatabasePath, RouteLayerName).ConfigureAwait(false);
        routePolylines = routes.Where(route => CalculatePolylineLengthMeters(route) > 10).ToList();
        escortRoute = routePolylines.FirstOrDefault();
        escortRouteDistanceMeters = 0;
        escortRouteLengthMeters = escortRoute is null ? 0 : CalculatePolylineLengthMeters(escortRoute);
        excursionRouteWindows = escortRoute is null
            ? []
            : BuildExcursionRouteWindows(escortRoute, escortRouteLengthMeters, routePolylines.Skip(1).ToList());

        if (escortRoute is null)
            throw new InvalidOperationException("No usable escort route loaded from geodatabase.");
    }

    private ExcursionRouteWindow? TryGetActiveExcursionWindow(
        double mainRouteDistanceMeters,
        double mainRouteLengthMeters,
        string vipDeviceId,
        IReadOnlyList<string> orderedVipDeviceIds)
    {
        if (excursionRouteWindows.Count == 0 || orderedVipDeviceIds.Count == 0)
            return null;

        foreach (var excursion in excursionRouteWindows)
        {
            var assignedVipDeviceId = orderedVipDeviceIds[excursion.WindowIndex % orderedVipDeviceIds.Count];
            if (!string.Equals(assignedVipDeviceId, vipDeviceId, StringComparison.OrdinalIgnoreCase))
                continue;

            var offset = ForwardDistanceAlongRoute(excursion.MainStartDistanceMeters, mainRouteDistanceMeters, mainRouteLengthMeters);
            if (offset <= excursion.MainWindowLengthMeters + 0.001)
                return excursion;
        }

        return null;
    }

    private List<ExcursionRouteWindow> BuildExcursionRouteWindows(Polyline mainRoute, double mainRouteLengthMeters, List<Polyline> excursionRoutes)
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
            var mainWindowLengthMeters = ForwardDistanceAlongRoute(mainStartDistanceMeters, mainEndDistanceMeters, mainRouteLengthMeters);
            if (mainWindowLengthMeters <= 1)
                continue;

            windows.Add(new ExcursionRouteWindow(index, excursionRoute, excursionLengthMeters, mainStartDistanceMeters, mainEndDistanceMeters, mainWindowLengthMeters));
        }

        return windows.OrderBy(window => window.MainStartDistanceMeters).ToList();
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
                ?? throw new InvalidOperationException($"Geodatabase does not contain table '{routeLayerName}'.");

            await routesTable.LoadAsync().ConfigureAwait(false);
            var queryResult = await routesTable.QueryFeaturesAsync(new QueryParameters { WhereClause = "1=1" }).ConfigureAwait(false);
            var routes = new List<Polyline>();

            foreach (var feature in queryResult)
            {
                if (feature.Geometry is not Polyline polyline)
                    continue;

                var wgs84Geometry = polyline.SpatialReference == SpatialReferences.Wgs84
                    ? polyline
                    : GeometryEngine.Project(polyline, SpatialReferences.Wgs84) as Polyline;

                if (wgs84Geometry is null)
                    continue;

                foreach (var part in wgs84Geometry.Parts)
                {
                    var points = part.Points.ToList();
                    if (points.Count >= 2)
                    {
                        routes.Add(new Polyline(points));
                    }
                }
            }

            return routes;
        }
        finally
        {
            geodatabase.Close();
        }
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

        return NormalizeDistance(bestDistanceAlong, routeLengthMeters);
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

    private static Polyline ProjectPolylineToMetric(Polyline polyline)
    {
        var projected = GeometryEngine.Project(polyline, SpatialReferences.WebMercator) as Polyline;
        return projected ?? polyline;
    }

    private static MapPoint ProjectPointToMetric(MapPoint point)
    {
        var projected = GeometryEngine.Project(point, SpatialReferences.WebMercator) as MapPoint;
        return projected ?? point;
    }

    private static MapPoint NormalizeForGeodetic(MapPoint point)
    {
        var projected = GeometryEngine.Project(point, SpatialReferences.Wgs84) as MapPoint;
        return projected ?? point;
    }

    private static double CalculateDistanceMeters(MapPoint fromPoint, MapPoint toPoint)
    {
        var metricFrom = ProjectPointToMetric(fromPoint);
        var metricTo = ProjectPointToMetric(toPoint);
        var distance = GeometryEngine.Distance(metricFrom, metricTo);
        return double.IsNaN(distance) ? 0 : Math.Max(0, distance);
    }

    private static string ComputeDirectionCardinal(double fromLatitude, double fromLongitude, double toLatitude, double toLongitude)
    {
        var fromPoint = new MapPoint(fromLongitude, fromLatitude, SpatialReferences.Wgs84);
        var toPoint = new MapPoint(toLongitude, toLatitude, SpatialReferences.Wgs84);
        var geodetic = GeometryEngine.DistanceGeodetic(fromPoint, toPoint, LinearUnits.Meters, AngularUnits.Degrees, GeodeticCurveType.Geodesic);
        var bearing = NormalizeBearingDegrees(geodetic.Azimuth1);

        var cardinals = new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        var index = (int)Math.Round(bearing / 45.0, MidpointRounding.AwayFromZero) % 8;
        return cardinals[index];
    }

    private static double NormalizeBearingDegrees(double bearingDegrees)
    {
        var normalized = bearingDegrees % 360.0;
        return normalized < 0 ? normalized + 360.0 : normalized;
    }

    private MapPoint ApplyVipWanderOffset(
        MapPoint routePoint,
        Polyline route,
        double routeDistanceMeters,
        double routeLengthMeters,
        VipState state,
        DateTimeOffset nowUtc,
        double deltaSeconds)
    {
        if (routeLengthMeters <= 0)
            return routePoint;

        if (!state.NextWanderRetargetUtc.HasValue || nowUtc >= state.NextWanderRetargetUtc.Value)
        {
            state.WanderTargetOffsetMeters = (wanderRandom.NextDouble() * 2.0 - 1.0) * VipWanderMaxOffsetMeters;
            var nextRetargetSeconds = VipWanderRetargetMinSeconds
                + wanderRandom.NextDouble() * (VipWanderRetargetMaxSeconds - VipWanderRetargetMinSeconds);
            state.NextWanderRetargetUtc = nowUtc.AddSeconds(nextRetargetSeconds);
        }

        var blend = Math.Clamp(deltaSeconds * VipWanderResponsePerSecond, 0.02, 1.0);
        state.WanderOffsetMeters += (state.WanderTargetOffsetMeters - state.WanderOffsetMeters) * blend;
        state.WanderOffsetMeters = Math.Clamp(state.WanderOffsetMeters, -VipWanderMaxOffsetMeters, VipWanderMaxOffsetMeters);

        if (Math.Abs(state.WanderOffsetMeters) < 0.05)
            return routePoint;

        var routeBearingDegrees = ComputeRouteBearingDegrees(route, routeDistanceMeters, routeLengthMeters);
        var lateralBearingDegrees = NormalizeBearingDegrees(routeBearingDegrees + (state.WanderOffsetMeters >= 0 ? 90.0 : -90.0));
        return MovePointGeodetic(routePoint, Math.Abs(state.WanderOffsetMeters), lateralBearingDegrees);
    }

    private static double ComputeRouteBearingDegrees(Polyline route, double routeDistanceMeters, double routeLengthMeters)
    {
        if (routeLengthMeters <= 0)
            return 0;

        var probeStepMeters = Math.Min(4.0, Math.Max(1.0, routeLengthMeters / 500.0));
        var fromPoint = PointAlongPolylineGeodetic(route, routeDistanceMeters);
        var toPoint = PointAlongPolylineGeodetic(route, NormalizeDistance(routeDistanceMeters + probeStepMeters, routeLengthMeters));

        return ComputeBearingDegrees(fromPoint, toPoint);
    }

    private static double ComputeBearingDegrees(MapPoint fromPoint, MapPoint toPoint)
    {
        var geodetic = GeometryEngine.DistanceGeodetic(
            fromPoint,
            toPoint,
            LinearUnits.Meters,
            AngularUnits.Degrees,
            GeodeticCurveType.Geodesic);

        return NormalizeBearingDegrees(geodetic.Azimuth1);
    }

    private static MapPoint MovePointGeodetic(MapPoint startPoint, double distanceMeters, double bearingDegrees)
    {
        const double earthRadiusMeters = 6_371_000.0;

        var angularDistance = distanceMeters / earthRadiusMeters;
        var bearingRadians = DegreesToRadians(bearingDegrees);
        var latitude1 = DegreesToRadians(startPoint.Y);
        var longitude1 = DegreesToRadians(startPoint.X);

        var latitude2 = Math.Asin(
            Math.Sin(latitude1) * Math.Cos(angularDistance)
            + Math.Cos(latitude1) * Math.Sin(angularDistance) * Math.Cos(bearingRadians));

        var longitude2 = longitude1 + Math.Atan2(
            Math.Sin(bearingRadians) * Math.Sin(angularDistance) * Math.Cos(latitude1),
            Math.Cos(angularDistance) - Math.Sin(latitude1) * Math.Sin(latitude2));

        var normalizedLongitude = ((RadiansToDegrees(longitude2) + 540.0) % 360.0) - 180.0;
        var normalizedLatitude = RadiansToDegrees(latitude2);
        return new MapPoint(normalizedLongitude, normalizedLatitude, SpatialReferences.Wgs84);
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

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static double RadiansToDegrees(double radians) => radians * 180.0 / Math.PI;

    private static bool IsTransitionToIn(string? previousStatus, string? currentStatus)
    {
        var previous = previousStatus?.Trim();
        var current = currentStatus?.Trim();
        return !string.Equals(previous, "In", StringComparison.OrdinalIgnoreCase)
            && string.Equals(current, "In", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveRouteGeodatabasePath()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("SIM_ROUTE_GDB_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return fromEnvironment;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, "Documents", "ArcGIS", "Projects", "MyProject5", "CampusRoute.geodatabase");
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

        public double WanderOffsetMeters { get; set; }

        public double WanderTargetOffsetMeters { get; set; }

        public DateTimeOffset? NextWanderRetargetUtc { get; set; }
    }

    private enum VipSpeedDirective
    {
        Normal,
        Stop,
        Hurry
    }

    private sealed record ExcursionRouteWindow(
        int WindowIndex,
        Polyline ExcursionRoute,
        double ExcursionLengthMeters,
        double MainStartDistanceMeters,
        double MainEndDistanceMeters,
        double MainWindowLengthMeters);
}
