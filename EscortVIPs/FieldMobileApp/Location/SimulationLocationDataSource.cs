using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Location;
using RuntimeLocation = Esri.ArcGISRuntime.Location.Location;

namespace FieldMobileApp.Location;

public sealed partial class SimulationLocationDataSource : LocationDataSource
{
    private const double SecurityZoneRadiusMeters = 25.0;
    private const double SimulatedHorizontalAccuracyMeters = 1.5;
    private const double OutsideZoneNonExcursionSpeedMultiplier = 0.75;
    private const double MaxStepDistanceMeters = 4.0;
    private const double TrackDensifyMaxSegmentMeters = 2.0;
    private const int DefaultVipVipCount = 3;
    private const double StartupAnchorMaxDistanceMeters = 80.0;
    private const double ExcursionLookaheadMeters = 14.0;
    private const double ExcursionForcedRejoinMinMeters = 90.0;
    private const double ExcursionRearmMinPrimaryDistanceMeters = 36.0;
    private static readonly TimeSpan ExcursionRearmCooldown = TimeSpan.FromSeconds(22);
    private static readonly int SharedRunSeed = HashCode.Combine(DateTime.UtcNow.Year, DateTime.UtcNow.DayOfYear);

    private readonly Lock gate = new();
    private readonly string role;
    private readonly string deviceId;
    private readonly string? routeDataPath;
    private readonly string? routeLayerName;
    private readonly bool passiveAssignedMode;
    private readonly int devicePhaseSeed;
    private RuntimeLocation? latestLocation;
    private double? centerLatitude;
    private double? centerLongitude;
    private double? escortReferenceLatitude;
    private double? escortReferenceLongitude;
    private double? escortPacingReferenceLatitude;
    private double? escortPacingReferenceLongitude;
    private DateTimeOffset escortPacingReferenceUtc = DateTimeOffset.MinValue;
    private CancellationTokenSource? motionCts;
    private Task? motionTask;
    private bool routeInitializationAttempted;
    private bool routeReady;
    private List<RouteTrack> routeTracks = [];
    private RouteTrack? primaryRouteTrack;
    private List<RouteTrack> excursionRouteTracks = [];
    private DateTimeOffset? lastRouteStepUtc;
    private double primaryTrackDistanceMeters;
    private int completedPrimaryLoops;
    private bool usingExcursionTrack;
    private RouteTrack? activeRouteTrack;
    private double activeRouteTrackDistanceMeters;
    private int activeRouteTrackDirection = 1;
    private List<RouteJunction> activeExcursionJunctions = [];
    private RouteJunction? activeExcursionStartJunction;
    private double excursionDistanceSinceStartMeters;
    private RouteTrack? pendingExcursionTrack;
    private RouteJunction? pendingExcursionStartJunction;
    private List<RouteJunction> pendingExcursionJunctions = [];
    private DateTimeOffset recoveryBoostUntilUtc = DateTimeOffset.MinValue;
    private bool activeExcursionHasAlternateJunction;
    private RouteTrack? publishedRouteTrack;
    private double? publishedRouteDistanceMeters;
    private DateTimeOffset excursionSelectionBlockedUntilUtc = DateTimeOffset.MinValue;
    private double? excursionRearmStartPrimaryDistanceMeters;

    public SimulationLocationDataSource(
        string role = "VIP",
        string deviceId = "Device",
        string? routeDataPath = null,
        string? routeLayerName = null,
        bool passiveAssignedMode = false)
    {
        this.role = role;
        this.deviceId = deviceId;
        this.routeDataPath = string.IsNullOrWhiteSpace(routeDataPath) ? null : routeDataPath;
        this.routeLayerName = string.IsNullOrWhiteSpace(routeLayerName) ? null : routeLayerName;
        this.passiveAssignedMode = passiveAssignedMode;
        devicePhaseSeed = Math.Abs(deviceId.GetHashCode(StringComparison.OrdinalIgnoreCase));
        primaryTrackDistanceMeters = devicePhaseSeed % 5;
        activeRouteTrackDistanceMeters = primaryTrackDistanceMeters;
    }

