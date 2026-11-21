# 桌面小组件

Libzora 的 iOS 与 macOS 版本共用同一组 WidgetKit 小组件。小组件不直接读取
数据库，而是由主应用把书库、阅读统计和最近摘录写成一份只读快照，扩展进程从
App Group 容器中读取。这样小组件不依赖 C# 运行时，也不会在扩展启动时打开
SQLite。

## 小组件

| 小组件 | 内容 | 尺寸 |
| --- | --- | --- |
| 继续阅读 | 小号显示一本书；中号横向排列最近在读的四本；两者都含封面、书名和进度 | 小号、中号；iOS 另支持锁屏矩形/内联 |
| 阅读数据 | 今日与累计时长、周趋势、读完/书签/划线数量 | 中号、大号 |
| 阅读之树 | 随累计阅读时长从书种长成古木，读完的书化作果实，批注化作点缀花芽 | 小号、中号 |

小组件扩展的最低系统版本是 iOS 17 / macOS 14；主应用仍可在 iOS 15 或
macOS 12 上运行，旧系统只是不显示小组件。

点按「继续阅读」中的某一本书会通过 `libzora://read/<bookId>` 打开对应
书籍；中号小组件的每一项都是独立的链接，打开的是被点的那本，而不是固定的
上次阅读书籍。点按统计类小组件会通过 `libzora://dashboard` 切到「阅读数据」标签页。
标签类链接会同时取消上次阅读会话的自动恢复、取消正在打开的阅读器并关闭
已显示的阅读页，避免上次的正文页盖住目标标签。
iOS 的 URL scheme 在主应用 `Info.plist` 注册，`AppDelegate` 与
`IOSNativeSceneDelegate` 都会把链接交给 `IOSNativeFrontend.HandleUrl`。
macOS 的小组件点击只负责重新打开应用，暂不路由到具体书籍。

## 样式

小组件采用黑白纸张配色，跟随系统的浅色／深色外观，封面保留原色。
「继续阅读」按尺寸调整封面与书名比例，底部集中显示百分比和细进度条；
缺少封面时，用书名生成素色占位封面。「阅读数据」突出今日分钟数，趋势图
使用细柱和浅色参考线，最近一天用墨色强调；大号另外展示本周与累计时长，
底部显示读完、书签和划线数量。iOS 主应用的炫彩开关不影响小组件配色。

全部文字统一使用主应用同款「京华老宋」，包括书名、标题、数字、百分比、
辅助文字和锁屏文字。
字体文件和许可记录由两个小组件构建脚本单独复制到扩展的 `Fonts/` 目录；
iOS 通过 `UIAppFonts` 声明，视图入口同时按进程注册字体供 macOS 使用。
若字体加载失败，文字回退到系统衬线字体。

## 共享数据

- App Group：`group.com.kingstacker.libzora`
- 子目录：`LibzoraWidgets`
- 快照：`LibzoraWidgets/snapshot.json`
- 封面：`LibzoraWidgets/covers/<bookId>.jpg`（最大边 420 px，JPEG 质量 84）

快照由 `src/Libzora.Infrastructure/WidgetSnapshotPublisher.cs` 生成：

```json
{
  "version": 1,
  "generatedAt": "2026-11-12T04:53:50.0000000+00:00",
  "colorfulMode": true,
  "themeMode": 0,
  "continueReading": {
    "id": "…", "title": "…", "authors": "…",
    "progress": 68.2, "cover": "covers/….jpg", "updatedAt": "…"
  },
  "continueBooks": [ "…最近在读的 4 本，按阅读时间倒序…" ],
  "books": [ "…最近 9 本…" ],
  "reading": {
    "todaySeconds": 334, "streakDays": 3, "weekSeconds": 3822,
    "totalSeconds": 3822, "booksStarted": 11, "booksFinished": 0,
    "bookmarkCount": 1, "annotationCount": 3,
    "days": [{ "date": "2026-11-12", "seconds": 334 }]
  },
  "quote": {
    "text": "…", "note": null, "bookTitle": "…", "bookId": "…",
    "kind": "highlight", "updatedAt": "…"
  },
  "libraryCount": 34,
  "favoriteCount": 0
}
```

`WidgetSnapshotCoordinator` 负责 700 ms 防抖、串行化发布并在成功后通知系统
刷新。iOS 在书库加载、偏好保存、阅读器关闭、应用退到后台以及书库/阅读数据
变更时调度快照；macOS 在初始书库加载与 `HandleLocalDataChanged` 变化时调度。

## iOS 构建与签名

`src/Libzora.Mobile.iOS/Libzora.Mobile.iOS.csproj` 会调用
`src/Libzora.Widgets/build-ios-widgets.sh` 生成 `LibzoraWidgets.appex`，再通过
.NET iOS SDK 的 `AdditionalAppExtensions` 复制到 `PlugIns/` 并签名。

