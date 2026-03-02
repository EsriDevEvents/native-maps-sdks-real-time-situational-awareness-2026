using Esri.ArcGISRuntime.Geometry;

namespace FieldMobileApp.Location;

public sealed partial class SimulationLocationDataSource
{
    private static double NormalizeDistance(double value, double lengthMeters)
    {
        if (lengthMeters <= 0)
        {
            return 0;
        }

        var remainder = value % lengthMeters;
        return remainder < 0 ? remainder + lengthMeters : remainder;
    }

    private static double ShortestCircularDistance(double fromDistance, double toDistance, double lengthMeters)
    {
        var forward = NormalizeDistance(toDistance - fromDistance, lengthMeters);
        var backward = NormalizeDistance(fromDistance - toDistance, lengthMeters);
        return Math.Min(forward, backward);
    }

    private static double SignedCircularDelta(double fromDistance, double toDistance, double lengthMeters)
    {
        var forward = NormalizeDistance(toDistance - fromDistance, lengthMeters);
        var backward = NormalizeDistance(fromDistance - toDistance, lengthMeters);
        return forward <= backward ? forward : -backward;
    }

    private static int ChooseExcursionDirection(RouteJunction startJunction, IReadOnlyList<RouteJunction> junctions, double trackLengthMeters)
    {
        var alternatives = junctions
            .Where(junction => Math.Abs(junction.ExcursionDistanceMeters - startJunction.ExcursionDistanceMeters) > 0.5)
            .ToList();

        if (alternatives.Count == 0)
        {
            return 1;
        }

        var farthest = alternatives
            .OrderByDescending(junction => ShortestCircularDistance(startJunction.ExcursionDistanceMeters, junction.ExcursionDistanceMeters, trackLengthMeters))
            .First();

        var clockwise = NormalizeDistance(farthest.ExcursionDistanceMeters - startJunction.ExcursionDistanceMeters, trackLengthMeters);
        var counterClockwise = NormalizeDistance(startJunction.ExcursionDistanceMeters - farthest.ExcursionDistanceMeters, trackLengthMeters);

        return clockwise >= counterClockwise ? 1 : -1;
    }

    private static RouteJunction? TryFindRejoinJunction(
        double previousDistance,
        double currentDistance,
        int direction,
        double trackLengthMeters,
        IReadOnlyList<RouteJunction> junctions,
        RouteJunction? startJunction,
        double distanceSinceStartMeters,
        bool allowStartJunction)
    {
        if (junctions.Count == 0 || startJunction is null || distanceSinceStartMeters < 12)
        {
            return null;
        }

        foreach (var junction in junctions)
        {
            var isStartJunction = Math.Abs(junction.ExcursionDistanceMeters - startJunction.ExcursionDistanceMeters) <= 0.5;
            if (isStartJunction && !allowStartJunction)
            {
                continue;
            }

            if (HasPassedDistance(previousDistance, currentDistance, junction.ExcursionDistanceMeters, direction, trackLengthMeters))
            {
                return junction;
            }
        }

        return null;
    }

    private static bool HasPassedDistance(
        double previousDistance,
        double currentDistance,
        double targetDistance,
        int direction,
        double trackLengthMeters)
    {
        if (direction >= 0)
        {
            var traveled = NormalizeDistance(currentDistance - previousDistance, trackLengthMeters);
            var toTarget = NormalizeDistance(targetDistance - previousDistance, trackLengthMeters);
            return toTarget <= traveled + 0.01;
        }

        var reverseTraveled = NormalizeDistance(previousDistance - currentDistance, trackLengthMeters);
        var reverseToTarget = NormalizeDistance(previousDistance - targetDistance, trackLengthMeters);
        return reverseToTarget <= reverseTraveled + 0.01;
    }

