using CommandDashboard.Models;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.RealTime;

namespace CommandDashboard.RealTime;

public sealed class TourDynamicEntityDataSource : DynamicEntityDataSource
{
    public const string EntityIdFieldName = "trackId";

    protected override Task<DynamicEntityDataSourceInfo> OnLoadAsync()
    {
        var fields = new List<Field>
        {
            Field.CreateString(EntityIdFieldName, "Track ID", 64),
            Field.CreateString("name", "Name", 128),
            Field.CreateString("role", "Role", 32),
            Field.CreateString("status", "Status", 32),
            Field.CreateDouble("distanceMeters", "Distance (m)"),
            Field.CreateString("direction", "Direction", 8)
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

    public void PublishUnit(FieldUnitStatus unit)
    {
        var attributes = new Dictionary<string, object?>
        {
            [EntityIdFieldName] = unit.DisplayName,
            ["name"] = unit.DisplayName,
            ["role"] = unit.Role.ToString(),
            ["status"] = unit.Status.ToString(),
            ["distanceMeters"] = unit.DistanceMeters,
            ["direction"] = unit.DirectionToEscort
        };

        var point = new MapPoint(unit.Longitude, unit.Latitude, SpatialReferences.Wgs84);
        AddObservation(point, attributes);
    }
}