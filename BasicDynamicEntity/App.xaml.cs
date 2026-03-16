using System.Windows;
using Esri.ArcGISRuntime;
using Esri.ArcGISRuntime.Security;

namespace Simple;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ArcGISRuntimeEnvironment.SetLicense("runtimelite,1000,rud2019458306,none,GB2PMD17JYEPDPF44224");
        ArcGISRuntimeEnvironment.ApiKey = "AAPK4bcb555404a043808dca2eba79baf74c34K2m_O_32rrPaHDQbepBE1YIUiicBaGJmXL8OG-dC6X1yeoF5y0SpQQjVLlalMv";
        ArcGISRuntimeEnvironment.Initialize();
        AuthenticationManager.Current.ChallengeHandler = new ChallengeHandler(async (info) =>
        {
            return await AccessTokenCredential.CreateAsync(info.ServiceUri!, "rt_velocity1", "rt_velocity01");
        });
    }
}
