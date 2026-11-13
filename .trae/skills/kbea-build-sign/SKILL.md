---
name: kbea-build-sign
description: Build publish and sign the KBEA WPF project. Use when the user asks to build publish release rebuild or package the app. Runs publish.ps1 for both Portable and Light self-contained builds then signs with signtool.
---

# KBEA Build + Publish + Sign

发布 KBEA WPF 应用。一个脚本完成构建 + 发布 + 签名 + 验证，产物统一到 `artifacts/`。

## 产物

| 目录 | 类型 | 说明 | 运行时 |
|---|---|---|---|
| `artifacts/KBEA-Portable/` | single-file self-contained | KBEA.exe ~159 MB + KBEA.Worker.exe ~72 MB，给最终用户 | 自带完整 runtime |
| `artifacts/KBEA-Light/` | multi-file self-contained | 500+ files ~275 MB，方便逐个签名 + 调试 | 自带完整 runtime |

**两者都 self-contained，不依赖系统 .NET，reboot 后不会再出现 hostfxr.dll 找不到。**

> **KBEA.Worker.exe** 是后台元数据服务进程（独立于前台，任务队列用共享 SQLite）。前台按 `AppContext.BaseDirectory` 相对路径拉起它，两份产物目录里各有一份。

## 前置条件

- .NET SDK：`c:\Users\qizic\Desktop\Projects\rsiKB\.dotnet\dotnet.exe`（系统 PATH 里没有）
- signtool：`C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe`
- 签名证书：`CN=7zThinking`，SHA1 `1583576BBF01ADB342EDC1CB9BCA018E7FC71381`（CurrentUser\My 存储）
- 时间戳：`http://timestamp.digicert.com`（多个备选见 sign.ps1）
- 根证书：`C:\Users\qizic\AppData\Roaming\KBEA-Certs\7zThinking-Root-CA.cer`（已导入 LocalMachine\Root + TrustedPublisher）
- PFX 备份：`C:\Users\qizic\AppData\Roaming\KBEA-Certs\7zThinking-CodeSigning.pfx`（含私钥，需离线保管）

## 一键流程

```powershell
cd c:\Users\qizic\Desktop\Projects\rsiKB

# Step 1: 构建 + 发布（同时出 Portable + Light，都 self-contained）
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1

# Step 2: 签名（递归签 artifacts\ 下所有 exe/dll + 验证）
powershell -NoProfile -ExecutionPolicy Bypass -File .\sign.ps1
```

**或双击**：`publish.ps1` + `sign.bat`（sign.bat 带 pause 等你看结果）

## 增量签名（日常改代码后的默认方式）

**原则：publish 产物里哪个文件的内容变了，就重签哪个。其余 477 个运行时 dll 内容没变，旧签名依然有效，永远不用重签。**

### 判断逻辑（为什么是这几个文件）

签名绑定的是文件内容（Authenticode 对文件哈希签名），dll 字节没变签名就继续有效。所以增量签名的本质是：**搞清楚"这次改动经过 publish 后，落到了产物里的哪几个文件"**。两个发布版的落点不同：

- **Portable（单文件）**：所有托管 dll 都打进唯一的 KBEA.exe → Portable **永远只签 KBEA.exe**（Worker 改动时另加 Worker.exe）
- **Light（多文件）**：每个项目独立成 dll → 按下表落点补签

### 修改落点映射表

| 这次改了什么 | Light 补签 | Portable 补签 |
|---|---|---|
| KBEA.App（界面/ViewModel/XAML） | KBEA.exe + KBEA.dll | KBEA.exe |
| KBEA.Core（核心库） | KBEA.Core.dll | KBEA.exe |
| App + Core 都改（最常见） | KBEA.exe + KBEA.dll + KBEA.Core.dll | KBEA.exe（合计 4 个，即"3+1"） |
| KBEA.Worker（后台进程） | KBEA.Worker.exe | KBEA.Worker.exe |
| 新增/升级 NuGet 包 | 对应新 dll 出现在 Light 根目录 → 一并签 | 已打进 KBEA.exe，签 exe 即可 |
| 只改文档/测试/脚本 | 不用签（产物没变，连 publish 都可跳过） | 不用签 |

