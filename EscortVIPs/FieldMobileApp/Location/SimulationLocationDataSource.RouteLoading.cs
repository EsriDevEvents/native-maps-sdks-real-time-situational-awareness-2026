using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;

namespace FieldMobileApp.Location;

public sealed partial class SimulationLocationDataSource
{
    private static async Task<List<RouteTrack>> LoadRouteTracksAsync(string routePath, string? preferredRouteLayerName)
    {
        var routeFileExtension = Path.GetExtension(routePath);

        if (routeFileExtension.Equals(".shp", StringComparison.OrdinalIgnoreCase))
        {
            var shapefileTable = new ShapefileFeatureTable(routePath);
            await shapefileTable.LoadAsync();
            return await ReadPolylineTracksFromFeatureTableAsync(shapefileTable);
        }

        if (routeFileExtension.Equals(".geodatabase", StringComparison.OrdinalIgnoreCase) || routeFileExtension.Equals(".geodb", StringComparison.OrdinalIgnoreCase))
        {
            var geodatabase = await Geodatabase.OpenAsync(routePath);
            try
            {
                var geodatabaseTables = geodatabase.GeodatabaseFeatureTables.ToList();

                if (geodatabaseTables.Count == 0)
                {
                    return [];
                }

                var orderedTables = OrderRouteTables(geodatabaseTables, preferredRouteLayerName);
                foreach (var featureTable in orderedTables)
                {
                    try
                    {
                        await featureTable.LoadAsync();

                        if (featureTable.GeometryType != GeometryType.Polyline)
                        {
                            continue;
                        }

                        var tracks = await ReadPolylineTracksFromFeatureTableAsync(featureTable);
                        if (tracks.Count > 0)
                        {
                            return tracks;
                        }
                    }
                    catch
                    {
                    }
                }

                return [];
            }
            finally
            {
                geodatabase.Close();
            }
        }

        return [];
    }

