namespace PKHeX.Mobile.Services;

/// <summary>
/// 界面文案唯一入口（简体中文）。
/// 代码里不要再直接写中文字面量；XAML 里的固定文案用 {x:Static svc:UiText.XXX} 引用本类。
/// </summary>
public static class UiText
{
    // ── 通用 ──
    public const string AppName    = "PKSafeX";
    public const string Bank       = "银行";
    public const string BankArrow  = "银行 ▸";
    public const string Box        = "箱子";
    public const string BoxArrow   = "箱子 ▸";
    public const string Items      = "道具";
    public const string EmptySlot  = "空位";
    public const string Save       = "保存";
    public const string Back       = "返回";

    // ── 底部提示条 ──
    public const string HintNav        = "导航";
    public const string HintSelect     = "选中";
    public const string HintSelectPlace = "选中/放入";
    public const string HintGrab       = "抓取";
    public const string HintGrabOrGift = "抓取/礼物";
    public const string HintBack       = "返回";
    public const string HintSave       = "保存";
    public const string HintCry        = "叫声";
    public const string HintEdit       = "编辑";
    public const string HintManage     = "管理";
    public const string HintView       = "视图";
    public const string KeyStartChar   = "Start";
    public const string KeySelectChar  = "Select";
    public const string HintSearch     = "搜索";
    public const string HintSettings   = "设置";

    // ── 存档切换浮层 ──
    public const string BoxSwitchNote  = "切换后箱子页会载入所选存档，未保存的改动会先写回原存档。";
    public const string BankSwitchNote = "银行里的宝可梦是全局共用的，切换存档不会改变银行内容。";

    // ── 箱子页 ──
    public const string SearchSpecies   = "搜索物种…";
    public const string SearchItems     = "搜索道具…";
    public const string BoxHintSubtitle = "右摇杆 左右换箱  |  L2/R2 进银行";

    // ── 银行页 / 顶部存档条 ──
    public const string NoSaveLoaded = "没有加载存档";
    // 与箱子页同款操作提示（右摇杆换箱 + 返回箱子页的按键）
    public const string BankHintSubtitle = "右摇杆 左右换箱  |  L2/R2 回箱子";

    public static string SaveMeta(int tid, int sid, int boxes) =>
        $"初训家 ID {tid}/{sid} · 箱子 {boxes} 个";

    public static string GameName(string version) => $"Pokémon {version}";

    // ── 首页 ──
    public const string QuitConfirm = "再按一次 B 键退出软件";
    public const string PickSave    = "先在上方选一张存档";
    public const string DualUnsupported = "暂不支持双屏设备，已按单屏横屏模式运行";
    public const string SaveCount   = "存档文件";

    // ── 宝可梦编辑页 ──
    public const string EditorHint = "↕ 上下移动   ◀▶ 切换   A 键盘   L/R 切换标签   ▶ 保存";
}
