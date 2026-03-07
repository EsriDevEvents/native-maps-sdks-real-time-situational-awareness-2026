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

    private readonly SemaphoreSlim escortFenceUpdateGate = new(1, 1);
    private FeatureCollectionTable? escortPerimeterFenceTable;
    private Feature? escortPerimeterFence;
    private GeotriggerMonitor? escortPerimeterMonitor;
    private GeotriggerMonitor? warningRingMonitor;
    private bool? vipInsideEscortPerimeter;
    private bool? vipInsideWarningRing;
    private string? vipStatus;

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

        await ApplyVipStatusFromRingsAsync();
    }

    private string? GetVipStatusFromRings()
    {
        if (!vipInsideEscortPerimeter.HasValue || !vipInsideWarningRing.HasValue)
            return null;

        if (!vipInsideEscortPerimeter.Value)
            return "Out";

        return vipInsideWarningRing.Value ? "In" : "Warning";
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

        await ApplyVipStatusFromRingsAsync();
    }

    private async Task ApplyVipStatusFromRingsAsync()
    {
        var nextStatus = GetVipStatusFromRings();
        if (nextStatus is null)
            return;

        await ApplyVipStatusAsync(nextStatus);
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

    private async Task ApplyVipStatusAsync(string nextStatus)
    {
        if (string.IsNullOrWhiteSpace(nextStatus))
            return;

        var changed = !string.Equals(vipStatus, nextStatus, StringComparison.OrdinalIgnoreCase);

        vipStatus = nextStatus;

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            ApplyStatusVisual(nextStatus, null);
        });

        if (changed)
        {
            await PublishVipStatusAsync(nextStatus);

            if (vipDirectiveActive
                && ShouldAutoResumeFromStatus(nextStatus)
                && (string.Equals(vipDirectiveSignal, "STOP", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(vipDirectiveSignal, "HURRY", StringComparison.OrdinalIgnoreCase)))
            {
                await PublishVipDirectiveResetAsync();
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    UpdateVipCommandBanner("RESUME", "Escort reached. Resume normal pace.", isActive: false);
                });
            }
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

    private async Task PublishVipDirectiveResetAsync()
    {
        var deviceId = connectedDeviceId;
        var sessionId = connectedSessionId;
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(sessionId))
            return;

        var resetEnvelope = MessageSerializer.CreateEnvelope(
            MessageTypes.VipControl,
            sessionId,
            deviceId,
            new VipControlPayload(
                DeviceId: deviceId,
                Signal: "RESUME",
                Message: "Escort reached. Resume normal pace.",
                IsActive: false));

        var sent = await SendAsync(resetEnvelope, receiveCts?.Token ?? CancellationToken.None);
        if (!sent)
        {
            LogDiagnostic("PublishVipDirectiveResetAsync skipped because websocket is not open.");
        }

        if (simulatedMode)
        {
            var simulationSent = await SendSimulationAsync(resetEnvelope, receiveCts?.Token ?? CancellationToken.None);
            if (!simulationSent)
            {
                LogDiagnostic("PublishVipDirectiveResetAsync to simulation engine skipped because simulation websocket is not open.");
            }
        }
    }

    private static bool ShouldAutoResumeFromStatus(string? status)
    {
        var normalized = status?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        // Keep HOLD/HURRY active through warning-edge states; clear only after full rejoin.
        return string.Equals(normalized, "In", StringComparison.OrdinalIgnoreCase);
    }
}
