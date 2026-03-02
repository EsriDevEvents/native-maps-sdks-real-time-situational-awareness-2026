using Esri.ArcGISRuntime.Geometry;

namespace FieldMobileApp.Location;

public sealed partial class SimulationLocationDataSource
{
    private static MapPoint MovePointGeodetic(MapPoint startPoint, double distanceMeters, double bearingDegrees) =>
        RuntimeGeoMath.MovePointGeodetic(startPoint, distanceMeters, bearingDegrees);

    private static double CalculateDistanceMeters(MapPoint fromPoint, MapPoint toPoint) =>
        RuntimeGeoMath.CalculateDistanceMeters(fromPoint, toPoint);

    private static double ComputeBearingDegrees(double fromLatitude, double fromLongitude, double toLatitude, double toLongitude)
    {
        var fromPoint = new MapPoint(fromLongitude, fromLatitude, SpatialReferences.Wgs84);
        var toPoint = new MapPoint(toLongitude, toLatitude, SpatialReferences.Wgs84);
        return RuntimeGeoMath.ComputeBearingDegrees(fromPoint, toPoint);
    }

    private static double NormalizeBearingDegrees(double bearingDegrees)
    {
        var normalized = bearingDegrees % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static double RadiansToDegrees(double radians) => radians * 180.0 / Math.PI;

}
