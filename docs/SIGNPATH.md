# SignPath Foundation 接入说明

## 当前状态

rsiKB 是 Apache-2.0 开源项目，Windows 绿色包由 `.github/workflows/build.yml` 自动构建、测试和打包。

This project uses SignPath Foundation for Windows code signing.

签名服务处于申请与接入阶段。申请获批前，开发构建仍是未签名文件，发布说明必须明确标注。

## 申请前检查

1. GitHub Actions 的 `Build, test and package` 工作流已在 `main` 分支成功运行。
2. 已创建对应版本 Release，并包含绿色 ZIP、SHA-256 和 SignPath 声明。
3. SignPath 申请表的 Build System 填写 `GitHub Actions`。
4. Download URL 填写 `https://github.com/7zthinking/rsiKB/releases`。
5. Reputation 只填写真实可验证的用户、下载、报道或社区材料。

## 获批后的接入原则

- SignPath 凭据只存入 GitHub Actions 加密 Secrets，不写入仓库。
- 签名步骤应在编译、测试之后，生成 Release 包之前。
- 只签名受控构建产物，不上传未签名文件冒充签名版本。
- 发布页保留版本、提交、构建运行记录和 SHA-256，便于追溯。
- 日常 CI 使用 GitHub 自动提供的 `GITHUB_TOKEN`，无需把个人 PAT 存为仓库 Secret。

具体 SignPath Action、项目标识和签名策略以获批后 SignPath 官方配置为准。
