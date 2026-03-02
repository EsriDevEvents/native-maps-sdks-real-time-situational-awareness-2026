namespace CommandMessaging;

public sealed record RegisterPayload(string DisplayName, string Role, string? ClientType = null);

public sealed record LocationUpdatePayload(double Latitude, double Longitude, double? DistanceMeters, string? DirectionToEscort);

public sealed record StatusUpdatePayload(string Status);

public sealed record AssignedLocationPayload(
	string DeviceId,
	string Role,
	double Latitude,
	double Longitude,
	string Status,
	double? DistanceMeters,
	string? DirectionToEscort);

public sealed record EscortPositionPayload(double Latitude, double Longitude);

public sealed record VipTelemetryPayload(
	string DeviceId,
	double Latitude,
	double Longitude,
	string Status,
	double? DistanceMeters,
	string? DirectionToEscort);

public sealed record VipControlPayload(
	string DeviceId,
	string Signal,
	string Message,
	bool IsActive = true);

public sealed record RoutePointPayload(double Latitude, double Longitude);

public sealed record RouteSnapshotPayload(string RouteId, IReadOnlyList<RoutePointPayload> Points);

public sealed record HeartbeatPayload(int? BatteryPercent);