    private static double StepTowardDistance(
        double currentDistance,
        double targetDistance,
        double trackLengthMeters,
        double maxStepMeters,
        int preferredDirection)
    {
        if (trackLengthMeters <= 0 || maxStepMeters <= 0)
        {
            return NormalizeDistance(targetDistance, trackLengthMeters);
        }

        var normalizedCurrent = NormalizeDistance(currentDistance, trackLengthMeters);
        var normalizedTarget = NormalizeDistance(targetDistance, trackLengthMeters);
        var forward = NormalizeDistance(normalizedTarget - normalizedCurrent, trackLengthMeters);
        var reverse = NormalizeDistance(normalizedCurrent - normalizedTarget, trackLengthMeters);

        if (preferredDirection >= 0)
        {
            var move = Math.Min(forward, maxStepMeters);
            return NormalizeDistance(normalizedCurrent + move, trackLengthMeters);
        }

        var reverseMove = Math.Min(reverse, maxStepMeters);
        return NormalizeDistance(normalizedCurrent - reverseMove, trackLengthMeters);
    }

    private static List<RouteJunction> FindRouteJunctions(RouteTrack primaryTrack, RouteTrack excursionTrack)
    {
        var junctions = new List<RouteJunction>();

        var primaryCumulative = BuildCumulativeSegmentStarts(primaryTrack.SegmentLengthsMeters);
        var excursionCumulative = BuildCumulativeSegmentStarts(excursionTrack.SegmentLengthsMeters);

        for (var primaryIndex = 0; primaryIndex < primaryTrack.SegmentLengthsMeters.Count; primaryIndex++)
        {
            var p1 = primaryTrack.Vertices[primaryIndex];
            var p2 = primaryTrack.Vertices[primaryIndex + 1];

            for (var excursionIndex = 0; excursionIndex < excursionTrack.SegmentLengthsMeters.Count; excursionIndex++)
            {
                var q1 = excursionTrack.Vertices[excursionIndex];
                var q2 = excursionTrack.Vertices[excursionIndex + 1];

                if (!TryIntersectSegments(p1, p2, q1, q2, out var intersectionPoint, out var primaryFraction, out var excursionFraction))
                {
                    continue;
                }

                var primaryDistance = primaryCumulative[primaryIndex] + primaryTrack.SegmentLengthsMeters[primaryIndex] * primaryFraction;
                var excursionDistance = excursionCumulative[excursionIndex] + excursionTrack.SegmentLengthsMeters[excursionIndex] * excursionFraction;

                var duplicate = junctions.Any(existing =>
                    Math.Abs(existing.PrimaryDistanceMeters - primaryDistance) < 1.0
                    && Math.Abs(existing.ExcursionDistanceMeters - excursionDistance) < 1.0);

                if (!duplicate)
                {
                    junctions.Add(new RouteJunction(primaryDistance, excursionDistance, intersectionPoint));
                }
            }
        }

        return junctions;
    }

    private static List<double> BuildCumulativeSegmentStarts(IReadOnlyList<double> segmentLengths)
    {
        var starts = new List<double>(segmentLengths.Count);
        var cumulative = 0.0;
        for (var index = 0; index < segmentLengths.Count; index++)
        {
            starts.Add(cumulative);
            cumulative += segmentLengths[index];
        }

        return starts;
    }

    private static bool TryIntersectSegments(
        MapPoint p1,
        MapPoint p2,
        MapPoint q1,
        MapPoint q2,
        out MapPoint intersection,
        out double primaryFraction,
        out double excursionFraction)
    {
        var rX = p2.X - p1.X;
        var rY = p2.Y - p1.Y;
        var sX = q2.X - q1.X;
        var sY = q2.Y - q1.Y;

        var denominator = rX * sY - rY * sX;
        if (Math.Abs(denominator) < 1e-12)
        {
            intersection = new MapPoint(p1.X, p1.Y, SpatialReferences.Wgs84);
            primaryFraction = 0;
            excursionFraction = 0;
            return false;
        }

        var qpX = q1.X - p1.X;
        var qpY = q1.Y - p1.Y;

        var t = (qpX * sY - qpY * sX) / denominator;
        var u = (qpX * rY - qpY * rX) / denominator;

        if (t < -1e-9 || t > 1 + 1e-9 || u < -1e-9 || u > 1 + 1e-9)
        {
            intersection = new MapPoint(p1.X, p1.Y, SpatialReferences.Wgs84);
            primaryFraction = 0;
            excursionFraction = 0;
            return false;
        }

        var clampedT = Math.Clamp(t, 0, 1);
        var clampedU = Math.Clamp(u, 0, 1);
        var x = p1.X + clampedT * rX;
        var y = p1.Y + clampedT * rY;

        intersection = new MapPoint(x, y, SpatialReferences.Wgs84);
        primaryFraction = clampedT;
        excursionFraction = clampedU;
        return true;
    }

