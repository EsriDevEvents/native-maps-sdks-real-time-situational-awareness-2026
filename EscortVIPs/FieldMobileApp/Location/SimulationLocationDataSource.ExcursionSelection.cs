namespace FieldMobileApp.Location;

public sealed partial class SimulationLocationDataSource
{
    private SelectedExcursionStart? SelectEncounteredExcursionStart(
        RouteTrack primaryTrack,
        IEnumerable<RouteTrack> tracks,
        double previousPrimaryDistanceMeters,
        double currentPrimaryDistanceMeters,
        double speedMetersPerSecond,
        double deltaSeconds,
        DateTimeOffset nowUtc)
    {
        if (nowUtc < excursionSelectionBlockedUntilUtc)
        {
            return null;
        }

        if (excursionRearmStartPrimaryDistanceMeters.HasValue)
        {
            var traveledSinceRejoin = NormalizeDistance(
                currentPrimaryDistanceMeters - excursionRearmStartPrimaryDistanceMeters.Value,
                primaryTrack.LengthMeters);
            if (traveledSinceRejoin < ExcursionRearmMinPrimaryDistanceMeters)
            {
                return null;
            }

            excursionRearmStartPrimaryDistanceMeters = null;
        }

        SelectedExcursionStart? selected = null;
        var bestTravelDelta = double.MaxValue;
        var lookaheadMeters = Math.Max(ExcursionLookaheadMeters, speedMetersPerSecond * deltaSeconds * 2.5);

        foreach (var track in tracks)
        {
            var junctions = FindRouteJunctions(primaryTrack, track);
            if (junctions.Count == 0)
            {
                continue;
            }

            foreach (var junction in junctions)
            {
                var hasPassed = HasPassedDistance(
                    previousPrimaryDistanceMeters,
                    currentPrimaryDistanceMeters,
                    junction.PrimaryDistanceMeters,
                    direction: 1,
                    primaryTrack.LengthMeters);
                var forwardToJunction = NormalizeDistance(
                    junction.PrimaryDistanceMeters - currentPrimaryDistanceMeters,
                    primaryTrack.LengthMeters);
                var isWithinLookahead = forwardToJunction <= lookaheadMeters;

                if (!hasPassed && !isWithinLookahead)
                {
                    continue;
                }

                var encounterSeed = BuildExcursionEncounterSeed(junction);
                if (!ShouldDeviceTakeEncounterExcursion(encounterSeed))
                {
                    continue;
                }

                var travelDelta = NormalizeDistance(
                    junction.PrimaryDistanceMeters - previousPrimaryDistanceMeters,
                    primaryTrack.LengthMeters);
                if (travelDelta < bestTravelDelta)
                {
                    bestTravelDelta = travelDelta;
                    selected = new SelectedExcursionStart(track, junction, junctions);
                }
            }
        }

        return selected;
    }

    private int BuildExcursionEncounterSeed(RouteJunction junction)
    {
        var primaryBucket = (int)Math.Round(junction.PrimaryDistanceMeters);
        var excursionBucket = (int)Math.Round(junction.ExcursionDistanceMeters);
        return HashCode.Combine(primaryBucket, excursionBucket);
    }

    private bool ShouldDeviceTakeEncounterExcursion(int encounterSeed)
    {
        var winnerSlot = Math.Abs(HashCode.Combine(SharedRunSeed, encounterSeed)) % DefaultVipVipCount + 1;
        if (TryGetTrailingNumber(deviceId, out var vipSlot) && vipSlot > 0)
        {
            var normalizedSlot = ((vipSlot - 1) % DefaultVipVipCount) + 1;
            return normalizedSlot == winnerSlot;
        }

        var fallbackSlot = Math.Abs(HashCode.Combine(deviceId.ToLowerInvariant(), SharedRunSeed, encounterSeed)) % DefaultVipVipCount + 1;
        return fallbackSlot == winnerSlot;
    }

    private static bool TryGetTrailingNumber(string value, out int trailingNumber)
    {
        trailingNumber = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var end = value.Length - 1;
        while (end >= 0 && !char.IsDigit(value[end]))
        {
            end--;
        }

        if (end < 0)
        {
            return false;
        }

        var start = end;
        while (start >= 0 && char.IsDigit(value[start]))
        {
            start--;
        }

        if (start == end)
        {
            return false;
        }

        var numericPart = value[(start + 1)..(end + 1)];
        return int.TryParse(numericPart, out trailingNumber);
    }
}
