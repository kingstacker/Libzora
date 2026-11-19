# iCloud 同步

## 使用

iPhone、iPad 和 macOS 的同步设置提供 **S3 / WebDAV / iCloud**。Windows、Linux 和 Android 不显示 iCloud 选项。

1. 在系统设置中登录 Apple 账户并开启 iCloud 云盘。
2. 在 Libzora 的同步设置中选择 **iCloud**，开启同步并保存。
3. 各设备使用同一 Apple 账户、相同书库区域和目录前缀；使用加密时，在各设备输入相同密钥。
4. 可开启自动同步，或点击立即同步。关闭“同步时下载书籍文件”时，先同步书目和封面，打开书籍再下载文件。

不需要填写服务器地址、用户名或密码，也不需要导入或导出连接参数。数据保存在用户自己的 iCloud 存储中；其他设备收到更新可能有短暂延迟。

系统 Apple 账户更改或退出后，本机自动同步会暂停。确认当前账户后，重新开启并保存同步设置。恢复普通数据备份时也不会自动开启 iCloud，同步密钥和账户绑定不随备份传递。

## 存储与协议

- 两个苹果入口共用容器 `iCloud.com.kingstacker.libzora`，使用 CloudDocuments，而非 CloudKit。
- 容器的 `Documents` 内继续使用现有区域隔离：`regions/china/<前缀>/` 和 `regions/international/<前缀>/`。
- 只传输协议 6 的设备快照、书籍和封面对象，不把运行中的 SQLite 数据库放到 iCloud。
- 沿用同一套字段合并、混合逻辑时钟、书签与批注、删除标记、删除二次确认以及客户端加密规则。
- 新提供方的持久化值是 `SyncProvider.ICloud = 3`；`AccountServer = 2` 仍只识别已移除的历史官方云设置，不能复用。
- iCloud 的目录和对象键使用小写 NFC 形式，避免苹果文件系统将大小写或 Unicode 变体视为同一目录，绕过加密密钥更换保护。S3 / WebDAV 的既有地址与前缀保持不变。
- 本机设置只保存 Apple ubiquity identity token 的不透明 SHA-256 指纹；切换系统账户不能直接复用旧账户的删除基线。指纹不进入快照、普通备份或连接参数文件。

平台适配器通过 `NSFileManager.URLForUbiquityContainerIdentifier` 获取有授权的容器，并用 `NSFileCoordinator` 协调文件访问。**无法获取容器时必须报错，不能降级为普通本地目录并显示同步成功。**

读取会先请求 iCloud 下载，等待正文可读，再在读协调范围内复制为本机临时文件；`.name.icloud` 占位只提供逻辑对象名，不当作正文。上传先完整暂存，再同目录原子更名，等待 `NSURLUbiquitousItemIsUploadedKey` 确认当前版本已上传；配额不足、下载/上传错误与超时不会推进成功基线。检查既有对象时也会等待未完成的上传，避免上次中断留下的本机对象先被快照引用。

iCloud 的本机列表不是云端完整列表，故声明 `ReliableListing = false`、`ReliableLastModified = false`：禁用远端快照退役和对象垃圾回收，不根据缺失或暂未下载的文件推断业务删除。读取不承诺远端 Range，并发上限为 1。容器资料不公开为用户文档目录，避免展示内部快照与对象文件。

## Apple 能力和签名

代码和模拟器 UI 通过不代表已取得真实 iCloud 授权。正式设备使用前，需要在同一个 Apple Developer team 配置以下项目：

| 平台 | App ID | iCloud 容器 |
| --- | --- | --- |
| iOS / iPadOS | `com.kingstacker.libzora` | `iCloud.com.kingstacker.libzora` |
| macOS | `io.github.kingstacker.libzora` | `iCloud.com.kingstacker.libzora` |

在 Certificates, Identifiers & Profiles 创建共享容器，为两个 App ID 启用 **iCloud Documents** 并关联该容器，然后重新生成描述文件。相关权限为：

- `com.apple.developer.icloud-container-identifiers`
- `com.apple.developer.ubiquity-container-identifiers`
- `com.apple.developer.icloud-services = [CloudDocuments]`

### iOS

真机 profile 必须包含 iCloud 权限；启用小组件时还须保留现有 App Group 权限。主应用的 `Entitlements.plist` 已声明共享容器；小组件不负责同步，无需加入 iCloud 权限。

无描述文件的诊断构建可以同时使用 `-p:EnableWidgets=false -p:EnableICloud=false`；后者也会移除 iCloud provider 注册。详细安装步骤见 [iOS 真机开发](ios-development.md)。

### macOS 与 GitHub Actions

正式发行继续走 GitHub Actions。在现有证书、公证 secrets 之外，加入：

`APPLE_ICLOUD_PROVISIONING_PROFILE_BASE64`：授权 macOS App ID、共享容器和 CloudDocuments 的 **Developer ID** 描述文件 Base64。

工作流解码该文件并要求 iCloud 授权。打包脚本将其嵌入 `Contents/embedded.provisionprofile`，按 profile 的 App ID、team 和环境生成主应用权限；MCP 等其他可执行程序只带运行时权限，不获得 iCloud 权限。过期、错误 App ID、错误 team、错误容器/服务或开发用 profile 会拒绝签名准备。

本地构建传入：

```bash
APPLE_SIGNING_IDENTITY="Developer ID Application: Your Name (TEAMID)" \
APPLE_ICLOUD_PROVISIONING_PROFILE="/absolute/path/Libzora.provisionprofile" \
APPLE_REQUIRE_ICLOUD=1 \
  bash scripts/build-macos-release.sh 1.1.6 artifacts/macos osx-arm64
```

无 profile 的 ad-hoc 诊断包会剥离受限 iCloud 权限以保证可启动，只用于界面与本地逻辑验证，不能拿它证明真实 iCloud 可用。完整公证配置见 [macOS 签名与公证](macos-notarization.md)。

## 验证

本地回归覆盖提供方数值/显隐、设置保存和凭据隔离、账户变化、普通备份恢复、原子替换、占位读取、取消、缺失对象、双设备明文/加密的书籍、按需下载、阅读进度、批注与删除传播，以及禁止不可靠列表的垃圾回收。Foundation ABI 用临时普通文件验证，不接触用户 iCloud 数据。

真实设备回归需单独进行：

1. 安装使用上述有效 profiles 签名的 iPhone / iPad 和 macOS 应用，登录同一测试 Apple 账户。
2. 两端使用相同区域、前缀和密钥，同步带封面的多本测试书；互相修改进度、书签、批注后重试并确认收敛。
3. 将另一端文件置为未下载状态，验证下载占位不会被误删，按需打开能取回完整正文。
4. 验证离线/配额不足/取消不显示完整成功、不丢失本地书库，恢复网络后可重试。
5. 切换系统 Apple 账户，确认自动同步暂停，只有明确重新保存后才绑定新账户。

模拟器 `nativepreview` 的替换 bundle ID、临时签名与测试成功不代表正式容器/profile 已配置。

## Apple 参考

- [配置 iCloud 服务](https://developer.apple.com/documentation/xcode/configuring-icloud-services)
- [macOS 能力支持表（含 Developer ID 的 iCloud Documents 支持）](https://developer.apple.com/help/account/reference/supported-capabilities-macos/)
