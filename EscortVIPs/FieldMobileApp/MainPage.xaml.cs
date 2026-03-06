namespace FieldMobileApp;

public partial class MainPage : ContentPage
{
    private readonly bool simulatedMode;
    private readonly string configuredRole;
    private readonly string configuredDeviceId;
    private readonly string configuredSessionId;
    private readonly string configuredHostUrl;
    private readonly string configuredSimulationUrl;
    private bool isShuttingDown;

    public MainPage()
    {
        InitializeComponent();

        var launchOptions = ParseLaunchOptions(Environment.GetCommandLineArgs());
        simulatedMode = string.Equals(launchOptions.Mode, "simulated", StringComparison.OrdinalIgnoreCase);
        configuredRole = string.Equals(launchOptions.Role, "Escort", StringComparison.OrdinalIgnoreCase)
            ? "Escort"
            : "VIP";
        configuredDeviceId = !string.IsNullOrWhiteSpace(launchOptions.DeviceId)
            ? launchOptions.DeviceId
            : configuredRole == "Escort" ? "Escort" : "VIP-01";
        configuredSessionId = !string.IsNullOrWhiteSpace(launchOptions.SessionId)
            ? launchOptions.SessionId
            : "DEVSUMMIT-2026";
        configuredHostUrl = !string.IsNullOrWhiteSpace(launchOptions.HostUrl)
            ? launchOptions.HostUrl
            : "ws://127.0.0.1:8765/ws/";
        configuredSimulationUrl = !string.IsNullOrWhiteSpace(launchOptions.SimulationUrl)
            ? launchOptions.SimulationUrl
            : "ws://127.0.0.1:8775/ws/";

        var appTitle = string.Equals(configuredRole, "Escort", StringComparison.OrdinalIgnoreCase)
            ? "Escort"
            : $"VIP: {configuredDeviceId}";

        Title = appTitle;
        if (Shell.Current is not null)
        {
            Shell.Current.Title = appTitle;
        }

        if (Application.Current?.Windows.Count > 0)
        {
            Application.Current.Windows[0].Title = appTitle;
        }

        var invalidNameCharacters = Path.GetInvalidFileNameChars();
        var safeDeviceId = new string(configuredDeviceId.Select(character => invalidNameCharacters.Contains(character) ? '_' : character).ToArray());
        diagnosticsLogPath = Path.Combine(FileSystem.Current.AppDataDirectory, $"field-app-{safeDeviceId}.log");

        ApplyStatusVisual(null, simulatedMode ? "Simulated mode" : "Live mode");
        UpdateVipCommandBanner(null, null, false);
        RoleStateLabel.FontSize = string.Equals(configuredRole, "VIP", StringComparison.OrdinalIgnoreCase) ? 24 : 40;
        UpdateEscortMetricsDetail();
        VipBottomPanel.IsVisible = string.Equals(configuredRole, "VIP", StringComparison.OrdinalIgnoreCase);
        EscortBottomPanel.IsVisible = string.Equals(configuredRole, "Escort", StringComparison.OrdinalIgnoreCase);
        EscortOverallStatusBanner.IsVisible = string.Equals(configuredRole, "Escort", StringComparison.OrdinalIgnoreCase);
        VipStatusListView.ItemsSource = escortVipStatuses;
        UpdateEscortOverallStatusBanner();
        LogDiagnostic($"Startup role={configuredRole} device={configuredDeviceId} mode={(simulatedMode ? "simulated" : "live")} host={configuredHostUrl} simHost={configuredSimulationUrl}");

        Loaded += OnPageLoaded;
    }
}
