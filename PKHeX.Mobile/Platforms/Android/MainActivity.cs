using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Views;
using AndroidX.Core.View;
using PKHeX.Mobile.Platforms.Android;
using PKHeX.Mobile.Services;

namespace PKHeX.Mobile;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges =
        ConfigChanges.ScreenSize |
        ConfigChanges.Orientation |
        ConfigChanges.UiMode |
        ConfigChanges.ScreenLayout |
        ConfigChanges.SmallestScreenSize |
        ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    public const int RequestPickDirectory = 9001;

    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // 音量键明确调「媒体音量」，避免个别 ROM 默认落到铃身上，看着像按了没反应
        VolumeControlStream = Android.Media.Stream.Music;
        // 屏幕方向由设置页的三态开关决定（默认强制竖屏），这里只负责按偏好应用一次
        ScreenOrientationService.ApplySaved();
        SetImmersiveMode();
        // 等 Shell 自己的返回回调注册完成后再加，保证我们排在最前面（后注册优先）
        Window?.DecorView?.Post(InstallBackCallback);
    }

    protected override void OnStop()
    {
        base.OnStop();
        // Dismiss the secondary-screen Presentation so it doesn't stay
        // visible on the bottom screen when the Home button sends the app
        // to the background. OnAppearing on each page calls Show() again
        // when the user returns, so no explicit re-show is needed here.
        var secondary = IPlatformApplication.Current?.Services
            .GetService<Services.ISecondaryDisplay>();
        secondary?.Hide();
    }

    /// <summary>
    /// Strips the Android focus highlight from every RecyclerView in the window.
    /// We intercept all gamepad keys at DispatchKeyEvent so view focus is unused.
    /// Posted to the main looper so it runs after MAUI inflates its content.
    /// </summary>
    private void DisableRecyclerViewFocusHighlight()
    {
        Window?.DecorView?.Post(() =>
        {
            StripFocusFromGroup(Window.DecorView as Android.Views.ViewGroup);
        });
    }

    private static void StripFocusFromGroup(Android.Views.ViewGroup? group)
    {
        if (group is null) return;
        for (int i = 0; i < group.ChildCount; i++)
        {
            var child = group.GetChildAt(i);
            if (child is AndroidX.RecyclerView.Widget.RecyclerView rv)
            {
                rv.Focusable = false;
                rv.FocusableInTouchMode = false;
                // Block focus from ever reaching item views inside the RecyclerView —
                // this is the definitive kill for the orange selection ring.
                rv.DescendantFocusability = Android.Views.DescendantFocusability.BlockDescendants;
            }
            else if (child is Android.Views.ViewGroup vg)
            {
                StripFocusFromGroup(vg);
            }
        }
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (!hasFocus) return;
        SetImmersiveMode();
        // Re-run every time the window gains focus so RecyclerViews created
        // after navigation (lazy page inflation) are also caught.
        DisableRecyclerViewFocusHighlight();
        // Re-show the secondary display when focus returns. OnStop() hides it
        // whenever another activity takes foreground (e.g. the SAF file picker),
        // but OnAppearing on the MAUI page doesn't fire in that case because the
        // navigation stack is unchanged — so we re-show it here instead.
        var secondary = IPlatformApplication.Current?.Services
            .GetService<Services.ISecondaryDisplay>();
        secondary?.Show();
    }

    private void SetImmersiveMode()
    {
        if (Window?.DecorView is null) return;
        WindowCompat.SetDecorFitsSystemWindows(Window, false);
        var ctrl = WindowCompat.GetInsetsController(Window, Window.DecorView);
        if (ctrl is null) return;
        ctrl.Hide(WindowInsetsCompat.Type.StatusBars() | WindowInsetsCompat.Type.NavigationBars());
        ctrl.SystemBarsBehavior = WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
    }


    public const int RequestPickFile      = 9002;
    public static event Action<Android.Net.Uri?>? DirectoryPickResult;
    public static event Action<Android.Net.Uri?>? FilePickResult;

    // Previous analog axis positions for edge detection
    private float _prevHatX, _prevHatY;
    private float _prevLX,   _prevLY;
    private float _prevRX;   // right stick horizontal

    private const float AxisThreshold = 0.45f;

    // ── Key events (digital buttons + digital D-pad) ──────────────────────
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e is not null)
        {
            // 音量/静音键一律交回系统，任何页面都不许吞。
            // 之前这里只按 source 判断手柄/摇杆，有些 ROM 会把侧键带上 JOYSTICK 位，
            // 于是音量键被判成手柄键、被 GamepadRouter 无条件消费，表现为按了没反应。
            if (e.KeyCode is Keycode.VolumeUp or Keycode.VolumeDown or Keycode.VolumeMute)
            {
                DiagLog.Add($"[KEY] volume {e.KeyCode} src={(int)e.Source} act={e.Action} -> 交给系统");
                return base.DispatchKeyEvent(e);
            }

            if ((e.Source & (InputSourceType.Gamepad | InputSourceType.Joystick)) != 0)
            {
                bool handled = GamepadRouter.Dispatch(e.KeyCode, e.Action);
                if (handled)
                    DiagLog.Add($"[KEY] consumed {e.KeyCode} src={(int)e.Source} act={e.Action}");
                else
                    DiagLog.Add($"[KEY] gamepad-no-handler {e.KeyCode} src={(int)e.Source}");
                if (handled) return true;
            }
        }

        return base.DispatchKeyEvent(e);
    }

    // ── 系统返回（含被映射成 BACK 的手柄 B）：首页先提示、再按一次退出 ──
    private void InstallBackCallback()
    {
        try
        {
            OnBackPressedDispatcher?.AddCallback(this, new QuitConfirmBackCallback(this));
        }
        catch { }
    }

    private sealed class QuitConfirmBackCallback : AndroidX.Activity.OnBackPressedCallback
    {
        private readonly MainActivity _activity;
        public QuitConfirmBackCallback(MainActivity activity) : base(true) => _activity = activity;

        private DateTime _lastBack = DateTime.MinValue;


        public override void HandleOnBackPressed()
        {
            // 防抖：一次按压可能被多处同时处理，600ms 内重复的返回直接忽略
            var now = DateTime.UtcNow;
            var gap = (now - _lastBack).TotalMilliseconds;
            if (gap < 600) return;
            _lastBack = now;

            // 当前页面若注册了「B 语义」的返回动作（关浮层 / 问是否保存 / 取消移动…），
            // 先交给它处理；它不处理才走下面的默认返回。
            if (Services.BackCoordinator.TryHandle()) return;

            var shell = Microsoft.Maui.Controls.Shell.Current;
            var page = shell?.CurrentPage;

            // 首页：先提示、再按一次退出
            if (page is PKHeX.Mobile.MainPage main)
            {
                main.GoBackOrQuit();
                return;
            }

            // 其他页面：等同于返回上一页（与页面内 B 键一致），栈底时回首页
            Microsoft.Maui.Controls.Application.Current?.Dispatcher.Dispatch(async () =>
            {
                try
                {
                    var sh = Microsoft.Maui.Controls.Shell.Current;
                    if (sh is null) return;
                    if (sh.Navigation.NavigationStack.Count > 1)
                    {
                        await sh.GoToAsync("..", false);
                        return;
                    }
                    await sh.GoToAsync("//MainPage");
                }
                catch
                {
                    // 导航失败时不退出应用，留给用户手动操作
                }
            });
        }
    }

    // ── Motion events (analog D-pad HAT axis + left stick) ───────────────
    public override bool DispatchGenericMotionEvent(MotionEvent? e)
    {
        if (e is not null
            && e.Action == MotionEventActions.Move
            && (e.Source & (InputSourceType.Joystick | InputSourceType.Gamepad)) != 0)
        {
            // D-pad HAT axis (most gamepads, including many built-in gamepad buttons)
            FireAxis(e.GetAxisValue(Axis.HatX), ref _prevHatX, Keycode.DpadLeft, Keycode.DpadRight);
            FireAxis(e.GetAxisValue(Axis.HatY), ref _prevHatY, Keycode.DpadUp,   Keycode.DpadDown);

            // Left analog stick (for games/devices that route D-pad through stick)
            FireAxis(e.GetAxisValue(Axis.X),    ref _prevLX,   Keycode.DpadLeft, Keycode.DpadRight);
            FireAxis(e.GetAxisValue(Axis.Y),    ref _prevLY,   Keycode.DpadUp,   Keycode.DpadDown);

            // Right stick horizontal → box scroll
            FireRightStick(e.GetAxisValue(Axis.Z), ref _prevRX);

            return true;
        }

        return base.DispatchGenericMotionEvent(e);
    }

    private static void FireRightStick(float value, ref float prev)
    {
        if      (value < -AxisThreshold && prev >= -AxisThreshold)
            GamepadRouter.DispatchBoxScroll(-1);
        else if (value >  AxisThreshold && prev <=  AxisThreshold)
            GamepadRouter.DispatchBoxScroll(+1);
        prev = value;
    }

    /// <summary>
    /// Edge-detects an analog axis crossing the threshold and fires the
    /// corresponding D-pad keycode exactly once per crossing.
    /// </summary>
    private static void FireAxis(float value, ref float prev, Keycode neg, Keycode pos)
    {
        if      (value < -AxisThreshold && prev >= -AxisThreshold)
            GamepadRouter.Dispatch(neg, KeyEventActions.Down);
        else if (value >  AxisThreshold && prev <=  AxisThreshold)
            GamepadRouter.Dispatch(pos, KeyEventActions.Down);
        // Release: fire a synthetic Up when the stick returns to centre
        else if (Math.Abs(value) <= AxisThreshold && Math.Abs(prev) > AxisThreshold)
            GamepadRouter.Dispatch(Math.Sign(prev) < 0 ? neg : pos, KeyEventActions.Up);

        prev = value;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == RequestPickDirectory)
            DirectoryPickResult?.Invoke(resultCode == Result.Ok ? data?.Data : null);
        else if (requestCode == RequestPickFile)
            FilePickResult?.Invoke(resultCode == Result.Ok ? data?.Data : null);
    }
}
