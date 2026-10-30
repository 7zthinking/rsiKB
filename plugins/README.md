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

v0.1.0 仅发现清单，不执行第三方程序集。核心能力接口位于 `KBEA.Core.Plugins`。后续加载器将使用独立 `AssemblyLoadContext`，并提供启用/禁用和白名单管理。