    private static MapPoint ClampStepDistance(MapPoint fromPoint, MapPoint toPoint, double maxStepMeters)
    {
        var distanceMeters = CalculateDistanceMeters(fromPoint, toPoint);
        if (distanceMeters <= maxStepMeters)
        {
            return toPoint;
        }

        var bearingDegrees = ComputeBearingDegrees(fromPoint.Y, fromPoint.X, toPoint.Y, toPoint.X);
        return MovePointGeodetic(fromPoint, maxStepMeters, bearingDegrees);
    }

    private static double FindNearestDistanceOnTrack(RouteTrack track, MapPoint referencePoint)
    {
        var snappedPoint = referencePoint;
        try
        {
            var nearest = GeometryEngine.NearestCoordinate(track.Polyline, referencePoint);
            if (nearest is not null)
            {
                snappedPoint = nearest.Coordinate;
            }
        }
        catch
        {
        }

        if (track.Vertices.Count < 2 || track.SegmentLengthsMeters.Count == 0)
        {
            return 0;
        }

        var cumulativeDistance = 0.0;
        var bestDistanceAlongTrack = 0.0;
        var bestPointDistance = double.MaxValue;

        for (var index = 0; index < track.SegmentLengthsMeters.Count; index++)
        {
            var start = track.Vertices[index];
            var end = track.Vertices[index + 1];
            var segmentLengthMeters = Math.Max(track.SegmentLengthsMeters[index], 0.0001);

            var projectedFraction = ProjectFractionOnSegment(snappedPoint, start, end);
            var projectedLatitude = start.Y + (end.Y - start.Y) * projectedFraction;
            var projectedLongitude = start.X + (end.X - start.X) * projectedFraction;
            var projectedPoint = new MapPoint(projectedLongitude, projectedLatitude, SpatialReferences.Wgs84);

            var candidateDistance = CalculateDistanceMeters(snappedPoint, projectedPoint);
            if (candidateDistance < bestPointDistance)
            {
                bestPointDistance = candidateDistance;
                bestDistanceAlongTrack = cumulativeDistance + segmentLengthMeters * projectedFraction;
            }

            cumulativeDistance += segmentLengthMeters;
        }

        return Math.Clamp(bestDistanceAlongTrack, 0, track.LengthMeters);
    }

    private static double ProjectFractionOnSegment(MapPoint reference, MapPoint start, MapPoint end)
    {
        var meanLatitudeRadians = DegreesToRadians((start.Y + end.Y + reference.Y) / 3.0);
        const double metersPerDegreeLatitude = 111_111.0;
        var metersPerDegreeLongitude = metersPerDegreeLatitude * Math.Cos(meanLatitudeRadians);

        var startX = start.X * metersPerDegreeLongitude;
        var startY = start.Y * metersPerDegreeLatitude;
        var endX = end.X * metersPerDegreeLongitude;
        var endY = end.Y * metersPerDegreeLatitude;
        var referenceX = reference.X * metersPerDegreeLongitude;
        var referenceY = reference.Y * metersPerDegreeLatitude;

        var segmentX = endX - startX;
        var segmentY = endY - startY;
        var segmentLengthSquared = segmentX * segmentX + segmentY * segmentY;
        if (segmentLengthSquared <= 1e-9)
        {
            return 0;
        }

        var relativeX = referenceX - startX;
        var relativeY = referenceY - startY;
        var projection = (relativeX * segmentX + relativeY * segmentY) / segmentLengthSquared;
        return Math.Clamp(projection, 0, 1);
    }
}
