namespace FieldMobileApp;

public partial class MainPage
{
    private readonly object diagnosticsGate = new();
    private readonly string diagnosticsLogPath;

    private static LaunchOptions ParseLaunchOptions(string[] args)
    {
        var options = new LaunchOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                continue;

            if (string.Equals(arg, "--autoconnect", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--exit-on-disconnect", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i >= args.Length - 1)
                continue;

            var value = args[i + 1];
            switch (arg.ToLowerInvariant())
            {
                case "--role":
                    options.Role = value;
                    i++;
                    break;
                case "--mode":
                    options.Mode = value;
                    i++;
                    break;
                case "--device":
                    options.DeviceId = value;
                    i++;
                    break;
                case "--session":
                    options.SessionId = value;
                    i++;
                    break;
                case "--host":
                    options.HostUrl = value;
                    i++;
                    break;
                case "--sim-host":
                    options.SimulationUrl = value;
                    i++;
                    break;
            }
        }

        return options;
    }

    protected override async void OnDisappearing()
    {
        isShuttingDown = true;
        LogDiagnostic("OnDisappearing invoked. Shutting down.");
        await DisconnectAsync();
        base.OnDisappearing();
    }

    private void LogDiagnostic(string message)
    {
        try
        {
            var line = $"{DateTimeOffset.UtcNow:O} [{configuredDeviceId}] {message}";
            System.Diagnostics.Debug.WriteLine(line);

            lock (diagnosticsGate)
            {
                var directory = Path.GetDirectoryName(diagnosticsLogPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(diagnosticsLogPath, line + Environment.NewLine);
            }
        }
        catch
        {
        }
    }

    private sealed class LaunchOptions
    {
        public string? Role { get; set; }

        public string? Mode { get; set; } = "live";

        public string? DeviceId { get; set; }

        public string? SessionId { get; set; }

        public string? HostUrl { get; set; }

        public string? SimulationUrl { get; set; }
    }
}
