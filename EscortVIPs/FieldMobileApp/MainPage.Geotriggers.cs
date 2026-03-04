using CommandMessaging;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Geotriggers;
using Esri.ArcGISRuntime.Location;

namespace FieldMobileApp;

public partial class MainPage
{
    private const double EscortPerimeterRadiusMeters = 25;
    private const double WarningRingRadiusMeters = EscortPerimeterRadiusMeters * 0.75;
    private static readonly TimeSpan VipStatusTransitionMinimumInterval = TimeSpan.FromSeconds(1.5);

    private readonly SemaphoreSlim escortFenceUpdateGate = new(1, 1);
    private FeatureCollectionTable? escortPerimeterFenceTable;
    private Feature? escortPerimeterFence;
    private GeotriggerMonitor? escortPerimeterMonitor;
    private GeotriggerMonitor? warningRingMonitor;
    private bool? vipInsideEscortPerimeter;
    private bool? vipInsideWarningRing;
    private string? vipStatus;
    private long geotriggerEventCount;
    private DateTimeOffset lastVipStatusChangeUtc = DateTimeOffset.MinValue;

    private async Task EnsurePerimeterMonitorsAsync(string role)
    {
        if (!string.Equals(role, "VIP", StringComparison.OrdinalIgnoreCase)
            || locationDataSource is null
            || (escortPerimeterMonitor is not null && warningRingMonitor is not null))
            return;

        escortPerimeterFenceTable ??= new FeatureCollectionTable(
                [Field.CreateString("FenceId", "Fence Id", 64)],
                GeometryType.Point,
                SpatialReferences.Wgs84);

        escortPerimeterMonitor = CreateFenceMonitor(
            locationDataSource,
            escortPerimeterFenceTable,
            EscortPerimeterRadiusMeters,
            OnEscortPerimeterNotification);

        warningRingMonitor = CreateFenceMonitor(
            locationDataSource,
            escortPerimeterFenceTable,
            WarningRingRadiusMeters,
            OnWarningRingNotification);

        await escortPerimeterMonitor.StartAsync();
        await warningRingMonitor.StartAsync();
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            ApplyStatusVisual(null, "Waiting for escort perimeter");
        });
    }

    private async void OnEscortPerimeterNotification(object? sender, GeotriggerNotificationInfo notificationInfo)
    {
        if (notificationInfo is not FenceGeotriggerNotificationInfo fenceNotification)
            return;

        await HandleRingNotificationAsync(fenceNotification, isWarningRing: false);
    }

    private async void OnWarningRingNotification(object? sender, GeotriggerNotificationInfo notificationInfo)
    {
        if (notificationInfo is not FenceGeotriggerNotificationInfo fenceNotification)
            return;

        await HandleRingNotificationAsync(fenceNotification, isWarningRing: true);
    }

    private async Task HandleRingNotificationAsync(FenceGeotriggerNotificationInfo fenceNotification, bool isWarningRing)
    {
        bool? isInsideFence = fenceNotification.FenceNotificationType switch
        {
            FenceNotificationType.Entered => true,
            FenceNotificationType.Exited => false,
            _ => (bool?)null
        };

        if (!isInsideFence.HasValue)
            return;

        if (isWarningRing)
            vipInsideWarningRing = isInsideFence.Value;
        else
            vipInsideEscortPerimeter = isInsideFence.Value;

        await ApplyVipStatusFromRingsAsync(allowThrottle: true, countGeotriggerEvent: true);
    }

    private string? GetVipStatusFromRings()
    {
        if (!vipInsideEscortPerimeter.HasValue || !vipInsideWarningRing.HasValue)
            return null;

        if (!vipInsideEscortPerimeter.Value)
            return "Out";

        return vipInsideWarningRing.Value ? "In" : "Danger";
    }

    private async Task UpdateEscortFenceAsync(double latitude, double longitude)
    {
        if (escortPerimeterFenceTable is null)
            return;

        await escortFenceUpdateGate.WaitAsync();
        try
        {
            var geometry = new MapPoint(longitude, latitude, SpatialReferences.Wgs84);
            if (escortPerimeterFence is null)
            {
                escortPerimeterFence = escortPerimeterFenceTable.CreateFeature(
                    [new KeyValuePair<string, object?>("FenceId", "EscortLead")],
                    geometry);

                await escortPerimeterFenceTable.AddFeatureAsync(escortPerimeterFence);
            }
            else
            {
                escortPerimeterFence.Geometry = geometry;
                await escortPerimeterFenceTable.UpdateFeatureAsync(escortPerimeterFence);
            }
        }
        finally
        {
            escortFenceUpdateGate.Release();
        }

        await SyncVipStatusAsync();
    }

    private async Task SyncVipStatusAsync()
    {
        if (!string.Equals(configuredRole, "VIP", StringComparison.OrdinalIgnoreCase))
            return;

        await ApplyVipStatusFromRingsAsync(allowThrottle: false, countGeotriggerEvent: false);
    }

    private async Task ApplyVipStatusFromRingsAsync(bool allowThrottle, bool countGeotriggerEvent)
    {
        var nextStatus = GetVipStatusFromRings();
        if (nextStatus is null)
            return;

        await ApplyVipStatusAsync(nextStatus, allowThrottle, countGeotriggerEvent);
    }

    private static GeotriggerMonitor CreateFenceMonitor(
        LocationDataSource locationSource,
        FeatureCollectionTable fenceTable,
        double radiusMeters,
        EventHandler<GeotriggerNotificationInfo> notificationHandler)
    {
        var fenceParameters = new FeatureFenceParameters(fenceTable, radiusMeters);
        var geotrigger = new FenceGeotrigger(
            new LocationGeotriggerFeed(locationSource),
            FenceRuleType.EnterOrExit,
            fenceParameters)
        {
            FeedAccuracyMode = FenceGeotriggerFeedAccuracyMode.UseGeometryWithAccuracy,
            EnterExitSpatialRelationship = FenceEnterExitSpatialRelationship.EnterContainsAndExitDoesNotIntersect
        };

        var monitor = new GeotriggerMonitor(geotrigger);
        monitor.Notification += notificationHandler;
        return monitor;
    }

    private async Task ApplyVipStatusAsync(string nextStatus, bool allowThrottle, bool countGeotriggerEvent)
    {
        if (string.IsNullOrWhiteSpace(nextStatus))
            return;

        var changed = !string.Equals(vipStatus, nextStatus, StringComparison.OrdinalIgnoreCase);
        if (changed && allowThrottle && DateTimeOffset.UtcNow - lastVipStatusChangeUtc < VipStatusTransitionMinimumInterval)
            return;

        vipStatus = nextStatus;
        if (changed)
        {
            lastVipStatusChangeUtc = DateTimeOffset.UtcNow;
        }

        if (countGeotriggerEvent)
        {
            Interlocked.Increment(ref geotriggerEventCount);
        }

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            ApplyStatusVisual(nextStatus, null);
        });

        if (changed)
        {
            await PublishVipStatusAsync(nextStatus);
        }
    }

    private async Task PublishVipStatusAsync(string nextStatus)
    {
        var deviceId = connectedDeviceId;
        var sessionId = connectedSessionId;
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(sessionId))
            return;

        var statusEnvelope = MessageSerializer.CreateEnvelope(
            MessageTypes.StatusUpdate,
            sessionId,
            deviceId,
            new StatusUpdatePayload(nextStatus));

        var sent = await SendAsync(statusEnvelope, receiveCts?.Token ?? CancellationToken.None);
        if (!sent)
        {
            LogDiagnostic("PublishVipStatusAsync skipped because websocket is not open.");
        }

        if (simulatedMode)
        {
            var simulationSent = await SendSimulationAsync(statusEnvelope, receiveCts?.Token ?? CancellationToken.None);
            if (!simulationSent)
            {
                LogDiagnostic("PublishVipStatusAsync to simulation engine skipped because simulation websocket is not open.");
            }
        }
    }
}