两个构建脚本都通过 `-Xlinker -e -Xlinker _NSExtensionMain` 把扩展入口设为
Foundation 的 `NSExtensionMain`，这与 Xcode 扩展 target 的 `LD_ENTRY_POINT`
一致。不能省略：若以 Swift `@main` 生成的 `main` 为入口，扩展会在解析
`WidgetBundle` 后直接返回，chronod 只能看到
`getAllDescriptors ... connection invalidated`，小组件库因此为空。

主应用与扩展都需要 App Group entitlement：

- 主应用：`src/Libzora.Mobile.iOS/Entitlements.plist`
- 扩展：`src/Libzora.Widgets/LibzoraWidgets.iOS.entitlements`

真机构建还要分别传入主应用和扩展的描述文件 UUID／路径。主应用使用
`-p:CodesignProvision=<主应用 profile UUID>`，扩展使用
`-p:WidgetProvisioningProfile=<扩展 profile 路径>`。后者会在签名前复制为
`PlugIns/LibzoraWidgets.appex/embedded.mobileprovision`；缺少它时应用本身
可以安装，但系统无法启动小组件扩展。

模拟器构建可以直接使用，因为 entitlements 会嵌入主应用和扩展的
`__TEXT,__entitlements` 段。真机构建需要为 `com.kingstacker.libzora` 和
`com.kingstacker.libzora.widgets` 分别准备包含 App Group 的 provisioning
profile。没有 profile 时可加 `-p:EnableWidgets=false` 跳过小组件构建。

本地预览脚本 `tests/Libzora.Mobile.iOS.UITests/prepare-preview.py` 会把嵌套扩展
的 bundle ID 同步改成 `<preview bundle id>.widgets`，并在重新签名时保留
entitlements，因此预览应用仍能读写同一个 App Group。

验证快照是否生成：

```bash
SIMULATOR_ID=4A14BDC2-FC9E-4C58-8112-50834D1BC74E
find ~/Library/Developer/CoreSimulator/Devices/$SIMULATOR_ID/data/Containers/Shared/AppGroup \
  -path "*LibzoraWidgets/snapshot.json"
```

## macOS 构建与打包

`src/Libzora.Widgets/build-macos-widgets.sh` 生成通用
`LibzoraWidgets.appex` 和 `libLibzoraWidgetBridge.dylib`。发布脚本
`scripts/build-macos-release.sh` 会：

1. 把 appex 放进 `Libzora.app/Contents/PlugIns/`；
2. 把桥接 dylib 放到每个架构的 payload 目录；
3. 先用 `LibzoraWidgets.macOS.entitlements` 单独签名 appex；
4. 再签名其余 Mach-O 和外层应用，避免破坏嵌套签名。

主应用本身不是沙盒应用，直接写入标准的组容器路径：

```text
~/Library/Group Containers/group.com.kingstacker.libzora/LibzoraWidgets
```

小组件扩展是沙盒进程，通过 `FileManager.containerURL(forSecurityApplicationGroupIdentifier:)`
读取同一目录。使用 Developer ID 分发时，macOS 通常要求 App Group 标识以团队
ID 开头（例如 `TEAMID.group.com.kingstacker.libzora`），并在签名 profile 中
声明该组；如果发布配置使用这种带前缀的组，需要同步修改
`LibzoraWidgets.macOS.entitlements`、`WidgetModels.swift` 中的 `appGroup`
以及 `MainWindow.Widgets.cs` 的组目录名。仓库默认值针对本地 ad-hoc 构建。
添加小组件：先启动一次 Libzora 让系统注册扩展，然后在 macOS 桌面空白处右键
选择「编辑小组件」，搜索 Libzora，再选择尺寸拖到桌面。

## 刷新

- WidgetKit 时间线每 20 分钟请求一次新快照。
- 主应用写完快照后会请求立即刷新：iOS 通过 `lz_ios_reload_widgets`，
  macOS 通过 `libLibzoraWidgetBridge.dylib` 调用
  `WidgetCenter.shared.reloadAllTimelines()`。
- 调试版 macOS 不包含桥接 dylib 时会静默跳过，最多等待下一次时间线刷新。

## 相关文件

- `src/Libzora.Widgets/Shared/`：数据模型、配色、视图、Widget 配置
- `src/Libzora.Widgets/build-ios-widgets.sh`
- `src/Libzora.Widgets/build-macos-widgets.sh`
- `src/Libzora.Widgets/MacWidgetBridge.swift`
- `src/Libzora.Infrastructure/WidgetSnapshotPublisher.cs`
- `src/Libzora.Mobile.iOS/NativeUI/NativeWidgets.swift`
- `src/Libzora.App/MainWindow.Widgets.cs`
