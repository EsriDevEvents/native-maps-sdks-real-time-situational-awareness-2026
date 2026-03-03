using System.Linq;
using ClickSource;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geotriggers;
using Esri.ArcGISRuntime.Mapping;

namespace BasicGeotrigger
{
    public sealed partial class MainWindow
    {
        private const string _fenceLayerId = "3f8be9a6ecda4add81b1b20c2edbe712";

        private Geotrigger? CreateAndInitializeGeotriggerProgrammatically(Map map)
        {
            var featureLayer = map.OperationalLayers
                .OfType<FeatureLayer>()
                .FirstOrDefault(layer => layer.Id == _fenceLayerId)
                ?? map.OperationalLayers.OfType<FeatureLayer>().FirstOrDefault();

            if (featureLayer?.FeatureTable is not FeatureTable featureTable)
                return null;

            var clickSource = ClickLocationDataSource.Create(_mapView);
            var feed = new LocationGeotriggerFeed(clickSource);
            var fenceParameters = new FeatureFenceParameters(featureTable, 30d);

            var messageExpression = new Esri.ArcGISRuntime.ArcadeExpression(
                "{\n" +
                "  'message': `${$fencefeature['TEXT_FOR_DESCRIPTION']}`,\n" +
                "  'actions': IIF($fencenotificationtype == 'entered', [ 'showPopup', 'selectFence' ], [ 'showPopup', 'selectFence' ])\n" +
                "}");

            var geotrigger = new FenceGeotrigger(
                feed,
                FenceRuleType.EnterOrExit,
                fenceParameters,
                messageExpression,
                "Palm Springs locales")
            {
                FeedAccuracyMode = FenceGeotriggerFeedAccuracyMode.UseGeometry,
                EnterExitSpatialRelationship = FenceEnterExitSpatialRelationship.EnterContainsAndExitDoesNotIntersect,
            };

            geotrigger.RequestedActions.Add("showPopup");
            geotrigger.RequestedActions.Add("selectFence");
            geotrigger.RequestedActions.Add("showMessage");

            return geotrigger;
        }
    }
}
