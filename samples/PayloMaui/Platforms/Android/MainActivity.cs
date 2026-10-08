using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;

namespace PayloMaui;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);

		// El diseño usa una barra de estado clara (#F5F5F5) con íconos oscuros.
		if (Window is { } window)
			WindowCompat.GetInsetsController(window, window.DecorView).AppearanceLightStatusBars = true;
	}
}
