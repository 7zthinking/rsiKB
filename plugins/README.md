# 插件目录

每个插件使用独立子目录，并提供 `plugin.json`：

```json
{
  "id": "example.assistant",
  "name": "示例助手",
  "version": "1.0.0",
  "entryAssembly": "Example.Assistant.dll",
  "capabilities": ["assistant"],
  "enabled": true
}
```

| 字段 | 说明 |
|---|---|
| `id` | 全局唯一，建议 `kbea-plugin-xxx` 格式 |
| `name` | 设置页与日志显示名 |
| `version` | 插件自身版本（semver） |
| `entryAssembly` | 入口 DLL 文件名（相对插件目录） |
| `capabilities` | 能力声明（toolbar / settings / fields / preview / metadata / git / wiki / archive） |
| `minHostVersion` | 兼容的宿主最低版本，低于则跳过加载并提示原因 |
| `enabled` | 启用开关，用户可在设置页切换（写回本文件） |
| `enabledByUser` | 用户是否显式设置过（宿主自动维护，手写可省略） |
| `defaultEnabled` | 插件自带的默认启用值（宿主自动维护，手写可省略） |

## 启用状态的优先级

1. **用户显式设置**（`enabledByUser: true`）—— 用户在「设置 → 插件管理」里的选择
2. **发行方策略** —— 程序目录下的 `plugins.defaults.json`：

```json
{
  "enabled":  ["kbea-plugin-git", "kbea-plugin-wiki"],
  "disabled": ["kbea-plugin-sample"]
}
```

3. **插件自带默认** —— 上面的 `enabled` 字段

`disabled` 优先于 `enabled`；未在策略中出现的插件沿用各自 `plugin.json` 的 `enabled`。
发行方策略只决定「开箱默认」，用户在设置页的显式选择永远优先 —— 点「恢复默认」可清除用户的选择，回到策略值。

## 加载机制

插件由宿主通过独立 `AssemblyLoadContext` 加载（ALC 隔离），单个插件失败不影响主程序。
插件的第三方依赖从插件目录解析（`KBEA.Core` 由宿主提供，不重复加载）。完整说明见 [docs/plugin-development-guide.md](../docs/plugin-development-guide.md)。
