using System.Windows;
using Esri.ArcGISRuntime;

namespace CommandDashboard;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var apiKey = Environment.GetEnvironmentVariable("ARCGIS_API_KEY", EnvironmentVariableTarget.Process)
            ?? Environment.GetEnvironmentVariable("ARCGIS_API_KEY", EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable("ARCGIS_API_KEY", EnvironmentVariableTarget.Machine);

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            ArcGISRuntimeEnvironment.ApiKey = apiKey;
        }

        base.OnStartup(e);
    }
}