### 标准操作

```powershell
# 1. 重新发布（publish 会覆盖产物，被更新的文件变回未签名状态）
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1

# 2. 补签受影响文件（按上表选择；App+Core 全改的例子）
Stop-Process -Name KBEA,KBEA.Worker -Force -ErrorAction SilentlyContinue
$sig="C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
foreach($t in @("artifacts\KBEA-Light\KBEA.exe","artifacts\KBEA-Light\KBEA.dll","artifacts\KBEA-Light\KBEA.Core.dll")){
  & $sig sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /sha1 1583576BBF01ADB342EDC1CB9BCA018E7FC71381 $t
}
& $sig sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /sha1 1583576BBF01ADB342EDC1CB9BCA018E7FC71381 "artifacts\KBEA-Portable\KBEA.exe"
```

跑完后必须验证：

```powershell
$files=Get-ChildItem artifacts -Recurse -Include *.exe,*.dll -File
$unsigned=@(); foreach($f in $files){ & $sig verify /pa $f.FullName *> $null; if($LASTEXITCODE -ne 0){$unsigned += $f.FullName} }
"total=$($files.Count) unsigned=$($unsigned.Count)"; $unsigned
```

期望 `unsigned=0`。若有遗漏，`$unsigned` 直接列出漏签文件的完整路径——**照列表补签即可，仍不需要全量**。

### 什么情况才必须全量重签（sign.ps1，5-8 分钟）

只有产物结构大变时才值得全量：

- .NET SDK / 运行时版本升级（477 个运行时 dll 内容全变）
- 发布参数变更（TargetFramework、RID、单文件开关等）
- 从零重建 artifacts 目录

**反模式提醒：不要因为"不确定哪些文件变了"就全量重签。** 先跑 unsigned 验证看列表，列表里是什么就补什么；拿不准落点时查上面的映射表。

## 冒烟验证

```powershell
# 构建后启动应用，确认关键 UI 元素存在
$env:DOTNET_ROOT="$PWD\.dotnet"
& .\.dotnet\dotnet.exe build src\KBEA.Smoke\KBEA.Smoke.csproj -c Release --nologo
& ".\src\KBEA.Smoke\bin\Release\net8.0-windows\KBEA.Smoke.exe"
```

冒烟脚本用 UIAutomation 列出窗口内所有按钮与关键控件，验证启动无 XAML 解析错误。

**⚠️ `DOTNET_ROOT` 必须设**，否则 framework-dependent 输出的 exe 报 `hostfxr.dll not found`（0x80008083）。

**⚠️ 模态窗口的 UIA 陷阱**：`InvokePattern.Invoke()` 会同步阻塞直到模态窗口关闭，必须放到后台线程调用，再轮询查找窗口。

## 测试项目

| 项目 | 用途 | 跑法 |
|---|---|---|
| `tests/KBEA.Tests` | xUnit 单测（14 项） | `dotnet test tests\KBEA.Tests -c Release` |
| `src/KBEA.Core.Tests` | 知识检索端到端回归（17 项断言） | `dotnet run --project src\KBEA.Core.Tests -c Release` |
| `src/KBEA.Worker.Tests` | 后台 Worker 任务队列 E2E（24 项断言） | `dotnet run --project src\KBEA.Worker.Tests -c Release` |
| `src/KBEA.Smoke` | UI 冒烟（UIAutomation） | 见上 |

`KBEA.Worker.Tests` 还支持 `enqueue <空间路径> <任务库路径>` 子命令，用于手工向任务库投递任务。

## 入口脚本

- `run-light.bat` — 开发/测试用，启动 `artifacts\KBEA-Light\KBEA.exe`（bat 原生 `start` 命令，无 PowerShell 嵌套）
- `KBEA-Portable\KBEA.exe` — 最终用户用，直接双击

> **⚠️ bat 文件必须是纯 ASCII**。cmd.exe 按系统 ANSI 代码页（中文系统是 GBK）读取 `.bat`，中文注释会被拆成乱码命令（表现为 `'KUP' 不是内部或外部命令`），可能连带破坏后续命令解析，导致应用"闪一下就没了"。写 bat 注释一律用英文。

