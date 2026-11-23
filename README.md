<div align="center">

# 知识库自进化助手（KBEA）

**机器即节点，目录即空间，成员即网络**

本地优先的知识库桌面应用 —— 文件留在你自己的磁盘上，团队之间点对点直连，AI 工具可直接检索你的本地资料。

[![Build, test and package](https://github.com/7zthinking/rsiKB/actions/workflows/build.yml/badge.svg)](https://github.com/7zthinking/rsiKB/actions/workflows/build.yml)
[![License: Apache-2.0](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](LICENSE)

[下载](https://github.com/7zthinking/rsiKB/releases) · [插件开发](docs/plugin-development-guide.md) · [签名说明](docs/SIGNPATH.md) · [问题反馈](https://github.com/7zthinking/rsiKB/issues)

</div>

---

## 这是什么

KBEA 不是"又一个网盘客户端"，而是一套**把知识库的存储、组织与演化交还给每台机器自身**的桌面优先架构。

没有中心服务器，没有云端账号，没有按容量计费。你的业务文件始终在你自己的目录里，KBEA 只在旁边的 `.7z/` 目录里维护索引与元数据。

- **分布式（Distributed）**：每台机器既是客户端也是服务端（mDNS 发现 + Kestrel 端点），成员之间点对点直连；断网、离机、owner 不在线，都不影响本地数据的完整可用。
- **自管理（Self-managed）**：数据归属清晰 —— 业务文件在用户自己的目录里，Git 仓库、元数据库（`.7z/meta.db`）、索引都在本机，凭据走 Windows 凭据管理器；卸载程序不留下任何"云端孤儿"。
- **自组织（Self-organizing）**：团队关系靠**邀请码 + 成员签名**自举建立，成员表在每台机器持有签名副本；共享空间按 search/read/write 三级权限自声明，加入即可被全网发现并按级别访问。
- **自进化（Self-evolving）**：文件变更自动防抖感知、后台知识索引持续增量构建、关键节点 rsync 留档 —— 知识库在不增加人工负担的前提下随使用不断增殖价值。

## 核心特性

| 模块 | 能力 |
|---|---|
| **工作空间** | 新建空间、挂接/解挂物理文件夹、离线标记、JSON 持久化 |
| **文件浏览** | 多 Tab、目录树、详细列表（多选/全选/批量复制剪切删除）、地址栏、前进/后退/上级、系统打开 |
| **团队共享** | mDNS 自动发现、KBEA-XXXX-XXXX 短码邀请、成员签名认证、三级权限（search/read/write）、按空间共享、成员表签名副本同步 |
| **WebDAV** | 任意程序经 UNC（`\\ip@端口\空间名`）访问共享空间；Basic 认证（成员签名令牌 24h 自动续期，cmdkey 自动写凭据） |
| **传输安全** | App 内通道 HTTPS（每机身份自签证书 + 成员表公钥钉扎防中间人）、文件 Range 断点续传 |
| **预览与索引** | 文本/图片预览、SQLite 文件名 FTS 索引、名称/路径/扩展名搜索、后台知识索引增量构建 |
| **资料编目** | 多级分类、一文件挂多分类、跨挂载文件夹汇总、SHA-256 指纹、快照导出 |
| **Git 集成** | 初始化、main 默认分支、远程绑定、状态角标、提交并推送、拉取、历史记录、HEAD 基线 vs 工作区双屏文本比对（DiffPlex）、冲突标记解决 |
| **自动检测** | FileSystemWatcher + 1.5 秒防抖，默认只提示、不自动上传 |
| **rsync 留档** | 由 `kbea-plugin-rsync` 插件提供：关键节点归档、进度输出、超时保护、独立留档历史库 |
| **界面** | 亮/暗主题、可收缩侧栏（Ctrl+B）、可折叠预览、状态栏、统一 WPF 界面规范 |
| **插件体系** | 独立的插件进程隔离加载（ALC），工具栏 / 设置页 / 详情字段 / 预览 / 元数据抽取五类扩展点，**详见 [插件开发指南](docs/plugin-development-guide.md)** |

## 快速开始

### 绿色版（推荐）

1. 从 [GitHub Releases](https://github.com/7zthinking/rsiKB/releases) 下载对应版本的 Windows x64 ZIP。
2. 解压后双击 `KBEA.exe`。程序已包含 .NET 8 运行库和 LibGit2Sharp 原生组件，无需另装运行时。
3. 点击**挂接文件夹**选择真实资料目录 —— KBEA 会在旁边创建 `.7z/` 存放索引，不改动你的业务文件。

### 源码开发

```powershell
# 需要 .NET 8 SDK
dotnet restore KBEA.sln
dotnet build KBEA.sln -c Release
dotnet test tests/KBEA.Tests/KBEA.Tests.csproj -c Release
```

Windows 用户也可直接运行仓库内的 `run-source.bat`、`build.bat`、`test.bat`；Git Bash 用户可用 `bash eng/dotnet.sh build KBEA.sln`。

## 使用流程

1. **新建空间**：点击"＋ 空间"，输入名称。
2. **挂接文件夹**：选中空间，点击"挂接文件夹"。程序会创建 `.7z/` 元数据并建立文件名索引。
3. **编目**：先选中分类，再在文件列表选中文件，点击"编入分类"。点击分类即可查看跨文件夹汇总。
4. **Git**：点击"Git 绑定"填写远程地址；之后使用"拉取更新""保存并上传""历史"。
5. **团队共享**：生成邀请码，在另一台机器上输入短码加入；用**局域网诊断**面板可查看 mDNS 发现链路与端口连通性。
6. **关键节点**：安装并启用 `kbea-plugin-rsync` 插件后，在插件设置页填写 `rsync.exe` 与归档根目录，再点击工具栏「归档 › 关键节点」。
7. **解挂**：只移除组织关系，不删除业务文件、`.git/` 或 `.7z/`。

可用 `--config` 指定独立配置文件，便于测试或便携运行：

```powershell
KBEA.exe --config "D:\KBEA-Config\workspaces.json"
```

## 数据都在哪

| 内容 | 位置 |
|---|---|
| 工作空间、挂载关系、署名与设置 | `%AppData%\KBEA\workspaces.json` |
| 编目挂接、文件索引、同步日志 | 各挂载文件夹的 `.7z/meta.db` |
| 关键节点留档历史（rsync 插件） | 各挂载文件夹的 `.7z/archive.db` |
| 编目快照 | 各挂载文件夹的 `.7z/catalog/` |

`.7z/` 会自动加入 `.gitignore`；默认数据类扩展名也会加入忽略清单。**卸载 KBEA 不会删除你的业务文件。**

## 编写插件

KBEA 的插件是独立的 .NET 程序集，由宿主通过独立的 `AssemblyLoadContext` 加载 —— 插件崩溃不会带崩宿主，插件缺依赖也不会影响主程序。

```csharp
public sealed class MyPlugin : IPluginEntryPoint, IToolbarContribution
{
    private IPluginHostContext? _context;

    public string DisplayName => "我的插件";

    public void Init(IPluginHostContext context) => _context = context;

    public void Shutdown() => _context = null;

    public IReadOnlyList<ToolbarGroupVm> GetToolbarGroups() =>
    [
        new ToolbarGroupVm
        {
            Title = "我的插件",
            Buttons =
            [
                new ToolbarButtonVm
                {
                    Icon = ToolbarIcon.Star,          // 只能用约定枚举，宿主统一渲染线性矢量图标
                    Label = "收藏",                     // 2-4 字短文字
                    Tooltip = "收藏当前文件到我的插件清单",
                    Command = new RelayCommand(() => _context?.Ui.SetGlobalStatus("已收藏"))
                }
            ]
        }
    ];
}
```

五个扩展点：**工具栏分组**、**设置页**、**详情字段**、**预览接手**、**元数据抽取**；外加**服务替换**模式（把宿主的某类操作整体接管，如 Git）。

📖 完整指南：[docs/plugin-development-guide.md](docs/plugin-development-guide.md)

## 工程结构

```text
src/KBEA.Core/          核心模型、存储、Git、监听、差异与插件契约（无 UI 依赖）
src/KBEA.App/           WPF、MVVM、主题、对话框与主界面
src/KBEA.Plugin.Git/    Git 插件（服务替换 + 工具栏范例）
src/KBEA.Plugin.Wiki/   WIKI 插件
src/KBEA.Plugin.Rsync/  rsync 归档插件（自治数据库 + 自有配置 + 服务替换）
src/KBEA.Plugin.Sample/ 样例插件（覆盖全部扩展点）
src/KBEA.Worker/        后台元数据服务进程
src/KBEA.Diag/          存储层诊断工具
src/KBEA.Smoke/         UI 冒烟验证（UIA）
tests/KBEA.Tests/       xUnit 核心与集成测试
eng/                    构建、验证与证书导出脚本
docs/                   插件开发指南、签名说明
```

## 参与贡献

欢迎提交 Issue 和 PR：

- **Bug 报告**：请附上 KBEA 版本、Windows 版本、复现步骤与诊断日志（`%AppData%\KBEA\` 下）
- **功能建议**：描述你想解决的真实场景
- **插件**：如果做了插件，欢迎在 Issue 里分享；主仓库内置 Git / WIKI / Rsync / Sample 四个参考实现
- **代码风格**：跟随现有代码风格，保持 Core 层无 UI 依赖

提交 PR 前请确保本地 `dotnet test` 全绿。

## 代码签名

This project uses SignPath Foundation for Windows code signing.

签名服务处于申请与接入阶段；接入完成前，开发构建仍为未签名文件。如果企业设备的 Windows 应用控制或 Smart App Control 阻止新程序，请按组织流程审核放行，不要关闭安全功能。详见 [docs/SIGNPATH.md](docs/SIGNPATH.md)。

**自建签名链（过渡方案）**：当前构建由项目自建根 CA 签名。绿色版 ZIP 的 `certs\` 目录内含根证书公钥 `KBEA-Root-CA.cer` 与一键导入脚本 `import-signing-cert.bat`（仅导入公钥，不含私钥）。受信后 UAC/企业应用控制中发布者显示为已验证。注意：导入证书不会消除 SmartScreen 的"未知发布者"下载信誉提示；面向公众分发仍以接入 SignPath（公共信任链）为目标。

> 自签名流程：`eng/export-sign-root.ps1` 导出根证书公钥（永不导出私钥），`sign.ps1` 签名。证书指纹通过环境变量 `KBEA_SIGN_THUMBPRINT` 传入，仓库中不含任何证书信息。

## rsync 配置

rsync 留档已移出核心，成为独立插件 `kbea-plugin-rsync`（源码在 `src/KBEA.Plugin.Rsync/`）。插件不在核心仓库携带第三方 rsync 二进制：请将可信的 Windows 便携版 rsync 放在 `tools/rsync/rsync.exe`，或在插件设置页指定其绝对路径。默认调用参数：

```text
-avz --partial --backup-dir=<归档根>/<项目>/<节点时间>
```

并排除 `.git/`、`.7z/index/` 和临时文件。

插件的留档历史存放在各挂载文件夹的 `.7z/archive.db`（插件自治数据库，不进宿主的 `meta.db`）；插件配置存放在 `%AppData%\KBEA\plugins\kbea-plugin-rsync\settings.json`。

## 当前边界

诚实地说，以下还没做：

- 插件暂不支持运行时热重载（禁用/启用需重启），也没有插件权限/信任模型 —— 安装插件等同于运行其代码
- 私有远程仓库的 Windows 凭据管理器适配尚未完成；本地仓库及无需额外凭据的远程已验证
- 完整双屏取舍窗口尚未接入（DiffPlex 服务与冲突读取已具备）
- rsync 需要用户提供可信便携二进制，未对实际 rsync 服务器做集成测试
- 仅支持 Windows（.NET 8 + WPF）

## 许可证

Apache License 2.0 —— 详见 [LICENSE](LICENSE) 与 [NOTICE](NOTICE)。

第三方组件声明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
