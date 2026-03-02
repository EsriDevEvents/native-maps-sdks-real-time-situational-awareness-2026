using CommandDashboard.Configuration;
using CommandDashboard.Models;

namespace CommandDashboard.Services;

public sealed class MockDashboardDataService : IDashboardDataService
{
    private readonly Random random = new();
    private const double BaseLatitude = 34.0556;
    private const double BaseLongitude = -117.1825;

    public IReadOnlyList<FieldUnitStatus> CreateInitialVipUnits()
    {
        var vip = new List<FieldUnitStatus>
        {
            new("VIP-01", UnitRole.Vip),
            new("VIP-02", UnitRole.Vip),
            new("VIP-03", UnitRole.Vip)
        };

        var seededOffsets = new[]
        {
            (northMeters: 12.0, eastMeters: 5.0),
            (northMeters: -10.0, eastMeters: 8.0),
            (northMeters: 6.0, eastMeters: -14.0)
        };

        for (var i = 0; i < vip.Count; i++)
        {
            var (lat, lon) = OffsetMetersToCoordinates(BaseLatitude, BaseLongitude, seededOffsets[i].northMeters, seededOffsets[i].eastMeters);
            vip[i].Latitude = lat;
            vip[i].Longitude = lon;
            ApplyDerivedStatus(vip[i], BaseLatitude, BaseLongitude);
        }

        return vip;
    }

    public FieldUnitStatus CreateEscortUnit()
    {
        return new FieldUnitStatus("Escort", UnitRole.Escort)
        {
            Status = UnitStatus.In,
            DistanceMeters = 0,
            DirectionToEscort = "-",
            Latitude = BaseLatitude,
            Longitude = BaseLongitude
        };
    }

    public void ApplyNextUpdate(FieldUnitStatus escortUnit, IList<FieldUnitStatus> vipUnits, bool isSimulationMode)
    {
        var escortJitterMeters = isSimulationMode ? 0.8 : 1.5;
        MoveUnitInMeters(escortUnit, escortJitterMeters);

        foreach (var unit in vipUnits)
        {
            var vipJitterMeters = isSimulationMode ? 2.2 : 4.2;
            MoveUnitInMeters(unit, vipJitterMeters);
            ApplyDerivedStatus(unit, escortUnit.Latitude, escortUnit.Longitude);
        }

        escortUnit.Status = vipUnits.Any(item => item.Status == UnitStatus.Out)
            ? UnitStatus.Out
            : UnitStatus.In;
        escortUnit.DistanceMeters = 0;
        escortUnit.DirectionToEscort = "-";
    }

    private void MoveUnitInMeters(FieldUnitStatus unit, double maxMetersDelta)
    {
        var northDelta = random.NextDouble() * (maxMetersDelta * 2) - maxMetersDelta;
        var eastDelta = random.NextDouble() * (maxMetersDelta * 2) - maxMetersDelta;

        var (newLat, newLon) = OffsetMetersToCoordinates(unit.Latitude, unit.Longitude, northDelta, eastDelta);
        unit.Latitude = newLat;
        unit.Longitude = newLon;
    }

    private static (double latitude, double longitude) OffsetMetersToCoordinates(double latitude, double longitude, double northMeters, double eastMeters)
    {
        const double metersPerDegreeLatitude = 111_111.0;
        var metersPerDegreeLongitude = metersPerDegreeLatitude * Math.Cos(latitude * Math.PI / 180.0);

        var lat = latitude + northMeters / metersPerDegreeLatitude;
        var lon = longitude + eastMeters / metersPerDegreeLongitude;
        return (lat, lon);
    }

    private static double CalculateDistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = DegreesToRadians(lat1);
        var phi2 = DegreesToRadians(lat2);
        var deltaPhi = DegreesToRadians(lat2 - lat1);
        var deltaLambda = DegreesToRadians(lon2 - lon1);

        var a = Math.Sin(deltaPhi / 2) * Math.Sin(deltaPhi / 2) +
                Math.Cos(phi1) * Math.Cos(phi2) *
                Math.Sin(deltaLambda / 2) * Math.Sin(deltaLambda / 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        const double earthRadiusMeters = 6_371_000.0;
        return earthRadiusMeters * c;
    }

    private static string CalculateDirectionCardinal(double fromLat, double fromLon, double toLat, double toLon)
    {
        var phi1 = DegreesToRadians(fromLat);
        var phi2 = DegreesToRadians(toLat);
        var deltaLambda = DegreesToRadians(toLon - fromLon);

        var y = Math.Sin(deltaLambda) * Math.Cos(phi2);
        var x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(deltaLambda);
        var bearing = (RadiansToDegrees(Math.Atan2(y, x)) + 360) % 360;

        var cardinals = new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        var index = (int)Math.Round(bearing / 45.0, MidpointRounding.AwayFromZero) % 8;
        return cardinals[index];
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static double RadiansToDegrees(double radians) => radians * 180.0 / Math.PI;

    private static void ApplyDerivedStatus(FieldUnitStatus vipUnit, double escortLatitude, double escortLongitude)
    {
        var distance = CalculateDistanceMeters(vipUnit.Latitude, vipUnit.Longitude, escortLatitude, escortLongitude);
        vipUnit.DistanceMeters = distance;
        vipUnit.DirectionToEscort = CalculateDirectionCardinal(vipUnit.Latitude, vipUnit.Longitude, escortLatitude, escortLongitude);
        vipUnit.Status = distance > DemoConstants.EscortSafetyRadiusMeters ? UnitStatus.Out : UnitStatus.In;
    }
}