# rsiKB <版本号>

知识库自进化助手（Knowledge Base Self-Evolving Assistant）开源版。

## 下载

- `KBEA-<版本号>-win-x64.zip`：Windows 10/11 x64 绿色版，无需安装 .NET。
- `KBEA-<版本号>-win-x64.zip.sha256`：ZIP 的 SHA-256 校验值。

## 本版内容

- 工作空间与多物理文件夹挂接。
- 文件浏览、文本/图片预览和文件名索引。
- 多级分类、跨文件夹编目和快照。
- Git 拉取、提交推送与历史。
- rsync 关键节点归档配置。
- 文件变更提示、亮/暗主题和插件入口壳。

## 构建与验证

- Windows x64 Release 构建。
- 自动化测试通过。
- 绿色 ZIP 与 SHA-256 校验文件由 GitHub Actions 生成。

## Code signing

This project uses SignPath Foundation for Windows code signing.

> 签名接入完成前，开发构建仍为未签名状态；不要在下载页把未签名包描述为已签名。

## 当前边界

rsync 二进制未内置；完整冲突取舍界面、自动同步队列和 Windows 凭据管理器适配仍在后续路线中。