    public void SetAssignedLocation(double latitude, double longitude)
    {
        lock (gate)
        {
            centerLatitude = latitude;
            centerLongitude = longitude;
        }

        if (Status == LocationDataSourceStatus.Started)
        {
            PublishMotionStep();
        }
    }

    public void SetEscortReference(double latitude, double longitude)
    {
        lock (gate)
        {
            escortReferenceLatitude = latitude;
            escortReferenceLongitude = longitude;
        }
    }

    public void SetEscortPacingReference(double latitude, double longitude)
    {
        lock (gate)
        {
            escortPacingReferenceLatitude = latitude;
            escortPacingReferenceLongitude = longitude;
            escortPacingReferenceUtc = DateTimeOffset.UtcNow;
        }
    }

    protected override async Task OnStartAsync()
    {
        if (!passiveAssignedMode && !routeInitializationAttempted)
        {
            routeInitializationAttempted = true;
            await TryInitializeRouteAsync();
        }

        if (passiveAssignedMode)
        {
            PublishMotionStep();
            return;
        }

        lock (gate)
        {
            motionCts?.Cancel();
            motionCts?.Dispose();
            motionCts = new CancellationTokenSource();
        }

        motionTask = RunMotionLoopAsync(motionCts.Token);

        lock (gate)
        {
            if (latestLocation is not null)
            {
                UpdateLocation(latestLocation);
            }
        }

        return;
    }

    protected override Task OnStopAsync()
    {
        lock (gate)
        {
            motionCts?.Cancel();
            motionCts?.Dispose();
            motionCts = null;
            motionTask = null;
        }

        return Task.CompletedTask;
    }

