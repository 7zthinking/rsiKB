# 知识库自进化助手（KBEA）开源版

基于设计文档 v1.1 实现的 Windows 桌面工程。当前版本为 **v0.1.0 基础可运行版**，开源核心不依赖闭源组件。

## 快速运行

### 绿色版

1. 打开 `artifacts/KBEA-Portable/`。
2. 双击 `KBEA.exe`。程序已包含 .NET 8 运行库和 LibGit2Sharp 原生组件，无需另装运行时。
3. 点击“挂接文件夹”选择真实资料目录。

> 发布程序当前未做商业代码签名。如果企业设备的 Windows 应用控制或 Smart App Control 阻止新程序，请按组织流程签名或审核放行，不要关闭安全功能。

### 源码开发

- 直接使用系统 .NET 8 SDK；或使用项目内 `.dotnet/dotnet.exe`。
- Windows 可运行 `run-source.bat`、`build.bat`、`test.bat`。
- Git Bash 可运行 `bash eng/dotnet.sh build KBEA.sln`。

## 已实现能力

| 模块 | 当前可用功能 |
|---|---|
| 工作空间 | 新建空间、挂接/解挂物理文件夹、离线标记、JSON 持久化 |
| 文件浏览 | 多 Tab、目录树、详细列表、地址栏、前进/后退/上级、系统打开 |
| 预览与索引 | 文本/图片预览、SQLite 文件名 FTS 索引、名称/路径/扩展名搜索 |
| 资料编目 | 多级分类、一文件挂多分类、跨挂载文件夹汇总、SHA-256 指纹、快照导出 |
| 本地编目 | 当前目录收藏、从收藏快速定位 |
| Git | 初始化、main 默认分支、远程绑定、状态角标、提交并推送、拉取、历史记录 |
| 自动检测 | FileSystemWatcher + 1.5 秒防抖，默认只提示、不自动上传 |
| rsync | 便携程序路径配置、关键节点参数、进度输出、超时保护、历史留档记录 |
| 界面 | 亮/暗主题、可收缩侧栏（Ctrl+B）、可折叠预览、状态栏 |
| 扩展壳 | 插件清单发现、能力接口、信息卡与可拖动浮动助手壳 |

## 基本使用流程

1. **新建空间**：点击“＋ 空间”，输入名称。
2. **挂接文件夹**：选中空间，点击“挂接文件夹”。程序会创建 `.7z/` 元数据并建立文件名索引。
3. **编目**：先选中分类，再在文件列表选中文件，点击“编入分类”。点击分类即可查看跨文件夹汇总。
4. **Git**：点击“Git 绑定”填写远程地址；之后使用“拉取更新”“保存并上传”“历史”。
5. **关键节点**：在“设置”中填写 `rsync.exe` 和归档根目录，再点击“关键节点”。
6. **解挂**：只移除组织关系，不删除业务文件、`.git/` 或 `.7z/`。

可用 `--config` 指定独立配置文件，便于测试或便携运行：

```powershell
KBEA.exe --config "D:\KBEA-Config\workspaces.json"
```

## 数据位置

- 工作空间、挂载关系、署名与设置：默认 `%AppData%\KBEA\workspaces.json`。
- 编目挂接、文件索引、同步日志、关键节点：各挂载文件夹的 `.7z/meta.db`。
- 编目快照：各挂载文件夹的 `.7z/catalog/`。
- `.7z/` 会自动加入 `.gitignore`；默认数据类扩展名也会加入忽略清单。

## rsync 配置

绿色核心不携带第三方 rsync 二进制。请将可信的 Windows 便携版 rsync 放在 `tools/rsync/rsync.exe`，或在设置中选择其绝对路径。默认调用参数：

```text
-avz --partial --backup-dir=<归档根>/<项目>/<节点时间>
```

并排除 `.git/`、`.7z/index/` 和临时文件。

## 构建与验证

```powershell
dotnet restore KBEA.sln
dotnet build KBEA.sln -c Release
dotnet test tests/KBEA.Tests/KBEA.Tests.csproj -c Release
powershell -ExecutionPolicy Bypass -File publish.ps1
```

测试覆盖工作空间配置、损坏配置恢复、SQLite 多分类编目、FTS 搜索、快照、归档记录、文件浏览、DiffPlex、插件清单，以及真实 Git 初始化、提交、推送和拉取。

## 工程结构

```text
src/KBEA.Core/       核心模型、存储、Git、归档、监听、差异与插件接口
src/KBEA.App/        WPF、MVVM、主题、对话框与主界面
tests/KBEA.Tests/    xUnit 核心与集成测试
eng/                 本地 SDK 调用和启动验证脚本
plugins/             第三方插件清单位置
artifacts/           绿色发布与验证输出
```

## 当前边界与后续迭代

- 当前是“基础可运行版”，不是设计文档全部里程碑的完成版。
- 自动同步退避队列、15 分钟兜底扫描、目录锁尚未接入。
- DiffPlex 服务和冲突读取已具备，完整双屏取舍窗口尚未接入。
- 私有远程仓库的 Windows 凭据管理器适配尚未完成；本地仓库及无需额外凭据的远程已验证。
- 插件当前仅做清单发现与接口预留，热加载隔离将在后续接入。
- rsync 需要用户提供可信便携二进制；未对实际 rsync 服务器做集成测试。

## 许可证

Apache License 2.0。设计文档中的闭源扩展不在本工程内。