## "闪一下就没了"排查顺序

窗口一闪即消，按这个顺序查：

1. **进程是否还活着**：`Get-Process KBEA` —— 进程没了是崩溃，进程还在但 `MainWindowHandle=0` 是 UI 线程阻塞在 `window.Show()` 之前
2. **看 Event Viewer**：`Get-WinEvent -LogName Application` 过滤 `KBEA`，找 .NET Runtime 错误
3. **对比开发版**：跑 `src\KBEA.App\bin\Release\net8.0-windows\KBEA.exe`。开发版正常 = 发布产物问题；开发版也闪 = 代码问题
4. **加启动埋点**：在 `App.OnStartup` 与 `MainViewModel` 构造、`Load()`、`OpenSpace()` 里逐段打日志，精确定位卡在哪一行（可用环境变量 `KBEA_DIAG` 指定日志文件路径）

### 已知的两个真实原因

| 现象 | 根因 | 处置 |
|---|---|---|
| bat 启动闪退，但直接跑 exe 正常 | bat 里的中文注释被 GBK 乱码，破坏命令解析 | bat 改纯 ASCII |
| 进程活着但窗口不出现（大仓库空间） | `MountTabViewModel.Refresh()` 在 UI 线程同步跑 Git 索引扫描（LibGit2Sharp `RetrieveStatus`），大仓库耗时数十秒 | Git 状态改为后台 `Task.Run` 异步补齐，文件列表先显示 |

## 发布配置

`publish.ps1` 同时执行两次 `dotnet publish`：

```
Portable: dotnet publish src/KBEA.App/KBEA.App.csproj
    -c Release --runtime win-x64 --self-contained true
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
    -p:DebugType=None -p:DebugSymbols=false
    -o artifacts/KBEA-Portable

Light: dotnet publish src/KBEA.App/KBEA.App.csproj
    -c Release --runtime win-x64 --self-contained true
    -p:PublishSingleFile=false
    -p:DebugType=None -p:DebugSymbols=false
    -o artifacts/KBEA-Light
```

**关键：两个版本都必须 `--self-contained true`。** framework-dependent 版本 reboot 后会找不到 hostfxr.dll，因为 DOTNET_ROOT 不是系统级 env var。

## 签名配置

`sign.ps1` 递归签 `artifacts\` 下所有 `*.exe` 和 `*.dll`（含 runtimes 子目录原生 dll 和 .NET resources.dll）。

```
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /sha1 1583576BBF01ADB342EDC1CB9BCA018E7FC71381 <file>
```

签名后自动验证所有文件。**480+ 文件，约 3-5 分钟**（每个文件一次 RSA 签名 + RFC3161 时间戳请求）。

## SAC / WDAC 状态

- Smart App Control：`VerifiedAndReputablePolicyState = 0`（Off）
- Supplemental policy XML：已清理（裸 XML 没转 CIP，无效果）
- Trusted Publishers：`CN=7zThinking Root CA` 已在 LocalMachine 存储
- 待办：SignPath EV 证书到手后，恢复 SAC 为 Eval/On，EV 签名天然满足 Enterprise signing level

---

## 故障排查树（按优先级匹配）

发布或运行出问题时，按此顺序定位，避免浪费时间在无关方向上。

### 第 1 步：先看 Event Viewer（Application 日志）

```powershell
Get-WinEvent -LogName Application -MaxEvents 10 -ErrorAction SilentlyContinue |
  Where-Object { $_.Message -match "KBEA|dotnet|0xe043" } |
  Select-Object TimeCreated, ProviderName, Id, Message | Format-List
