# 构建与签名（BUILD）

本文件记录 PKSafeX 的可复现构建方式。换电脑开发时照此执行即可，产物签名保持不变。

## 1. 需要的工具链

| 组件 | 版本 / 路径 | 说明 |
|---|---|---|
| .NET SDK | 10.0.x | 需要 `net10.0-android` 工作负载（`dotnet workload install maui-android`） |
| JDK | 17 | 编译 + apksigner 都需要 |
| Android SDK | platform 36 + build-tools 36.0.0 + platform-tools | 含 `zipalign`、`apksigner`、`aapt2`、`adb` |
| Git | 任意 | 推送需 GitHub 授权 |

> 本仓库不包含任何签名密钥，签名库必须自行保管（见第 4 节）。

## 2. 环境变量

```powershell
$env:JAVA_HOME      = "D:\Dev\jdk\jdk-17.0.13+11"
$env:ANDROID_HOME   = "D:\Android\android-sdk"
$env:NUGET_PACKAGES = "D:\Dev\nuget-cache"
$env:DOTNET_CLI_HOME= "D:\Dev\dotnet-home"
$env:MSBUILDDISABLENODEREUSE = "1"
```

## 3. 编译

```powershell
cd <仓库根>
dotnet build PKHeX.Mobile\PKHeX.Mobile.csproj -c Release -f net10.0-android `
  -p:AndroidSdkDirectory="$env:ANDROID_HOME" `
  -p:JavaSdkDirectory="$env:JAVA_HOME" `
  -p:AndroidPackageFormat=apk -m:1 -nodeReuse:false
```

未签名产物：`PKHeX.Mobile\bin\Release\net10.0-android\com.pksafe.x.apk`
（约 2–3 分钟；首次编译要还原 NuGet，时间更久）

## 4. 签名（关键）

升级安装能否覆盖，只看**签名证书是否相同**，与在哪台电脑编译无关。
因此必须始终使用同一个 keystore、同一个 alias、同一组口令。

```powershell
$bt = "$env:ANDROID_HOME\build-tools\36.0.0"

# 1) 对齐
& "$bt\zipalign.exe" -f 4 "PKHeX.Mobile\bin\Release\net10.0-android\com.pksafe.x.apk" out\_aligned.apk

# 2) 签名（keystore 路径与口令换成你自己的）
& "$bt\apksigner.bat" sign `
  --ks D:\PKSafe\PKSafe-buildkey.keystore --ks-key-alias pksafe `
  --ks-pass pass:<storePassword> --key-pass pass:<keyPassword> `
  --out out\PKSafeX-<version>.apk out\_aligned.apk
```

**当前发布用的证书指纹（SHA-256）**，换机器后务必比对一致：

```
CN=PKSafe Build, O=PKSafe, C=CN
A5:CD:00:35:DC:B1:5C:9A:F0:02:5C:D6:74:1B:0A:F5:3B:BF:4B:A8:DE:36:18:3F:06:77:A3:66:60:BA:23:19
```

校验命令：

```powershell
& "$bt\apksigner.bat" verify --print-certs out\PKSafeX-1.0.1-beta.apk
```

输出的 `certificate SHA-256 digest` 必须与上面一致。不一致的包无法覆盖升级，用户必须先卸载（会丢数据）。

## 5. 版本号与发布清单

1. `PKHeX.Mobile\PKHeX.Mobile.csproj`：
   - `<ApplicationVersion>`（versionCode，整数，**每次发布必须 +1**）
   - `<ApplicationDisplayVersion>`（versionName，如 `1.0.1`）
2. 同步 `README.md` 版本行、`CHANGELOG.md` 新段落。
3. 编译 → 对齐 → 签名 → `aapt2 dump badging` 与 `apksigner verify` 双重核对。
4. 真机 `adb install -r -g -d` 验证。
5. `git tag v<version>` → 推送 → `gh release create`（附 APK）。

## 6. 密钥备份（必读）

- **keystore 一旦丢失，就无法再发布能覆盖旧版的更新**，只能换包名或让用户卸载重装。
- 建议：把 keystore 与口令分别存进密码管理器 / 加密压缩包，至少两处异地备份。
- 千万不要把 `.keystore`、口令、`keystore.properties` 提交进仓库（`.gitignore` 已屏蔽）。
