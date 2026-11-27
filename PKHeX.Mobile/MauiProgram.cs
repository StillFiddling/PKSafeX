using PKHeX.Mobile.Services;
using SkiaSharp.Views.Maui.Controls.Hosting;

namespace PKHeX.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseSkiaSharp()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("Nunito-Regular.ttf", "Nunito");
                fonts.AddFont("Nunito-Bold.ttf", "NunitoBold");
                fonts.AddFont("Nunito-ExtraBold.ttf", "NunitoExtraBold");
                fonts.AddFont("Quicksand-Bold.ttf", "Quicksand");
                fonts.AddFont("Quicksand-ExtraBold.ttf", "QuicksandExtraBold");
                fonts.AddFont("Phosphor-Regular.ttf", "Phosphor");
                fonts.AddFont("Phosphor-Fill.ttf", "PhosphorFill");
            })
            .ConfigureMauiHandlers(handlers =>
            {
#if ANDROID
                Microsoft.Maui.Handlers.WebViewHandler.Mapper.AppendToMapping(
                    "EnableJavaScript", (handler, _) =>
                        handler.PlatformView.Settings.JavaScriptEnabled = true);

#endif
            });

        builder.Services.AddSingleton<IFileService, FileService>();
        builder.Services.AddSingleton<DexService>();
        builder.Services.AddSingleton<SessionState>();
        builder.Services.AddTransient<MainPage>();
        builder.Services.AddTransient<Pages.GamePage>();
        // 编辑页复用同一实例：避免每次跳转都在 UI 线程 inflate 53KB XAML（约 300ms 卡顿）
        builder.Services.AddSingleton<Pages.PkmEditorPage>();
        builder.Services.AddTransient<Pages.BankViewPage>();
        builder.Services.AddTransient<Pages.BankPage>();
        builder.Services.AddTransient<Pages.WelcomePage>();
        builder.Services.AddTransient<Pages.DexPage>();
        builder.Services.AddTransient<Pages.SettingsPage>();
        builder.Services.AddTransient<Pages.DatabasePage>();
        builder.Services.AddTransient<Pages.MysteryGiftDBPage>();
        builder.Services.AddTransient<Pages.BoxPage>();

#if ANDROID
        // 双屏（Thor 类设备）暂时关闭，统一按单屏跑；AppFlags.DualScreenEnabled 改回 true 即可恢复。
        // 注意：ThorSecondaryDisplay 仍保留在代码里，只是在关闭期间不注册。
        if (AppFlags.DualScreenEnabled)
            builder.Services.AddSingleton<ISecondaryDisplay, Platforms.Android.ThorSecondaryDisplay>();
        else
            builder.Services.AddSingleton<ISecondaryDisplay, SingleScreenFallback>();
#else
        builder.Services.AddSingleton<ISecondaryDisplay, SingleScreenFallback>();
#endif

        return builder.Build();
    }
}
