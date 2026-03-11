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

        // Create a single fence table that will hold the perimeter fence for both geotrigger monitors
        // - there will be a single fence feature in the table that gets updated with the Escort's current location.
        // - both geotriggers share the same location and only differ by radius.
        escortPerimeterFenceTable ??= new FeatureCollectionTable(
                [Field.CreateString("FenceId", "Fence Id", 64)],
                GeometryType.Point,
                SpatialReferences.Wgs84);

        // Create the geotrigger monitors for the escort perimeter and warning ring
        escortPerimeterMonitor = CreateFenceGeotriggerMonitor(
            locationDataSource,
            escortPerimeterFenceTable,
            EscortPerimeterRadiusMeters,
            OnEscortPerimeterNotification);

        warningRingMonitor = CreateFenceGeotriggerMonitor(
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

    private static GeotriggerMonitor CreateFenceGeotriggerMonitor(
        LocationDataSource locationSource,
        FeatureCollectionTable fenceTable,
        double radiusMeters,
        EventHandler<GeotriggerNotificationInfo> notificationHandler)
    {
        // Feed / Fence / Rule - Geotrigger setup
        var feed = new LocationGeotriggerFeed(locationSource);
        var fenceParameters = new FeatureFenceParameters(fenceTable, radiusMeters);
        var geotrigger = new FenceGeotrigger(feed, FenceRuleType.EnterOrExit, fenceParameters)
        {
            // Only signal the geotrigger when the GPS point and its horizontal accuracy is completely inside / outside the fence.
            // - this helps to reduce jitter when the VIP is near the edge of the perimeter.
            FeedAccuracyMode = FenceGeotriggerFeedAccuracyMode.UseGeometryWithAccuracy,
            EnterExitSpatialRelationship = FenceEnterExitSpatialRelationship.EnterContainsAndExitDoesNotIntersect
        };

        // Create the monitor and wire up the notification event handler
        var monitor = new GeotriggerMonitor(geotrigger);
        monitor.Notification += notificationHandler;
        return monitor;
    }

    private async void OnEscortPerimeterNotification(object? sender, GeotriggerNotificationInfo notificationInfo)
    {
        await HandleGeotriggerNotificationAsync(notificationInfo, isWarningRing: false);
    }

    private async void OnWarningRingNotification(object? sender, GeotriggerNotificationInfo notificationInfo)
    {
        await HandleGeotriggerNotificationAsync(notificationInfo, isWarningRing: true);
    }

    private async Task HandleGeotriggerNotificationAsync(GeotriggerNotificationInfo notificationInfo, bool isWarningRing)
    {
        if (notificationInfo is not FenceGeotriggerNotificationInfo fenceNotification)
            return;

        // determine the official status of the VIP (inside, outside, or warning)
        // - based on the geotrigger notification type and which monitor signaled it.
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

        if (!vipInsideEscortPerimeter.HasValue || !vipInsideWarningRing.HasValue)
            return;

        var nextStatus = !vipInsideEscortPerimeter.Value
            ? "Out"
            : vipInsideWarningRing.Value ? "In" : "Warning";

        await ApplyVipStatusAsync(nextStatus);
    }

    private async Task ApplyVipStatusAsync(string nextStatus)
    {
        if (string.IsNullOrWhiteSpace(nextStatus))
            return;

        var changed = !string.Equals(vipStatus, nextStatus, StringComparison.OrdinalIgnoreCase);

        vipStatus = nextStatus;

        // Update the UI with the new VIP status
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            ApplyStatusVisual(nextStatus, null);
        });

        if (changed)
        {
            // Play an audio cue to alert the user of the VIP status change
            PlayVipStatusChangedAudioCue(nextStatus);

            // Broadcast the VIP status change onto the network for escort and dashboard
            await BroadcastVipStatusAsync(nextStatus);

            if (vipDirectiveActive
                && ShouldAutoResumeFromStatus(nextStatus)
                && (string.Equals(vipDirectiveSignal, "STOP", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(vipDirectiveSignal, "HURRY", StringComparison.OrdinalIgnoreCase)))
            {
                await BroadcastVipDirectiveResetAsync();
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    UpdateVipCommandBanner("RESUME", "Escort reached. Resume normal pace.", isActive: false);
                });
            }
        }
    }

    private void PlayVipStatusChangedAudioCue(string nextStatus)
    {
        if (string.IsNullOrWhiteSpace(nextStatus))
            return;

        _ = Task.Run(() =>
        {
#if WINDOWS
            try
            {
                var normalized = nextStatus.Trim();
                if (string.Equals(normalized, "In", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Beep(880, 120);
                    return;
                }

                if (string.Equals(normalized, "Warning", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Beep(740, 140);
                    Console.Beep(660, 140);
                    return;
                }

                if (string.Equals(normalized, "Out", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Beep(440, 180);
                    Console.Beep(349, 220);
                }
            }
            catch (Exception ex)
            {
                LogDiagnostic($"VIP status audio cue failed: {ex.Message}");
            }
#endif
        });
    }

    private async Task BroadcastVipStatusAsync(string nextStatus)
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
            LogDiagnostic("BroadcastVipStatusAsync skipped because websocket is not open.");
        }

        if (simulatedMode)
        {
            var simulationSent = await SendSimulationAsync(statusEnvelope, receiveCts?.Token ?? CancellationToken.None);
            if (!simulationSent)
            {
                LogDiagnostic("BroadcastVipStatusAsync to simulation engine skipped because simulation websocket is not open.");
            }
        }
    }

    private async Task BroadcastVipDirectiveResetAsync()
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
            LogDiagnostic("BroadcastVipDirectiveResetAsync skipped because websocket is not open.");
        }

        if (simulatedMode)
        {
            var simulationSent = await SendSimulationAsync(resetEnvelope, receiveCts?.Token ?? CancellationToken.None);
            if (!simulationSent)
            {
                LogDiagnostic("BroadcastVipDirectiveResetAsync to simulation engine skipped because simulation websocket is not open.");
            }
        }
    }

    private async Task UpdateEscortFenceAsync(double latitude, double longitude)
    {
        if (escortPerimeterFenceTable is null)
            return;

        await escortFenceUpdateGate.WaitAsync();
        try
        {
            // update the geometry of the existing fence feature, or create it if it doesn't exist yet.
            // The geotrigger monitor will automatically pick up the geometry change and update the fence location accordingly.
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
