using PKHeX.Mobile.Pages;
using PKHeX.Mobile.Services;

namespace PKHeX.Mobile;

public partial class App : Application
{
    /// <summary>构造函数里发生的异常（若有），用于在窗口里直接显示原因而不是闪退。</summary>
    internal static Exception? StartupError { get; private set; }

    public App()
    {
        CrashLog.Install();

        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            StartupError = ex;
            CrashLog.Write("App.InitializeComponent", ex);
        }

        try
        {
            ThemeService.ApplyOnStartup();
        }
        catch (Exception ex)
        {
            CrashLog.Write("ThemeService.ApplyOnStartup", ex);
        }

        try
        {
            SettingsPage.ApplyOnStartup();
        }
        catch (Exception ex)
        {
            CrashLog.Write("SettingsPage.ApplyOnStartup", ex);
        }

        // 打启动标记：若下次启动它还躺在磁盘上，说明这次启动中途崩了
        CrashLog.MarkBootStarted();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        if (StartupError is not null)
            return new Window(BuildErrorPage(StartupError));

        // 上次启动未走完 → 先亮出诊断页，避免盲崩，也方便截图反馈
        if (CrashLog.LastBootUnhealthy() && CrashLog.HasLog)
            return new Window(BuildDiagnosticPage());

        try
        {
            return new Window(new AppShell());
        }
        catch (Exception ex)
        {
            CrashLog.Write("AppShell", ex);
            return new Window(BuildErrorPage(ex));
        }
    }

    /// <summary>启动期出错时展示的兜底页面（可截图反馈）。</summary>
    internal static ContentPage BuildErrorPage(Exception ex)
    {
        var label = new Label
        {
            Text = $"启动失败：\n\n{ex.GetType().Name}\n{ex.Message}\n\n--- 详细信息 ---\n{ex}",
            FontSize = 11,
            Margin = new Thickness(16),
        };
        return new ContentPage
        {
            Content = new ScrollView { Content = label },
        };
    }

    /// <summary>上次启动崩溃时的诊断页：显示崩溃摘要，并提供「继续进入」和「清除记录」。</summary>
    internal static ContentPage BuildDiagnosticPage()
    {
        var label = new Label
        {
            Text = "上次启动没有走完，以下是记录到的异常摘要：\n\n" + CrashLog.ReadLast(1200),
            FontSize = 11,
            Margin = new Thickness(16, 16, 16, 8),
        };

        var clearBtn = new Button { Text = "清除记录", Margin = new Thickness(16, 0, 16, 8) };
        clearBtn.Clicked += (_, _) =>
        {
            CrashLog.Clear();
            GoToShell(clearBtn);
        };

        var goBtn = new Button { Text = "继续进入软件", Margin = new Thickness(16, 0, 16, 16) };
        goBtn.Clicked += (_, _) => GoToShell(goBtn);

        return new ContentPage
        {
            Content = new ScrollView
            {
                Content = new VerticalStackLayout
                {
                    Children = { label, clearBtn, goBtn },
                },
            },
        };
    }

    private static void GoToShell(VisualElement anchor)
    {
        try
        {
            CrashLog.MarkBootHealthy();
            var window = anchor.Window ?? Current?.Windows.FirstOrDefault();
            if (window is not null)
                window.Page = new AppShell();
        }
        catch (Exception ex)
        {
            CrashLog.Write("App.GoToShell", ex);
        }
    }
}