    private async Task RunMotionLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    PublishMotionStep();
                }
                catch
                {
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void PublishMotionStep()
    {
        if (passiveAssignedMode)
        {
            PublishPassiveAssignedStep();
            return;
        }

        if (routeReady && TryPublishRouteMotionStep())
        {
            return;
        }

        double? latitude;
        double? longitude;

        lock (gate)
        {
            latitude = centerLatitude;
            longitude = centerLongitude;
        }

        if (!latitude.HasValue || !longitude.HasValue)
            return;

        var seconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var northMeters = Math.Sin(seconds / 8.0) * 4;
        var eastMeters = Math.Cos(seconds / 10.0) * 3;

        var centerPoint = new MapPoint(longitude.Value, latitude.Value, SpatialReferences.Wgs84);
        var offsetDistanceMeters = Math.Sqrt(northMeters * northMeters + eastMeters * eastMeters);
        var offsetBearingDegrees = NormalizeBearingDegrees(RadiansToDegrees(Math.Atan2(eastMeters, northMeters)));
        var position = MovePointGeodetic(centerPoint, offsetDistanceMeters, offsetBearingDegrees);
        MapPoint finalPoint;
        lock (gate)
        {
            if (latestLocation?.Position is MapPoint previousPoint)
            {
                finalPoint = ClampStepDistance(previousPoint, position, MaxStepDistanceMeters);
            }
            else
            {
                finalPoint = position;
            }
        }

        var location = new RuntimeLocation(finalPoint, SimulatedHorizontalAccuracyMeters, 0.0, 0.0, false);
        PublishLocation(location);
    }

    private void PublishPassiveAssignedStep()
    {
        double? latitude;
        double? longitude;

        lock (gate)
        {
            latitude = centerLatitude;
            longitude = centerLongitude;
        }

        if (!latitude.HasValue || !longitude.HasValue)
            return;

        var position = new MapPoint(longitude.Value, latitude.Value, SpatialReferences.Wgs84);
        var location = new RuntimeLocation(position, SimulatedHorizontalAccuracyMeters, 0.0, 0.0, false);
        PublishLocation(location);
    }

    private bool TryPublishRouteMotionStep()
    {
        RouteTrack? primaryTrackSnapshot;
        List<RouteTrack> excursionTracksSnapshot;
        double? escortLatitudeSnapshot;
        double? escortLongitudeSnapshot;

        lock (gate)
        {
            if (!routeReady || routeTracks.Count == 0 || primaryRouteTrack is null)
            {
                return false;
            }

            primaryTrackSnapshot = primaryRouteTrack;
            excursionTracksSnapshot = excursionRouteTracks;
            escortLatitudeSnapshot = escortReferenceLatitude;
            escortLongitudeSnapshot = escortReferenceLongitude;
        }

        if (primaryTrackSnapshot is null)
            return false;

        var nowUtc = DateTimeOffset.UtcNow;
        var deltaSeconds = lastRouteStepUtc.HasValue
            ? Math.Clamp((nowUtc - lastRouteStepUtc.Value).TotalSeconds, 0.25, 2.0)
            : 1.0;
        lastRouteStepUtc = nowUtc;

        const double baseSpeedMetersPerSecond = 1.35;
        var speedMetersPerSecond = baseSpeedMetersPerSecond;

        if (string.Equals(role, "Escort", StringComparison.OrdinalIgnoreCase))
        {
            double? pacingLatitude;
            double? pacingLongitude;
            DateTimeOffset pacingUtc;
            lock (gate)
            {
                pacingLatitude = escortPacingReferenceLatitude;
                pacingLongitude = escortPacingReferenceLongitude;
                pacingUtc = escortPacingReferenceUtc;
            }

            if (pacingLatitude.HasValue
                && pacingLongitude.HasValue
                && pacingUtc > DateTimeOffset.MinValue
                && (nowUtc - pacingUtc) <= TimeSpan.FromSeconds(8))
            {
                var pacingPoint = new MapPoint(pacingLongitude.Value, pacingLatitude.Value, SpatialReferences.Wgs84);
                var pacingDistanceOnRoute = FindNearestDistanceOnTrack(primaryTrackSnapshot, pacingPoint);
                var signedAheadDelta = SignedCircularDelta(primaryTrackDistanceMeters, pacingDistanceOnRoute, primaryTrackSnapshot.LengthMeters);

                if (signedAheadDelta > 5)
                {
                    speedMetersPerSecond *= Math.Clamp(1.0 + signedAheadDelta / 38.0, 1.0, 1.6);
                }
                else if (signedAheadDelta < -5)
                {
                    speedMetersPerSecond *= Math.Clamp(1.0 + signedAheadDelta / 34.0, 0.7, 1.0);
                }
            }

            primaryTrackDistanceMeters = NormalizeDistance(primaryTrackDistanceMeters + speedMetersPerSecond * deltaSeconds, primaryTrackSnapshot.LengthMeters);
            var (escortPoint, escortBearing) = InterpolateAlongRoute(
                primaryTrackSnapshot.Vertices,
                primaryTrackSnapshot.SegmentLengthsMeters,
                primaryTrackDistanceMeters);

            var escortLocation = new RuntimeLocation(escortPoint, SimulatedHorizontalAccuracyMeters, speedMetersPerSecond, escortBearing, false);
            PublishLocation(escortLocation);

            return true;
        }

        var previousPrimaryDistanceMeters = primaryTrackDistanceMeters;

        if (nowUtc < recoveryBoostUntilUtc)
        {
            speedMetersPerSecond = baseSpeedMetersPerSecond * 1.5;
        }

        var currentPrimaryDistanceMeters = primaryTrackDistanceMeters;
        var (currentPrimaryPoint, _) = InterpolateAlongRoute(
            primaryTrackSnapshot.Vertices,
            primaryTrackSnapshot.SegmentLengthsMeters,
            currentPrimaryDistanceMeters);

        if (!usingExcursionTrack
            && escortLatitudeSnapshot.HasValue
            && escortLongitudeSnapshot.HasValue)
        {
            var escortPoint = new MapPoint(escortLongitudeSnapshot.Value, escortLatitudeSnapshot.Value, SpatialReferences.Wgs84);
            var distanceToEscortMeters = CalculateDistanceMeters(currentPrimaryPoint, escortPoint);
            if (distanceToEscortMeters > SecurityZoneRadiusMeters)
            {
                speedMetersPerSecond *= OutsideZoneNonExcursionSpeedMultiplier;
            }
        }

        var nextUnwrappedPrimaryDistanceMeters = primaryTrackDistanceMeters + speedMetersPerSecond * deltaSeconds;
        if (nextUnwrappedPrimaryDistanceMeters >= primaryTrackSnapshot.LengthMeters)
        {
            completedPrimaryLoops += Math.Max(1, (int)(nextUnwrappedPrimaryDistanceMeters / primaryTrackSnapshot.LengthMeters));
        }

        primaryTrackDistanceMeters = NormalizeDistance(nextUnwrappedPrimaryDistanceMeters, primaryTrackSnapshot.LengthMeters);

        if (!usingExcursionTrack)
        {
            activeRouteTrack = primaryTrackSnapshot;
            activeRouteTrackDistanceMeters = primaryTrackDistanceMeters;

            if (pendingExcursionTrack is not null && pendingExcursionStartJunction is not null)
            {
                if (HasPassedDistance(
                    previousPrimaryDistanceMeters,
                    primaryTrackDistanceMeters,
                    pendingExcursionStartJunction.PrimaryDistanceMeters,
                    direction: 1,
                    primaryTrackSnapshot.LengthMeters))
                {
                    usingExcursionTrack = true;
                    activeRouteTrack = pendingExcursionTrack;
                    activeExcursionJunctions = pendingExcursionJunctions;
                    activeExcursionStartJunction = pendingExcursionStartJunction;
                    activeExcursionHasAlternateJunction = activeExcursionJunctions
                        .Any(junction =>
                            Math.Abs(junction.ExcursionDistanceMeters - pendingExcursionStartJunction.ExcursionDistanceMeters) > 8
                            && Math.Abs(junction.PrimaryDistanceMeters - pendingExcursionStartJunction.PrimaryDistanceMeters) > 8);
                    excursionDistanceSinceStartMeters = 0;

                    primaryTrackDistanceMeters = pendingExcursionStartJunction.PrimaryDistanceMeters;
                    activeRouteTrackDistanceMeters = pendingExcursionStartJunction.ExcursionDistanceMeters;
                    activeRouteTrackDirection = 1;

                    pendingExcursionTrack = null;
                    pendingExcursionStartJunction = null;
                    pendingExcursionJunctions = [];
                    speedMetersPerSecond = baseSpeedMetersPerSecond * 1.5;
                    recoveryBoostUntilUtc = nowUtc.AddSeconds(18);
                }
            }

            if (pendingExcursionTrack is null && excursionTracksSnapshot.Count > 0)
            {
                var selectedExcursion = SelectEncounteredExcursionStart(
                    primaryTrackSnapshot,
                    excursionTracksSnapshot,
                    previousPrimaryDistanceMeters,
                    primaryTrackDistanceMeters,
                    speedMetersPerSecond,
                    deltaSeconds,
                    nowUtc);

                if (selectedExcursion is not null)
                {
                    pendingExcursionTrack = selectedExcursion.Track;
                    pendingExcursionStartJunction = selectedExcursion.StartJunction;
                    pendingExcursionJunctions = selectedExcursion.Junctions;
                }
            }
        }

        if (usingExcursionTrack && activeRouteTrack is not null)
        {
            var previousExcursionDistance = activeRouteTrackDistanceMeters;
            speedMetersPerSecond = baseSpeedMetersPerSecond * 1.5;
            var excursionStepMeters = speedMetersPerSecond * deltaSeconds;

            activeRouteTrackDistanceMeters = NormalizeDistance(
                activeRouteTrackDistanceMeters + excursionStepMeters * activeRouteTrackDirection,
                activeRouteTrack.LengthMeters);
            excursionDistanceSinceStartMeters += Math.Abs(excursionStepMeters);

            var reachedRejoin = TryFindRejoinJunction(
                previousExcursionDistance,
                activeRouteTrackDistanceMeters,
                activeRouteTrackDirection,
                activeRouteTrack.LengthMeters,
                activeExcursionJunctions,
                activeExcursionStartJunction,
                excursionDistanceSinceStartMeters,
                allowStartJunction: excursionDistanceSinceStartMeters >= Math.Max(ExcursionForcedRejoinMinMeters, activeRouteTrack.LengthMeters * 1.15));

            if (reachedRejoin is not null)
            {
                primaryTrackDistanceMeters = reachedRejoin.PrimaryDistanceMeters;
                usingExcursionTrack = false;
                activeRouteTrack = primaryTrackSnapshot;
                activeRouteTrackDistanceMeters = primaryTrackDistanceMeters;
                activeExcursionJunctions = [];
                activeExcursionStartJunction = null;
                activeExcursionHasAlternateJunction = false;
                excursionDistanceSinceStartMeters = 0;
                recoveryBoostUntilUtc = nowUtc.AddSeconds(16);
                excursionSelectionBlockedUntilUtc = nowUtc.Add(ExcursionRearmCooldown);
                excursionRearmStartPrimaryDistanceMeters = primaryTrackDistanceMeters;
            }
        }
        else if (activeRouteTrack is null)
        {
            activeRouteTrack = primaryTrackSnapshot;
            activeRouteTrackDistanceMeters = primaryTrackDistanceMeters;
        }

        var renderingTrack = activeRouteTrack ?? primaryTrackSnapshot;
        var renderingDistance = usingExcursionTrack
            ? Math.Clamp(activeRouteTrackDistanceMeters, 0, renderingTrack.LengthMeters)
            : primaryTrackDistanceMeters;

        var (targetPoint, bearingDegrees) = InterpolateAlongRoute(renderingTrack.Vertices, renderingTrack.SegmentLengthsMeters, renderingDistance);

        double finalRouteDistance = renderingDistance;
        lock (gate)
        {
            if (publishedRouteTrack is not null
                && ReferenceEquals(publishedRouteTrack, renderingTrack)
                && publishedRouteDistanceMeters.HasValue)
            {
                finalRouteDistance = StepTowardDistance(
                    publishedRouteDistanceMeters.Value,
                    renderingDistance,
                    renderingTrack.LengthMeters,
                    MaxStepDistanceMeters,
                    usingExcursionTrack ? activeRouteTrackDirection : 1);
            }
            else
            {
                MapPoint? previousPoint = latestLocation?.Position;
                if (previousPoint is null && centerLatitude.HasValue && centerLongitude.HasValue)
                {
                    var centerPoint = new MapPoint(centerLongitude.Value, centerLatitude.Value, SpatialReferences.Wgs84);
                    if (CalculateDistanceMeters(centerPoint, targetPoint) <= StartupAnchorMaxDistanceMeters)
                    {
                        previousPoint = centerPoint;
                    }
                }

                if (previousPoint is not null)
                {
                    var nearestDistance = FindNearestDistanceOnTrack(renderingTrack, previousPoint);
                    var (nearestPoint, _) = InterpolateAlongRoute(
                        renderingTrack.Vertices,
                        renderingTrack.SegmentLengthsMeters,
                        nearestDistance);

                    if (CalculateDistanceMeters(previousPoint, nearestPoint) <= StartupAnchorMaxDistanceMeters)
                    {
                        finalRouteDistance = StepTowardDistance(
                            nearestDistance,
                            renderingDistance,
                            renderingTrack.LengthMeters,
                            MaxStepDistanceMeters,
                            usingExcursionTrack ? activeRouteTrackDirection : 1);
                    }
                }
            }

            publishedRouteTrack = renderingTrack;
            publishedRouteDistanceMeters = finalRouteDistance;
        }

        var (finalPoint, finalBearingDegrees) = InterpolateAlongRoute(
            renderingTrack.Vertices,
            renderingTrack.SegmentLengthsMeters,
            finalRouteDistance);

        bearingDegrees = finalBearingDegrees;

        var location = new RuntimeLocation(finalPoint, SimulatedHorizontalAccuracyMeters, speedMetersPerSecond, bearingDegrees, false);
        PublishLocation(location);

        return true;
    }

    private void PublishLocation(RuntimeLocation location)
    {
        lock (gate)
        {
            latestLocation = location;
        }

        if (Status == LocationDataSourceStatus.Started)
        {
            UpdateLocation(location);
        }
    }

    private async Task TryInitializeRouteAsync()
    {
        if (string.IsNullOrWhiteSpace(routeDataPath))
        {
            routeReady = false;
            throw new InvalidOperationException("Route geodatabase path is required for simulated route motion but was not provided.");
        }

        try
        {
            var loadedTracks = await LoadRouteTracksAsync(routeDataPath, routeLayerName);
            if (loadedTracks.Count == 0)
            {
                routeReady = false;
                throw new InvalidOperationException(
                    $"No usable polyline routes were loaded from geodatabase '{routeDataPath}' (layer hint: '{routeLayerName ?? "<any>"}').");
            }

            var orderedTracks = loadedTracks.OrderByDescending(track => track.LengthMeters).ToList();
            var hasExplicitSimRole = orderedTracks.Any(track => track.HasExplicitSimulationRole);
            var selectedPrimaryTrack = hasExplicitSimRole
                ? orderedTracks.FirstOrDefault(track => track.SimulationRole == RouteSimulationRole.Main)
                : orderedTracks.FirstOrDefault(track => !track.IsExcursion);

            selectedPrimaryTrack ??= orderedTracks.FirstOrDefault();
            if (selectedPrimaryTrack is null)
            {
                routeReady = false;
                throw new InvalidOperationException(
                    $"Unable to select a primary route from geodatabase '{routeDataPath}' (layer hint: '{routeLayerName ?? "<any>"}').");
            }

            ApplyInitializedRouteState(orderedTracks, selectedPrimaryTrack, hasExplicitSimRole);
        }
        catch
        {
            routeReady = false;
            throw;
        }
    }

    private void ApplyInitializedRouteState(List<RouteTrack> orderedTracks, RouteTrack selectedPrimaryTrack, bool hasExplicitSimRole)
    {
        lock (gate)
        {
            routeTracks = orderedTracks;
            primaryRouteTrack = selectedPrimaryTrack;
            if (hasExplicitSimRole)
            {
                excursionRouteTracks = orderedTracks
                    .Where(track => !ReferenceEquals(track, selectedPrimaryTrack)
                        && track.SimulationRole == RouteSimulationRole.Excursion)
                    .ToList();
            }
            else
            {
                excursionRouteTracks = orderedTracks
                    .Where(track => !ReferenceEquals(track, selectedPrimaryTrack)
                        && (track.IsExcursion || !orderedTracks.Any(t => t.IsExcursion)))
                    .ToList();
            }

            activeRouteTrack = selectedPrimaryTrack;
            activeRouteTrackDistanceMeters = NormalizeDistance(primaryTrackDistanceMeters, selectedPrimaryTrack.LengthMeters);
            primaryTrackDistanceMeters = activeRouteTrackDistanceMeters;
            usingExcursionTrack = false;
            activeExcursionJunctions = [];
            activeExcursionStartJunction = null;
            activeExcursionHasAlternateJunction = false;
            excursionDistanceSinceStartMeters = 0;
            pendingExcursionTrack = null;
            pendingExcursionStartJunction = null;
            pendingExcursionJunctions = [];
            recoveryBoostUntilUtc = DateTimeOffset.MinValue;
            lastRouteStepUtc = null;
            completedPrimaryLoops = 0;
            publishedRouteTrack = null;
            publishedRouteDistanceMeters = null;
            excursionSelectionBlockedUntilUtc = DateTimeOffset.MinValue;
            excursionRearmStartPrimaryDistanceMeters = null;
            routeReady = true;
        }
    }

    private static (MapPoint point, double bearingDegrees) InterpolateAlongRoute(
        IReadOnlyList<MapPoint> vertices,
        IReadOnlyList<double> segmentLengthsMeters,
        double distanceMeters)
    {
        var remaining = distanceMeters;
        var segmentIndex = 0;

        while (segmentIndex < segmentLengthsMeters.Count - 1 && remaining > segmentLengthsMeters[segmentIndex])
        {
            remaining -= segmentLengthsMeters[segmentIndex];
            segmentIndex++;
        }

        var start = vertices[segmentIndex];
        var end = vertices[Math.Min(segmentIndex + 1, vertices.Count - 1)];
        var segmentLength = Math.Max(segmentLengthsMeters[segmentIndex], 0.0001);
        var t = Math.Clamp(remaining / segmentLength, 0, 1);

        var latitude = start.Y + (end.Y - start.Y) * t;
        var longitude = start.X + (end.X - start.X) * t;
        var bearing = ComputeBearingDegrees(start.Y, start.X, end.Y, end.X);

        return (new MapPoint(longitude, latitude, SpatialReferences.Wgs84), bearing);
    }

}
