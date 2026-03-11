using CommandMessaging;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.RealTime;

namespace FieldMobileApp.RealTime;

public sealed class VipDynamicEntityDataSource : DynamicEntityDataSource
{
    public const string EntityIdFieldName = "trackId";
    public const string StatusFieldName = "status";

    protected override Task<DynamicEntityDataSourceInfo> OnLoadAsync()
    {
        // Define the schema for the VIP dynamic entities (ID and Status in this case)
        var fields = new List<Field>
        {
            Field.CreateString(EntityIdFieldName, "Track ID", 64),
            Field.CreateString(StatusFieldName, "Status", 32)
        };

        var info = new DynamicEntityDataSourceInfo(EntityIdFieldName, fields);
        return Task.FromResult(info);
    }

    protected override Task OnConnectAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    protected override Task OnDisconnectAsync()
    {
        return Task.CompletedTask;
    }

    public void PublishVipTelemetry(VipTelemetryPayload payload)
    {
        var normalizedDeviceId = NormalizeDeviceId(payload.DeviceId);
        if (normalizedDeviceId is null)
            return;

        var point = new MapPoint(payload.Longitude, payload.Latitude, SpatialReferences.Wgs84);
        var attributes = new Dictionary<string, object?>
        {
            [EntityIdFieldName] = normalizedDeviceId,
            [StatusFieldName] = payload.Status
        };

        // AddObservation will push a new DynamicEntityObservation for the current track
        AddObservation(point, attributes);
    }

    private static string? NormalizeDeviceId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        return deviceId.Trim();
    }
}
