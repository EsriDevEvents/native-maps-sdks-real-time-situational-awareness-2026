using System.Linq;
using System.Threading.Tasks;
using ClickSource;
using Esri.ArcGISRuntime.Geotriggers;
using Esri.ArcGISRuntime.Mapping;

namespace BasicGeotrigger
{
    public sealed partial class MainWindow
    {
        private Geotrigger? CreateAndInitializeGeotriggerFromWebMap(Map map)
        {
            var geotrigger = map.GeotriggersInfo.Geotriggers.FirstOrDefault();
            if (geotrigger is not null)
            {
                var clickSource = ClickLocationDataSource.Create(_mapView);
                ((LocationGeotriggerFeed)geotrigger.Feed).LocationDataSource = clickSource;
            }

            return geotrigger;
        }

        private async Task<Geotrigger?> CreateAndInitializeGeotriggerFromWebMapAsync(Map map)
        {
            await map.GeotriggersInfo.LoadAsync();
            return CreateAndInitializeGeotriggerFromWebMap(map);
        }
    }
}