```

### 第 2 步：按错误码分类

#### A. 启动即崩溃 / 0xe0434352（最常见）

```
System.Windows.Markup.XamlParseException / InvalidOperationException
"Button" TargetType 与元素"ToggleButton"的类型不匹配
```

**根因**：某个 WPF Style 的 TargetType 和实际应用它的元素类型不兼容。WPF 要求 `Style.TargetType` 与元素类型完全匹配（ToggleButton 不是 Button 的子类，不能用 Button 的 Style）。

**排查命令**：
```powershell
# 找所有 TargetType=Button 的 Style
Select-String -Path src/**/*.xaml -Pattern 'TargetType="Button"'
# 找所有 ToggleButton 元素
Select-String -Path src/**/*.xaml -Pattern '<ToggleButton'
# 重点看：ToggleButton 有没有用 IconButton / ToolbarButton 这类 Button 专属 Style
```

**修复**：为 ToggleButton 创建同属性的 Style（TargetType=ToggleButton），或移除 BasedOn 的 ControlTemplate 依赖。

**⚠️ 陷阱**：XAML TargetType bug 以前被 SAC 拦在更前面（还没跑到 XAML 解析就被 WDAC 杀了）。SAC Off + self-contained 后才暴露到这层。**所以 SAC/WDAC 修好后出现新的运行时崩溃不一定是你引入了新 bug，可能是之前被拦着没跑到的代码。**

#### B. 启动即崩溃 / 0x800711C7

**根因**：WDAC/SAC 拦截未签名或签名级别不够的 dll。

**验证**：
```powershell
# 查 SAC 状态
(Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy").VerifiedAndReputablePolicyState
# 0=Off  1=Eval  2=On
```

**解法**：
- SAC=0（Off）：不拦，直接用
- SAC=1/2：需要 Supplemental policy 或 EV 签名
  - Supplemental policy 裸 XML 没用，必须编译成 CIP（需要 ADK PolicyEditor）
  - **推荐**：SignPath EV 证书到手后直接签，天然满足 Enterprise signing level

#### C. `hostfxr.dll not found` / `0x80008083`

**根因**：framework-dependent 产物 + reboot 后 DOTNET_ROOT 不是系统级 env var。

**解法**：重跑 publish.ps1，确保两个版本都 `--self-contained true`。旧的 framework-dependent 产物直接删掉。

#### D. `此应用一部分被阻止` / SmartScreen 提示

**根因**：SmartScreen 信誉检查，与 Authenticode 签名是两套系统。自建 CA 签的在微软云端无信誉分。

**解法**：SignPath EV 证书天然带信誉分。过渡期间每次启动点「更多信息 → 仍要运行」。

#### E. `you must install .NET to run this application`

**根因**：跑了旧的 framework-dependent KBEA.exe。

**解法**：检查文件名——是不是用了 Light 版（应该是 self-contained）或旧的 dev bin 输出？用 `publish.ps1` 重新生成。

#### F. signtool 锁文件 / File not found

**根因**：KBEA 还在运行，或相对路径不对。

```powershell
Stop-Process -Name KBEA -Force -ErrorAction SilentlyContinue
cd C:\Users\qizic\Desktop\Projects\rsiKB
.\sign.ps1
```

#### G. `signtool Error: The time stamp server could not be reached`

**根因**：时间戳服务器超时。

**解法**：sign.ps1 有 4 个备选（DigiCert / Sectigo / GlobalSign / Starfield），会自动重试换。

#### H. PFX 密码忘了

证书在 CertStore 里还有（CurrentUser\My），直接用指纹签就行；PFX 只是离线备份。

---

## 2026-11-04 完整踩坑记录（按时间线）

### 问题 1：WDAC / SAC 拦截（0x800711C7）
- **现象**：Event Viewer CodeIntegrity 日志报 `did not meet the Enterprise signing level requirements`
- **根因**：SAC Eval 模式拦自建 CA 签的 Standard 级别 dll
- **尝试方案 1**（Supplemental policy）：
  ```powershell
  New-CIPolicy -ScanPath artifacts -Level SignedAndReputable -FilePath base.xml
  Add-SignerRule -FilePath base.xml -CertificatePath 7zThinking-Root-CA.cer -User
  Set-RuleOption -FilePath base.xml -Option 3 -Delete  # 关掉 Audit Mode
  Move-Item base.xml C:\Windows\System32\CodeIntegrity\CIPolicies\Active\{GUID}.xml
  ```
  ❌ 裸 XML 没被编译成 CIP（ConfigCI 缺 ConvertTo-CIPolicy，ADK 只装了最小版，PolicyEditor 组件没装）
- **最终方案 2**：SAC 关
  ```powershell
  Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy" -Name VerifiedAndReputablePolicyState -Value 0 -Type DWord
  # 然后 reboot
  ```
- **Supplemental policy XML（{72C0536B-...}.xml）**：已清理（没被加载过，无效果）

### 问题 2：hostfxr.dll not found（0x80008083）
- **现象**：KBEA.exe 启动报错 `.NET location: Not found`，`Failed to resolve hostfxr.dll`
- **根因**：旧 Light 版是 framework-dependent（没加 `--self-contained true`），reboot 后 DOTNET_ROOT 不是系统级 env var，KBEA.exe 找不到 .NET runtime
- **附加问题**：run-light.bat 用 PowerShell -Command 嵌套绕 SAC，在子进程里 DOTNET_ROOT 丢了
- **修复**：
  - publish.ps1 加 `--self-contained true`（Light 和 Portable 都加）
  - run-light.bat 去掉 PowerShell 嵌套，用 bat 原生 `start "" "..."`

### 问题 3：XAML TargetType 不匹配（0xe0434352）⭐ 代码 Bug
- **现象**：
  ```
  System.Windows.Markup.XamlParseException: 设置属性"FrameworkElement.Style"时引发异常。
  ---> System.InvalidOperationException: "Button" TargetType 与元素"ToggleButton"的类型不匹配。
  ```
- **根因**：`MainWindow.xaml:308` 的 `ToolbarOverflowButton`（ToggleButton）用了 `IconButton` Style（`App.xaml:84`，TargetType=Button，BasedOn 有 Button ControlTemplate）。WPF 要求 Style.TargetType 与元素类型完全匹配。
- **陷阱**：这个 bug 在 SAC 拦着的时候根本跑不到（还没解析 XAML 就被 WDAC 杀了）。SAC Off + self-contained 后第一次跑到 XAML 解析层才暴露。
- **修复**：
  - `App.xaml:90-100` 新增 `IconToggleButton` Style（TargetType=ToggleButton，属性与 IconButton 对齐）
  - `MainWindow.xaml:308` 把 `Style="{StaticResource IconButton}"` → `Style="{StaticResource IconToggleButton}"`

### 为什么三个问题同时出现
- SAC/WDAC 拦截（外部策略层）
  → 导致 run-light.bat 用了绕 SAC 的复杂启动链路（PowerShell -Command 嵌套）
  → SAC Off 后这条链路变得多余且在 reboot 后丢环境变量
  → 直接导致 hostfxr.dll 找不到（framework-dependent 版本的问题暴露）
- framework-dependent（发布配置层）
  → self-contained 修好后终于能启动到 WPF XAML 解析
  → 暴露了原来被 SAC 挡着没跑到的 XAML TargetType bug
- XAML TargetType（代码层）
  → 最终修复 KBEA 能正常打开

**教训**：SAC/WDAC 拦截会隐藏更深层的 Bug。每次修完一层，必须重新跑完整发布→签名→启动链路，每一层都可能有新东西暴露。

---

## 重发布全流程

```powershell
# 1. 清理旧产物（可选，publish 会覆盖）
Remove-Item -Recurse -Force artifacts/KBEA-Portable, artifacts/KBEA-Light -ErrorAction SilentlyContinue

# 2. 重新发布（必须两个都 self-contained）
.\publish.ps1

# 3. 确保 KBEA 没在跑（签名会锁文件）
Stop-Process -Name KBEA -Force -ErrorAction SilentlyContinue

# 4. 签名 + 验证（480+ 文件，3-5 分钟）
.\sign.ps1

# 5. 测试启动
.\run-light.bat
# 或直接双击 artifacts\KBEA-Light\KBEA.exe
```

## SignPath EV 证书到位后

1. 把 sign.ps1 里的 SHA1 `1583576BBF01ADB342EDC1CB9BCA018E7FC71381` 换成 SignPath 新证书的指纹
2. SAC 恢复为 Eval（`VerifiedAndReputablePolicyState=1`）或 On（=2）
3. EV 签名天然满足 SAC Enterprise signing level + SmartScreen 信誉分
4. 自建根从 LocalMachine\TrustedPublisher 清理（可选，过渡期双签也可以）
