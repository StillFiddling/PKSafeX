#if ANDROID
using Android.Views;

namespace PKHeX.Mobile.Services;

/// <summary>
/// 一个逻辑动作对应的物理按键（含提示条上显示的键名）。
/// </summary>
public readonly struct KeyBinding
{
    public readonly Keycode Code;
    public readonly string Glyph;

    public KeyBinding(Keycode code, string glyph)
    {
        Code = code;
        Glyph = glyph;
    }
}

/// <summary>
/// 全应用按键映射的唯一入口：需要改键（或换手柄/掌机）时只改这个文件。
/// 方向键与 A/B 属通用约定，页面仍直接判断 DpadUp/Down/Left/Right、ButtonA/ButtonB；
/// 其余动作（叫声、进出银行、抓取、菜单…）统一从这里取。
/// </summary>
public static class KeyMap
{
    // ── 键码（常量，可直接用于 switch case）──
    public const Keycode ConfirmKey = Keycode.ButtonA;
    public const Keycode CancelKey  = Keycode.ButtonB;
    public const Keycode AltKey     = Keycode.ButtonX;
    public const Keycode GrabKey    = Keycode.ButtonY;

    /// <summary>叫声：原 R3，改为 R1。</summary>
    public const Keycode CryKey = Keycode.ButtonR1;

    /// <summary>进银行：原 R1，改为 R2。</summary>
    public const Keycode BankInKey = Keycode.ButtonR2;

    /// <summary>出银行：原 L1，改为 L2。</summary>
    public const Keycode BankOutKey = Keycode.ButtonL2;

    public const Keycode BoxPrevKey = Keycode.ButtonL1;
    public const Keycode BoxNextKey = Keycode.ButtonR1;

    public const Keycode MenuKey   = Keycode.ButtonStart;
    public const Keycode SelectKey = Keycode.ButtonSelect;

    /// <summary>右摇杆按下（旧版叫声键位，保留常量以防回退）。</summary>
    public const Keycode StickClickRightKey = Keycode.ButtonThumbr;

    // ── 带提示键名的绑定（底部提示条用）──
    public static readonly KeyBinding Confirm = new(ConfirmKey, "A");
    public static readonly KeyBinding Cancel  = new(CancelKey, "B");
    public static readonly KeyBinding Alt     = new(AltKey, "X");
    public static readonly KeyBinding Grab    = new(GrabKey, "Y");
    public static readonly KeyBinding Cry     = new(CryKey, "R");
    public static readonly KeyBinding BankIn  = new(BankInKey, "R2");
    public static readonly KeyBinding BankOut = new(BankOutKey, "L2");
    public static readonly KeyBinding BoxPrev = new(BoxPrevKey, "L1");
    public static readonly KeyBinding BoxNext = new(BoxNextKey, "R1");
    public static readonly KeyBinding Menu    = new(MenuKey, "≡");
    public static readonly KeyBinding Select  = new(SelectKey, "Select");
}
#else
namespace PKHeX.Mobile.Services;

public readonly struct KeyBinding
{
    public readonly string Glyph;
    public KeyBinding(string glyph) => Glyph = glyph;
}

public static class KeyMap
{
    public static readonly KeyBinding Confirm = new("A");
    public static readonly KeyBinding Cancel  = new("B");
    public static readonly KeyBinding Grab    = new("Y");
    public static readonly KeyBinding Cry     = new("R");
}
#endif