    private static IReadOnlyList<GeodatabaseFeatureTable> OrderRouteTables(
        IReadOnlyList<GeodatabaseFeatureTable> polylineTables,
        string? preferredRouteLayerName)
    {
        if (string.IsNullOrWhiteSpace(preferredRouteLayerName))
        {
            return polylineTables;
        }

        var preferredNormalized = NormalizeName(preferredRouteLayerName);

        var exactMatches = polylineTables
            .Where(table => string.Equals(table.TableName, preferredRouteLayerName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var containsMatches = polylineTables
            .Where(table => !exactMatches.Contains(table)
                && NormalizeName(table.TableName).Contains(preferredNormalized, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var remaining = polylineTables
            .Where(table => !exactMatches.Contains(table) && !containsMatches.Contains(table))
            .ToList();

        return exactMatches
            .Concat(containsMatches)
            .Concat(remaining)
            .ToList();
    }

    private static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value
            .Trim()
            .ToLowerInvariant()
            .Where(character => char.IsLetterOrDigit(character))
            .ToArray());
    }

    private static async Task<List<RouteTrack>> ReadPolylineTracksFromFeatureTableAsync(FeatureTable featureTable)
    {
        var tracks = new List<RouteTrack>();
        var queryParameters = new QueryParameters { WhereClause = "1=1" };
        var features = await featureTable.QueryFeaturesAsync(queryParameters);
        foreach (var feature in features)
        {
            if (feature.Geometry is not Polyline polyline)
            {
                continue;
            }

            var wgs84Geometry = polyline.SpatialReference == SpatialReferences.Wgs84
                ? polyline
                : GeometryEngine.Project(polyline, SpatialReferences.Wgs84) as Polyline;

            if (wgs84Geometry is null)
            {
                continue;
            }

            var (simulationRole, hasExplicitRole) = ReadSimulationRole(feature.Attributes);
            var isExcursion = simulationRole == RouteSimulationRole.Excursion || (simulationRole == RouteSimulationRole.Unknown && IsExcursionFeature(feature.Attributes));
            var name = ReadFeatureName(feature.Attributes);

            foreach (var part in wgs84Geometry.Parts)
            {
                var vertices = new List<MapPoint>();
                foreach (var point in part.Points)
                {
                    vertices.Add(point);
                }

                var track = BuildTrack(vertices, isExcursion, name, simulationRole, hasExplicitRole);
                if (track is not null)
                {
                    tracks.Add(track);
                }
            }
        }

        return tracks;
    }

    private static RouteTrack? BuildTrack(
        IReadOnlyList<MapPoint> vertices,
        bool isExcursion,
        string? name,
        RouteSimulationRole simulationRole,
        bool hasExplicitRole)
    {
        if (vertices.Count < 2)
        {
            return null;
        }

        var densifiedVertices = DensifyVertices(vertices, TrackDensifyMaxSegmentMeters);
        if (densifiedVertices.Count < 2)
        {
            return null;
        }

        var normalizedVertices = new List<MapPoint> { densifiedVertices[0] };
        var segmentLengths = new List<double>(densifiedVertices.Count - 1);
        var totalLength = 0.0;
        for (var i = 1; i < densifiedVertices.Count; i++)
        {
            var from = normalizedVertices[^1];
            var to = densifiedVertices[i];
            var segmentLength = CalculateDistanceMeters(from, to);
            if (segmentLength <= 0.05)
            {
                continue;
            }

            segmentLengths.Add(segmentLength);
            normalizedVertices.Add(to);
            totalLength += segmentLength;
        }

        if (segmentLengths.Count == 0 || totalLength <= 1 || normalizedVertices.Count != segmentLengths.Count + 1)
        {
            return null;
        }

        var polyline = new Polyline(normalizedVertices);
        return new RouteTrack(normalizedVertices, segmentLengths, totalLength, isExcursion, name, simulationRole, hasExplicitRole, polyline);
    }

    private static List<MapPoint> DensifyVertices(IReadOnlyList<MapPoint> vertices, double maxSegmentMeters)
    {
        var result = new List<MapPoint>(vertices.Count);
        if (vertices.Count == 0)
        {
            return result;
        }

        result.Add(vertices[0]);
        for (var index = 1; index < vertices.Count; index++)
        {
            var start = vertices[index - 1];
            var end = vertices[index];
            var segmentLengthMeters = CalculateDistanceMeters(start, end);

            if (segmentLengthMeters <= maxSegmentMeters)
            {
                result.Add(end);
                continue;
            }

            var pieceCount = Math.Max(1, (int)Math.Ceiling(segmentLengthMeters / maxSegmentMeters));
            for (var pieceIndex = 1; pieceIndex < pieceCount; pieceIndex++)
            {
                var fraction = pieceIndex / (double)pieceCount;
                var latitude = start.Y + (end.Y - start.Y) * fraction;
                var longitude = start.X + (end.X - start.X) * fraction;
                result.Add(new MapPoint(longitude, latitude, SpatialReferences.Wgs84));
            }

            result.Add(end);
        }

        return result;
    }

    private static (RouteSimulationRole role, bool isExplicit) ReadSimulationRole(IDictionary<string, object?> attributes)
    {
        foreach (var key in attributes.Keys)
        {
            var normalizedKey = key.Trim().ToLowerInvariant();
            if (normalizedKey is not ("simrole" or "sim_role" or "simrol" or "sim_route_role" or "simrouterole"))
            {
                continue;
            }

            var value = attributes[key]?.ToString()?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(value))
            {
                return (RouteSimulationRole.Unknown, true);
            }

            if (value is "main" or "primary" or "baseline")
            {
                return (RouteSimulationRole.Main, true);
            }

            if (value is "excursion" or "detour" or "VIP")
            {
                return (RouteSimulationRole.Excursion, true);
            }

            return (RouteSimulationRole.Unknown, true);
        }

        return (RouteSimulationRole.Unknown, false);
    }

    private static bool IsExcursionFeature(IDictionary<string, object?> attributes)
    {
        foreach (var key in attributes.Keys)
        {
            var value = attributes[key]?.ToString();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var normalizedValue = value.Trim().ToLowerInvariant();
            var normalizedKey = key.Trim().ToLowerInvariant();

            if (normalizedKey.Contains("excursion") || normalizedKey.Contains("detour"))
            {
                return normalizedValue is "1" or "true" or "yes" or "y" or "excursion" or "detour";
            }

            if (normalizedKey is "type" or "route_type" or "routetype" or "path_type" or "pathtype" or "category" or "role")
            {
                if (normalizedValue.Contains("excursion") || normalizedValue.Contains("detour") || normalizedValue.Contains("VIP"))
                {
                    return true;
                }
            }

            if (normalizedKey is "name" or "route_name" or "routename" or "label")
            {
                if (normalizedValue.Contains("excursion") || normalizedValue.Contains("detour"))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? ReadFeatureName(IDictionary<string, object?> attributes)
    {
        var candidateKeys = new[] { "name", "route_name", "routename", "label", "id" };
        foreach (var candidateKey in candidateKeys)
        {
            var match = attributes.FirstOrDefault(pair => string.Equals(pair.Key, candidateKey, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match.Key) && match.Value is not null)
            {
                var value = match.Value.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }
}
