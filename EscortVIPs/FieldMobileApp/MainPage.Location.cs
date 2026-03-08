using Esri.ArcGISRuntime.Location;
using FieldMobileApp.Location;

namespace FieldMobileApp;

public partial class MainPage
{
    private readonly Lock latestLocationGate = new();
    private LocationDataSource? locationDataSource;
    private Esri.ArcGISRuntime.Location.Location? latestLocation;

    private async Task StartLocationDataSourceAsync()
    {
        if (locationDataSource is not null)
        {
            locationDataSource.LocationChanged -= OnLocationChanged;

            if (locationDataSource.Status == LocationDataSourceStatus.Started)
            {
                await locationDataSource.StopAsync();
            }
        }

        if (simulatedMode)
        {
            locationDataSource = new SimulationLocationDataSource(configuredRole, configuredDeviceId);
        }
        else
        {
            locationDataSource = new SystemLocationDataSource();
        }

        locationDataSource.LocationChanged += OnLocationChanged;
        await locationDataSource.StartAsync();
    }

    private void OnLocationChanged(object? sender, Esri.ArcGISRuntime.Location.Location runtimeLocation)
    {
        lock (latestLocationGate)
        {
            latestLocation = runtimeLocation;
        }
    }

    private Esri.ArcGISRuntime.Location.Location? GetLatestLocation()
    {
        lock (latestLocationGate)
        {
            return latestLocation;
        }
    }
}
