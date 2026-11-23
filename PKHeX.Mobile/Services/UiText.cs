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
    public const string HintBack       = "返回";
    public const string HintSave       = "保存";
    public const string HintCry        = "叫声";
    public const string HintEdit       = "编辑";
    public const string HintSearch     = "搜索";

    // ── 箱子页 ──
    public const string SearchSpecies   = "搜索物种…";
    public const string SearchItems     = "搜索道具…";
    public const string BoxHintSubtitle = "右摇杆 左右换箱  |  L2/R2 进银行";

    // ── 银行页 / 顶部存档条 ──
    public const string NoSaveLoaded = "没有加载存档";

    public static string SaveMeta(int tid, int sid, int boxes) =>
        $"初训家 ID {tid}/{sid} · 箱子 {boxes} 个";

    public static string GameName(string version) => $"Pokémon {version}";

    // ── 首页 ──
    public const string QuitConfirm = "再按一次 B 键退出软件";
    public const string SaveCount   = "存档文件";

    // ── 宝可梦编辑页 ──
    public const string EditorHint = "↕ 上下移动   ◀▶ 切换   A 键盘   L/R 切换标签   ▶ 保存";
}
