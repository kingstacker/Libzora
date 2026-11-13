# Libzora 插件 SDK

| 文件 | 用途 |
|---|---|
| `libzora.d.ts` | `window.libzora`（接口版本 1）的 TypeScript 类型，复制到插件工程或用 `/// <reference path>` 引用 |
| `libzora-mock.js` | 浏览器里的模拟宿主：在页面脚本之前引入，即可脱离应用调试页面和词典/翻译/后台脚本 |

命令行工具在 `tools/Libzora.PluginTool`（`dotnet run --project tools/Libzora.PluginTool -- help`）。完整文档见 `docs/plugins.md`。

修改桥接接口（`PluginPageComposer` 的模板或 `PluginBridge.Methods`）时，同步更新这两个文件。
