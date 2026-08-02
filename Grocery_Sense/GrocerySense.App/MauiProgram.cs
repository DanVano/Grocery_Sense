using Microsoft.Extensions.Logging;
using MudBlazor.Services;

namespace GrocerySense.App;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		// No ConfigureFonts: the whole UI is a BlazorWebView, so text is styled by MudBlazor's web CSS —
		// a registered native font would have no consumer.
		var builder = MauiApp.CreateBuilder();
		builder.UseMauiApp<App>();

		builder.Services.AddMauiBlazorWebView();
		builder.Services.AddMudServices();
		builder.Services.AddGrocerySenseServices();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
