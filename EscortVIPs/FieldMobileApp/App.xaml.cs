using Microsoft.Extensions.DependencyInjection;
using Esri.ArcGISRuntime;

namespace FieldMobileApp;

public partial class App : Application
{
    public App()
    {
        var apiKey = Environment.GetEnvironmentVariable("ARCGIS_API_KEY", EnvironmentVariableTarget.Process)
                     ?? Environment.GetEnvironmentVariable("ARCGIS_API_KEY", EnvironmentVariableTarget.User)
                     ?? Environment.GetEnvironmentVariable("ARCGIS_API_KEY", EnvironmentVariableTarget.Machine);

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            ArcGISRuntimeEnvironment.ApiKey = apiKey;
        }

        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new AppShell());

#if WINDOWS
		window.Width = 320;
		window.Height = 520;
		window.MinimumWidth = 300;
		window.MinimumHeight = 320;
#endif

        return window;
    }
}