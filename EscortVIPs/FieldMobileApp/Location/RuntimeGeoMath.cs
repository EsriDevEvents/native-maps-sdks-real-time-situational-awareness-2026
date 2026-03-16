using Esri.ArcGISRuntime.Geometry;

namespace FieldMobileApp.Location;

internal static class RuntimeGeoMath
{
    public static double CalculateDistanceMeters(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        var fromPoint = new MapPoint(longitude1, latitude1, SpatialReferences.Wgs84);
        var toPoint = new MapPoint(longitude2, latitude2, SpatialReferences.Wgs84);
        return CalculateDistanceMeters(fromPoint, toPoint);
    }

    public static double CalculateDistanceMeters(MapPoint fromPoint, MapPoint toPoint)
    {
        try
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
        catch
        {
            return 0.0;
        }
    }

    public static double ComputeBearingDegrees(MapPoint fromPoint, MapPoint toPoint)
    {
        try
        {
            var start = NormalizeForGeodetic(fromPoint);
            var end = NormalizeForGeodetic(toPoint);
            var geodeticResult = GeometryEngine.DistanceGeodetic(
                start,
                end,
                LinearUnits.Meters,
                AngularUnits.Degrees,
                GeodeticCurveType.Geodesic);

            return double.IsNaN(geodeticResult.Azimuth1)
                ? 0.0
                : NormalizeBearingDegrees(geodeticResult.Azimuth1);
        }
        catch
        {
            return 0.0;
        }
    }

    public static string ComputeDirectionCardinal(double fromLatitude, double fromLongitude, double toLatitude, double toLongitude)
    {
        var fromPoint = new MapPoint(fromLongitude, fromLatitude, SpatialReferences.Wgs84);
        var toPoint = new MapPoint(toLongitude, toLatitude, SpatialReferences.Wgs84);
        var bearing = ComputeBearingDegrees(fromPoint, toPoint);

        var cardinals = new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        var index = (int)Math.Round(bearing / 45.0, MidpointRounding.AwayFromZero) % 8;
        return cardinals[index];
    }

    public static MapPoint MovePointGeodetic(MapPoint startPoint, double distanceMeters, double bearingDegrees)
    {
        try
        {
            var normalizedStart = NormalizeForGeodetic(startPoint);
            var movedPoints = GeometryEngine.MoveGeodetic(
                new[] { normalizedStart },
                distanceMeters,
                LinearUnits.Meters,
                bearingDegrees,
                AngularUnits.Degrees,
                GeodeticCurveType.Geodesic);

            foreach (var movedPoint in movedPoints)
            {
                return movedPoint;
            }
        }
        catch
        {
        }

        return startPoint;
    }

    private static MapPoint NormalizeForGeodetic(MapPoint point)
    {
        if (point.SpatialReference is null)
        {
            return new MapPoint(point.X, point.Y, SpatialReferences.Wgs84);
        }

        if (point.SpatialReference == SpatialReferences.Wgs84)
        {
            return point;
        }

        return GeometryEngine.Project(point, SpatialReferences.Wgs84) as MapPoint
            ?? new MapPoint(point.X, point.Y, SpatialReferences.Wgs84);
    }

    private static double NormalizeBearingDegrees(double bearingDegrees)
    {
        var normalized = bearingDegrees % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }
}
