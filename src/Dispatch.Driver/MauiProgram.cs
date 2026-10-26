using Dispatch.Driver.Services;
using Microsoft.Extensions.Logging;

namespace Dispatch.Driver;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts => fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular"));

        builder.Services.AddMauiBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        builder.Services.AddSingleton<TokenStore>();
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddTransient<AuthHandler>();

        builder.Services.AddHttpClient("auth", c =>
        {
            c.BaseAddress = new Uri(AppConfig.ServerUrl);
            c.Timeout = TimeSpan.FromSeconds(20);
        });
        builder.Services.AddHttpClient("api", c =>
        {
            c.BaseAddress = new Uri(AppConfig.ServerUrl);
            c.Timeout = TimeSpan.FromSeconds(60);   // photo uploads on slow connections
        }).AddHttpMessageHandler<AuthHandler>();

        builder.Services.AddSingleton<DriverApiClient>();
        builder.Services.AddSingleton<LocationQueue>();
        builder.Services.AddSingleton<ShiftService>();
        builder.Services.AddSingleton<DriverState>();

#if ANDROID
        builder.Services.AddSingleton<ILocationTracker, Dispatch.Driver.Droid.AndroidLocationTracker>();
#elif IOS
        builder.Services.AddSingleton<ILocationTracker, Dispatch.Driver.iOSPlatform.IosLocationTracker>();
#endif

        return builder.Build();
    }
}
