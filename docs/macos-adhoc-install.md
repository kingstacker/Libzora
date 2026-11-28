# 安装 macOS ad-hoc 版本

这个 Libzora 版本使用 ad-hoc 签名，尚未经过 Apple 公证。虽然应用签名自身有效，
但 macOS 通常仍会阻止从网络下载的应用直接启动。

## 安装步骤

1. 打开下载的 `Libzora-<版本>-osx-<架构>.dmg`，窗口中只有一个居中的 Libzora 安装图标。
2. 按住 Control 点击该图标，选择“打开”，在弹窗中再次确认“打开”；安装器会自动将 `Libzora.app` 安装到“应用程序”并启动。
3. 若安装时提示“无法打开”或仍被阻止，请在“系统设置 > 隐私与安全性”中找到对应提示并选择“仍要打开”。

如果以上方法仍不可用，可在已打开 DMG 后或在安装完成后通过终端移除隔离标记：

```sh
xattr -dr com.apple.quarantine "/Volumes/Libzora/ .app"
open "/Volumes/Libzora/ .app"
```

只应对从本项目官方 GitHub Release 页面下载的 Libzora 使用该命令。后续采用
Developer ID 签名并经过 Apple 公证的版本将不再需要这些手动步骤。

卸载应用并清理书库、设置和 Keychain 密钥：见
[macos-uninstall.md](macos-uninstall.md)。
