# PKSafeX

**PKSafeX 是 [PKSafe](https://github.com/DeffoNotAlex/PKSafe) 的简体中文修改版（非官方 fork）**，
基于 [PKHeX](https://github.com/kwsch/PKHeX) 的存档编辑内核，面向 Android 纯触屏手机做了完整中文化与触摸化改造。

> 本项目与原作者 DeffoNotAlex、kwsch，以及 Nintendo / Game Freak / The Pokémon Company 均无隶属或背书关系。
> 这是社区修改版，仅按 GPLv3 分发。

- 包名：`com.pksafe.x`（**与上游 `com.pksafe.app` 不同**，签名也不同，两者不能互相覆盖安装，数据各自独立）
- 版本：**1.0.1 Beta**（versionCode 16）
- 授权：[GPLv3](LICENSE)

> **当前适配范围（1.0.1 测试版）**
> - ✅ 已优化：**手机竖屏**（单屏触摸操作为主）
> - ⚠️ 暂未优化：**横屏**（布局可能错位/拥挤，请以竖屏使用）
> - ⏳ 安卓掌机（如 AYN Thor 双屏机型）：尚未适配，可期待后续版本


## 与原版的差异

- **全中文界面**：首页、箱子页、银行页、道具页、宝可梦编辑页、设置页文案全部简体中文。
- **纯触屏可用**：底部提示条每一枚"键帽"都可直接点按，等同按手柄同键；无手柄也能完成全部操作。
- **银行页重做**：补齐"存入/取出/抓取"流程，顶部存档条与箱子页同款版式。
- **顶部版式统一**：箱子页 / 银行页顶部只保留「游戏图标 + 训练家名 + 游戏名 + 右上角互跳按钮」。
- **键位调整**（见下文"键位"）：叫声 R3→R1，进出银行 R1/L1→R2/L2。
- **首页退出确认**：按 B（手柄 B / 系统返回 / 点底部 B 徽章）先提示，再按一次才退出。
- **宝可梦编辑页**：性别显示为 ♂ / ♀ / —，返回与保存按钮中文，操作说明移到保存按钮上方。
- **包名改为 `com.pksafe.x`**，避免与上游 APK 混淆。

## 构建

要求：.NET 10 SDK + MAUI workload、Android SDK (API 26+)。

```bash
dotnet build PKHeX.Mobile/PKHeX.Mobile.csproj -f net10.0-android -c Release
dotnet build PKHeX.sln
```

产物：`PKHeX.Mobile/bin/Release/net10.0-android/com.pksafe.x.apk`（未签名），自行用 apksigner 签名后安装。

## 代码组织（改动集中在少数文件里，便于以后合并上游）

| 内容 | 位置 |
| --- | --- |
| 界面文案（中文唯一来源） | `PKHeX.Mobile/Services/UiText.cs` |
| 按键映射（改键只改这里） | `PKHeX.Mobile/Services/KeyMap.cs` |
| 底部提示条强调色 / 键帽配色 | `PKHeX.Mobile/Services/HintPalette.cs` |
| 游戏内名词（宝可梦/招式/道具） | `PKHeX.Mobile/Services/GameLang.cs`（复用 PKHeX 自带 zh-Hans 词条） |
| 主题色 | `PKHeX.Mobile/Services/ThemeService.cs` 与 `PKHeX.Mobile/Theme/*.xaml` |

XAML 里的固定文案用静态引用指向 `UiText`，例如：

```xml
<Label Text="{x:Static svc:UiText.HintCry}" />
```

## 键位

| 动作 | 按键 |
| --- | --- |
| 导航 | 方向键 / 左摇杆 / 十字键 |
| 选中 / 放入 | A |
| 返回 | B（首页：连按两次退出） |
| 抓取 | Y |
| 打开键盘 | X（编辑页） |
| 叫声 | **R1** |
| 进银行 / 出银行 | **R2 / L2** |
| 箱子前后 | L1 / R1（或在箱子页用右摇杆左右拨） |
| 菜单 / 设置 | Start |

## Credits

- **PKHeX** by kwsch — 存档解析与编辑内核（GPLv3）
- **PKSafe** by DeffoNotAlex — Android 前端基础（GPLv3）
- **PokeAPI Sprites** — Pokémon HOME 风格精灵图（CC0）
- **Pokémon Showdown** — 动画精灵 CDN
- **pokesprite** — 异色精灵图（MIT）
- **QRCoder**（MIT）、**SkiaSharp**、**Phosphor Icons**（MIT）

## 免责声明

本项目不鼓励、也不支持以损害他人为前提的作弊行为；请勿在对战或交换中使用明显修改的宝可梦。
本项目与 Nintendo、Game Freak、The Pokémon Company 无关。

## License

GNU General Public License v3.0，见 [LICENSE](LICENSE)。
本仓库是对 PKSafe 的修改版本，修改说明见 [NOTICE.md](NOTICE.md)。
