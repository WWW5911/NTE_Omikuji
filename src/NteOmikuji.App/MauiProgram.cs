using Microsoft.Extensions.Logging;
using NteOmikuji.App.Services;

namespace NteOmikuji.App;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		// WebView2 otherwise writes its profile beside the exe, which may be read-only.
		// An elevated WebView2 also cannot share a profile with a normal one, and the
		// normal instance may still be closing when the elevated one starts.
		if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER")))
		{
			Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"NteOmikuji",
				Elevation.IsElevated ? "WebView2-Admin" : "WebView2"));
		}

		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();
		builder.Services.AddSingleton<AppState>();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
