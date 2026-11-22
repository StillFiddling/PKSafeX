# NOTICE — 修改声明（GPLv3 §5(a)）

本仓库是 **[PKSafe](https://github.com/DeffoNotAlex/PKSafe)**（作者 DeffoNotAlex，GPLv3）的修改版本，
仓库名 **PKSafeX**，包名 `com.pksafe.x`。

上游 PKSafe 又基于 **[PKHeX](https://github.com/kwsch/PKHeX)**（作者 kwsch，GPLv3）的存档编辑内核。
本仓库继承了上述全部 GPLv3 条款，完整许可证文本见 `LICENSE`。

## 修改时间

2026 年 11 月

## 修改内容

1. 界面全简体中文化：XAML 与代码内文案统一改为中文；固定文案集中到 `PKHeX.Mobile/Services/UiText.cs`。
2. 触屏适配：底部提示条键帽可点按触发对应动作（`MainPage`、`GamePage`、`BankPage`）。
3. 顶部版式统一：箱子页 / 银行页顶部改为「游戏图标 + 训练家名 + 游戏名 + 右上角互跳按钮」；移除 TID/图鉴/游戏时间统计行。
4. 银行页：补齐存入 / 取出 / 抓取流程与存档条；移除物种选择器；新增「取出银行」。
5. 按键映射集中到 `PKHeX.Mobile/Services/KeyMap.cs`：叫声 R3→R1，进出银行 R1/L1→R2/L2。
6. 首页退出确认：B（含系统返回）首次提示，二次确认后退出。
7. 宝可梦编辑页：性别用 ♂ / ♀ / —；返回 / 保存按钮中文；操作说明移到保存按钮上方。
8. 应用名改为 PKSafeX，包名改为 `com.pksafe.x`，避免与上游混淆。
9. 移除首启动欢迎页的第三方粉丝改编配乐资源（版权归属任天堂 / Game Freak，且非 GPL 覆盖范围），保留静默降级。

## 未修改部分

`PKHeX.Core` 及其相关项目保持上游原样，仅作为项目引用使用。

## 版权

- PKSafe © DeffoNotAlex（GPLv3）
- PKHeX © kwsch 及贡献者（GPLv3）
- 本仓库修改部分 © PKSafeX 贡献者（GPLv3）

本项目与 Nintendo、Game Freak、The Pokémon Company 及上述作者均无隶属关系。
