知识库自进化助手（rsiKB / KBEA）开源核心

运行：解压后双击 KBEA.exe，无需安装 .NET。
入口：新建工作空间 → 挂接文件夹 → 编目 / Git / 关键节点。
设置：右上角齿轮，可配置 Git 署名、rsync 路径、归档目录和主题。
rsync：未内置第三方二进制，请在设置中指定可信便携版 rsync.exe。
指定独立配置：KBEA.exe --config "D:\Config\workspaces.json"

Code signing:
This project uses SignPath Foundation for Windows code signing.
签名接入完成前，开发构建仍为未签名状态。如果企业设备的应用控制策略阻止运行，请按组织流程审核，不要关闭 Windows 安全功能。
