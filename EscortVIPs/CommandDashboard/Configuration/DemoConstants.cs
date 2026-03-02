namespace CommandDashboard.Configuration;

public static class DemoConstants
{
    public const double EscortSafetyRadiusMeters = 25;
    public const double EscortDangerRadiusMeters = EscortSafetyRadiusMeters * 0.75;
    public const int AutoUpdateIntervalMilliseconds = 1000;
    public const int TelemetryStaleSeconds = 5;
}