using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Location;
using RuntimeLocation = Esri.ArcGISRuntime.Location.Location;

namespace FieldMobileApp.Location;

public sealed partial class SimulationLocationDataSource : LocationDataSource
{
    private const double SimulatedHorizontalAccuracyMeters = 1.5;
    private const double BaseLatitude = 34.0556;
    private const double BaseLongitude = -117.1825;

    private readonly Lock gate = new();
    private readonly string role;
    private readonly string deviceId;
    private RuntimeLocation? latestLocation;
    private double? centerLatitude;
    private double? centerLongitude;

    public SimulationLocationDataSource(string role, string deviceId)
    {
        this.role = role;
        this.deviceId = deviceId;

        var (latitude, longitude) = GetInitialAssignedLocation(this.role, this.deviceId);
        centerLatitude = latitude;
        centerLongitude = longitude;
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
            PublishAssignedLocation();
        }
    }

    protected override async Task OnStartAsync()
    {
        PublishAssignedLocation();
        await Task.CompletedTask;
    }

    protected override Task OnStopAsync()
    {
        return Task.CompletedTask;
    }

    private void PublishAssignedLocation()
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

    private static (double latitude, double longitude) GetInitialAssignedLocation(string role, string deviceId)
    {
        if (string.Equals(role, "Escort", StringComparison.OrdinalIgnoreCase))
            return (BaseLatitude, BaseLongitude);

        var hash = Math.Abs(deviceId.GetHashCode(StringComparison.OrdinalIgnoreCase));
        var bearingDegrees = hash % 360;
        var basePoint = new MapPoint(BaseLongitude, BaseLatitude, SpatialReferences.Wgs84);
        var offsetPoint = RuntimeGeoMath.MovePointGeodetic(basePoint, 22.0, bearingDegrees);
        return (offsetPoint.Y, offsetPoint.X);
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
}
