using Esri.ArcGISRuntime.Geometry;

namespace FieldMobileApp.Location;

public sealed partial class SimulationLocationDataSource
{
    private sealed record RouteTrack(
        List<MapPoint> Vertices,
        List<double> SegmentLengthsMeters,
        double LengthMeters,
        bool IsExcursion,
        string? Name,
        RouteSimulationRole SimulationRole,
        bool HasExplicitSimulationRole,
        Polyline Polyline);

    private sealed record RouteJunction(double PrimaryDistanceMeters, double ExcursionDistanceMeters, MapPoint Point);

    private sealed record SelectedExcursionStart(RouteTrack Track, RouteJunction StartJunction, List<RouteJunction> Junctions);

    private enum RouteSimulationRole
    {
        Unknown = 0,
        Main = 1,
        Excursion = 2
    }
}
