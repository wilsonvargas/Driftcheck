using Microsoft.Extensions.Logging;

namespace PayloMaui;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
				fonts.AddFont("Inter-Regular.ttf", "InterRegular");
				fonts.AddFont("Inter-SemiBold.ttf", "InterSemiBold");
				fonts.AddFont("Lato-Regular.ttf", "LatoRegular");
			});

		// Los campos del diseño no tienen el subrayado ni el relleno que Android pone a los EditText
		// (ni el espacio extra de includeFontPadding, ver el Label más abajo), ni el borde redondeado
		// de los UITextField de iOS.
		Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("NoUnderline", (handler, _) =>
		{
#if ANDROID
			handler.PlatformView.Background = null;
			handler.PlatformView.SetPadding(0, 0, 0, 0);
			handler.PlatformView.SetIncludeFontPadding(false);
#elif IOS
			handler.PlatformView.BorderStyle = UIKit.UITextBorderStyle.None;
#endif
		});

		// Figma reparte el interlineado según las métricas de la fuente; Android, con includeFontPadding,
		// agrega espacio extra arriba y abajo: el título de 32 crecía y empujaba el encabezado.
		Microsoft.Maui.Handlers.LabelHandler.Mapper.AppendToMapping("NoFontPadding", (handler, _) =>
		{
#if ANDROID
			handler.PlatformView.SetIncludeFontPadding(false);
#endif
		});

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
