using Driftcheck.Mcp.Android;
using Driftcheck.Mcp.Devices;
using Driftcheck.Mcp.Ios;

namespace Driftcheck.Tests;

public class AppInfoTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "driftcheck-tests", Guid.NewGuid().ToString("N"));

    public AppInfoTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ForegroundPackageIgnoresSystemBars()
    {
        const string xml = """
            <?xml version='1.0' encoding='UTF-8' standalone='yes' ?>
            <hierarchy rotation="0">
              <node package="com.android.systemui" bounds="[0,0][1080,63]" />
              <node package="com.companyname.paylomaui" bounds="[0,0][1080,2400]">
                <node package="com.companyname.paylomaui" text="Sign up" bounds="[90,1700][990,1850]" />
              </node>
              <node package="com.android.systemui" bounds="[0,2300][1080,2400]" />
            </hierarchy>
            """;

        Assert.Equal("com.companyname.paylomaui", AdbClient.ParseForegroundPackage(xml));
    }

    [Fact]
    public void AndroidDeployTimeIsTheLatestOfApkAndFastDeployment()
    {
        // Lo que imprime el script de GetAppInfoAsync en MAUI Debug: Fast Deployment copió PayloMaui.dll
        // después de instalar el APK.
        const string output = """
                versionCode=1 minSdk=21 targetSdk=36
                versionName=1.0
            apk 1791562174
            override 1791562200
            """;

        var app = AdbClient.ParseAppInfo("com.companyname.paylomaui", output);

        Assert.Equal("1.0 (1)", app.Version);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791562200).LocalDateTime, app.DeployedAt);
    }

    [Fact]
    public void AndroidAppThatIsNotDebuggableUsesTheApkTime()
    {
        // run-as falla en una app de Release y la línea queda vacía.
        var app = AdbClient.ParseAppInfo("com.driftcheck.paylonative", "versionName=2.3\napk 1791562174\noverride \n");

        Assert.Equal("2.3", app.Version);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791562174).LocalDateTime, app.DeployedAt);
    }

    [Fact]
    public void ParsesSimctlAppInfo()
    {
        const string output = """
            {
                ApplicationType = User;
                Bundle = "file:///Users/me/Library/Developer/CoreSimulator/Devices/A1/data/Containers/Bundle/Application/B2/PayloIos.app/";
                CFBundleDisplayName = PayloIos;
                CFBundleIdentifier = "com.driftcheck.payloios";
                CFBundleShortVersionString = "1.2";
                CFBundleVersion = 7;
                Path = "/Users/me/Library/Developer/CoreSimulator/Devices/A1/data/Containers/Bundle/Application/B2/PayloIos.app";
            }
            """;

        var (version, path) = SimulatorClient.ParseAppInfo(output);

        Assert.Equal("1.2 (7)", version);
        Assert.Equal("/Users/me/Library/Developer/CoreSimulator/Devices/A1/data/Containers/Bundle/Application/B2/PayloIos.app", path);
    }

    [Fact]
    public void WarnsWhenTheProjectChangedAfterTheAppWasInstalled()
    {
        var deployed = new DateTime(2026, 10, 9, 11, 9, 0);
        Touch("SignUpPage.xaml", deployed.AddMinutes(10));
        Touch("MauiProgram.cs", deployed.AddMinutes(-30));
        // Las salidas de compilación siempre son más nuevas y no cuentan.
        Touch(Path.Combine("bin", "Debug", "PayloMaui.dll.xml"), deployed.AddHours(1));

        var warning = ProjectFreshness.Check(_dir, new AppInfo("com.companyname.paylomaui", "1.0", deployed));

        Assert.Equal("La app instalada es del 2026-10-09 11:09, pero `SignUpPage.xaml` cambió el 2026-10-09 11:19: " +
                     "compila e instala la app antes de comparar, o esta comparación es de la versión anterior.", warning);
    }

    [Fact]
    public void NoWarningWhenTheAppIsUpToDate()
    {
        var deployed = new DateTime(2026, 10, 9, 11, 9, 0);
        Touch("SignUpPage.xaml", deployed.AddMinutes(-5));
        Touch("notes.txt", deployed.AddMinutes(30));

        Assert.Null(ProjectFreshness.Check(_dir, new AppInfo("com.companyname.paylomaui", "1.0", deployed)));
    }

    [Fact]
    public void WarnsWhenTheInstallTimeIsUnknown()
    {
        var warning = ProjectFreshness.Check(_dir, new AppInfo("com.companyname.paylomaui", null, null));
        Assert.StartsWith("No pude leer cuándo se instaló la app (com.companyname.paylomaui)", warning);
    }

    void Touch(string relative, DateTime time)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        File.SetLastWriteTime(path, time);
    }
}
