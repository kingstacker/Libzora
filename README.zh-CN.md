# Libzora

[![Release](https://github.com/kingstacker/Libzora/actions/workflows/release.yml/badge.svg)](https://github.com/kingstacker/Libzora/actions/workflows/release.yml) [![最新版本](https://img.shields.io/github/v/release/kingstacker/Libzora)](https://github.com/kingstacker/Libzora/releases/latest)

[官方网站](https://libzora.stacker.beauty)

**简体中文** · [English](README.md)

Libzora 是一款基于 Avalonia 的跨平台电子书与 Kindle 管理器。它将本地书库、阅读、批注、AI 助手、格式转换和 Kindle 传输集中在一个简洁的桌面应用中。

![Libzora macOS 公版书库预览](website/assets/library.webp?v=20261116-public-domain)

桌面主图截取自 macOS 应用，移动端主图截取自 iOS 应用；README 图示统一展示本地公版书目录中的六本公版书。中英文页面分别使用对应语言的软件界面截图。

## 平台开发状态

| 平台 | 状态 | 说明 |
| --- | --- | --- |
| Windows | ✅ 已测试 | 稳定可用，小修小补进行中 |
| macOS | 🟡 初始测试 | 已提供安装包，修改与测试进行中 |
| iOS | 🚧 开发中 | 初始版本开发中 |
| Android | 🚧 开发中 | 初始版本开发中 |
| Linux | ⏳ 待测试 | 尚未进行完整测试 |

## 多端主界面

| macOS | iOS |
| :---: | :---: |
| <img src="website/assets/macos-library.webp?v=20261116-public-domain" alt="Libzora macOS 书库主界面" width="460"> | <img src="website/assets/ios-library.webp?v=20261116-public-domain" alt="Libzora iOS 书库主界面" width="190"> |
| 书库 | 书库 |

## 主要功能

- **书库管理**：导入 EPUB、PDF、MOBI、AZW3、TXT；TXT 自动识别 UTF-8、带 BOM 的 Unicode 或 GB18030 编码，识别章节及分卷目录并转换为 EPUB，无需 Calibre。管理元数据、封面、标签、分类、搜索和阅读状态；同名版本导入时可选择添加格式、独立保存或跳过，删除内容会先进入可恢复回收站。
- **Kreader 阅读器**：EPUB、PDF 和无 DRM 的 AZW3 可直接在应用内阅读；AZW3 使用内置 KF8 解析，无需安装 Calibre。支持分页/滚动阅读、目录、书签、查找、批注、排版设置、阅读进度，以及 Windows/Linux/macOS 听书（Linux 需安装 mpv、ffplay、VLC 或 GStreamer；macOS 使用系统自带的 afplay）。
- **PDF 阅读**：书库自动使用第一页作为封面，支持连续滚动、单页和双页阅读，以及适合宽度/整页、清晰缩放和 90° 旋转。支持选择文字、六种划线/标记样式、批注编辑，以及通过页注图标或右键在 PDF 任意位置添加页面批注。打开时先显示当前页，全文搜索在后台准备；可见区域按屏幕像素密度重绘。搜索、书签和阅读进度沿用 EPUB 的本地保存逻辑。扫描版可显示原页并添加页面笔记，暂不提供 OCR。
- **AI 阅读助手**：围绕当前书籍提问、章节总结、选文解释和全书概览；支持自动获取模型、模型选择、连通性检测以及 DeepSeek、OpenAI 兼容接口。只发送相关本地片段。
- **Kindle 管理**：识别 USB/WPD/MTP 设备，传输书籍，管理字体和词典，导入 `My Clippings.txt`。
- **工具与同步**：Calibre 格式转换、加密本地备份、凭据保护，以及**当前版本无需登录即可使用**的自备 S3 / WebDAV 跨端同步。账号注册暂未上线；账号服务只做身份和功能权益管理，不存储书籍。

## 下载

通过[官网下载页](https://libzora.stacker.beauty/#downloads)获取 Windows、Linux 或 macOS 安装包；页面会自动选择最新稳定版。

备用下载：[GitHub Releases](https://github.com/kingstacker/Libzora/releases)。

格式转换需要另行安装 Calibre。GitHub 官方 Release 提供 Windows、Linux 和 macOS 包。macOS 包支持 macOS 12+ 的 Intel 与 Apple Silicon；未配置 Apple 发布凭据时使用临时签名，首次启动需要按 [docs/macos-adhoc-install.md](docs/macos-adhoc-install.md) 放行。

设置中的“诊断”页面会检查数据目录、PDF 解析器、WebView、Calibre、TTS 和 Kindle 服务；跨平台问题可先从这里确认。书库设置中的“回收站”支持恢复误删的整本书或单个格式，也可在确认后永久清理。回收站内容会随本地备份一起保存。

## Free / 试用订阅 / 买断订阅

**当前发布的客户端版本不收取费用**：完整本地功能与自备 S3 / WebDAV 跨端同步均可直接使用，账号注册暂未上线，无需登录。下表为账号服务上线后的规划方案，不代表当前已开放注册、购买或支付。

| 档位 | 价格与期限 | 权益 |
| --- | --- | --- |
| **Free** | 永久免费 | 完整本地阅读、书库、USB Kindle、插件和自配 AI |
| **试用订阅（Trial）** | 7 天免费试用，每账号一次，由用户主动领取 | 全部 Free 权益，以及自备 S3 / WebDAV 跨端书籍、阅读进度、书签和批注同步 |
| **买断订阅（Lifetime）** | ¥88 永久，一次购买，无需续费 | 全部 Free 权益，以及上述自备 S3 / WebDAV 跨端同步的永久使用权 |

每账号最多同时登录 **6 台设备**。所有档均不提供官方云存储或官方云同步；账号服务只做身份认证和功能权益管理，不接收用户书籍。存储由用户自行配置，存储与流量费用自行承担；AI 服务费用按所选服务商规则承担。试用过期只停用付费同步功能，不删除用户自备存储中的数据或本地书库，已购 Lifetime 权益不受试用到期影响。

买断权益直接绑定账号，永久权益由受保护管理员按外部授权开通，客户端不能自授，只刷新并实时校验。**购买和开通方式以实际渠道公告为准；开通后刷新权益。** 7 天与 ¥88 沿用现有规则，不表示购买渠道、自动支付或支付网关已经上线。账号服务仅保留必要认证与邮箱恢复、区域隔离、6 台设备 / 会话安全、试用、功能权益验证 / 到期、账号停用及受保护后台权益开通 / 撤销 / 审计；不处理产品、订单、支付或购买账单目录，不提供激活码生成、兑换和管理，也不接收反馈截图或提供云对象 / 配额统计。

历史已付限时权益按原到期时间继续有效，不自动变永久。自助“注销”改为当前密码 + 手动输入确认文字的**账号停用**并撤销会话，保留必要许可 / 审计记录，不表示服务器数据已删除。本地与用户自备存储数据保留；本次调整不迁移或删除真实数据库、旧账本、历史已用码和云文件；旧码与账本仅必要只读兼容，不恢复兑换。

## 自备 S3 / WebDAV 跨端同步

当前版本无需登录即可配置自备 S3 / WebDAV 并跨端同步；账号注册暂未上线。账号服务上线后，中国区 / 国际区的账号、权益、会话、本地书库和同步命名空间仍将保持隔离，不互通。独立 PostgreSQL 服务与管理网页见[账号服务说明](docs/account-server.md)。

在“设置 → 数据与同步 → 云端同步”中选择 **S3** 或 **WebDAV**，填写自己的连接信息后点击“保存设置”。书籍和快照直接在客户端与自备存储间传输，不经过账号服务。两种方式的地址和凭据分别保留；立即同步、启动/退出时同步和定时同步均使用当前保存的方式。

WebDAV 服务地址应指向已存在的目录，例如 `https://dav.example.com/dav/`。需要认证时填写用户名和密码（部分服务使用应用密码）。程序会自动创建同步子目录，默认名为 `libzora`，可在高级选项中修改；测试连接会验证读写能力并清理测试文件。支持标准 WebDAV 的 PROPFIND、MKCOL、GET、PUT、MOVE、DELETE 操作；文件上传完毕后通过 MOVE 发布，避免中断上传覆盖已有快照。

同一区域内，各同步方式共用同步子目录、加密密钥、同步间隔和传输选项。跨设备同步同一目录时应使用相同的加密密钥；更换密钥请使用新的同步子目录。独立连接参数文件只导出所选方式的凭据，兼容原来的 `.libzora-s3.json`，WebDAV 使用 `.libzora-webdav.json`。

**所有 `.libzora` 数据备份都使用本机的同步密钥加密**，覆盖书籍、阅读记录和书库信息。未设置密钥时，导入、导出会先提示设置，即使未启用云同步也必须设置。换设备恢复时，先设置原来的密钥，再导入备份。备份不包含连接密码、账号会话或密钥本身。**密钥由用户自行牢记，不上传服务器，不提供找回；遗忘后无法解密已有数据，设置新密钥也不能解开旧备份。** 旧版明文备份不再允许导入。详见[加密备份说明](docs/backup-encryption.md)。

## 公版书测试样本

仓库的 [公版书](公版书/README.md) 文件夹提供六本与截图一致的 EPUB，可下载后导入应用，测试书库、阅读、批注及 Kindle 传书。目录内附来源和文件校验值。

## 从源码运行

需要 `global.json` 锁定的 [.NET SDK 10.0.400](https://dotnet.microsoft.com/download/dotnet/10.0)。

Windows（PowerShell）：

```powershell
dotnet restore Libzora.sln -p:Platform=x64
dotnet build Libzora.sln -p:Platform=x64
dotnet test Libzora.sln --no-build -p:Platform=x64
# 发布前需要完整回归时再显式加入慢测试
dotnet test Libzora.sln --no-build -p:Platform=x64 -p:RunSlowTests=true
dotnet run --project src\Libzora.Desktop.Windows\Libzora.Desktop.Windows.csproj -p:Platform=x64
```

macOS：

```bash
dotnet build src/Libzora.Desktop.MacOS/Libzora.Desktop.MacOS.csproj
dotnet test tests/Libzora.Tests/Libzora.Tests.csproj
dotnet run --project src/Libzora.Desktop.MacOS/Libzora.Desktop.MacOS.csproj
```

Linux 使用相同命令，把 `Libzora.Desktop.MacOS` 换成 `Libzora.Desktop.Linux`。

构建 macOS 应用包：

```bash
# Apple Silicon
bash scripts/build-macos-release.sh 1.1.3 artifacts/macos osx-arm64
# Intel
bash scripts/build-macos-release.sh 1.1.3 artifacts/macos osx-x64
# 通用版（一个包同时支持两种架构）
bash scripts/build-macos-release.sh 1.1.3 artifacts/macos osx-universal
```

这些命令在任一架构的 Mac 上都能执行：随包分发的 libmtp 和 libusb 会按目标架构从源码构建（与主机架构不同时用 macOS SDK 交叉编译），所以 Intel Mac 也能打出 arm64 包，反之亦然。CI 的 Apple Silicon 使用 `macos-26`，Intel 使用 `macos-15-intel`，两者固定 Xcode 26.3；ARM64 主机环境也需要与 Icon Composer 工具链兼容。

`osx-universal` 生成的单个 `Libzora.app` 可以在 Intel 和 Apple Silicon 上原生启动。.NET 自包含发布包含按架构编译的原生库和 ReadyToRun 程序集，无法合并成同一套文件，因此包里同时携带两份完整负载（`Contents/MacOS/x86_64` 与 `Contents/MacOS/arm64`），由通用启动器选择对应架构。体积约为单架构包的 2 倍。

默认测试会跳过耗时的 UI/渲染/GC 压力用例，只保留核心、平台、设备和少量 UI 冒烟回归；这些用例仍保留在仓库中，可用 `RunSlowTests=true` 手动开启。

Linux 还需要 WebKitGTK 和 Secret Service。平台打包和签名细节见[跨平台说明](docs/cross-platform.md)。移动端位于 `Libzora.Mobile.sln`，工作负载和模拟器命令见[移动端说明](docs/mobile-skeleton.md)。

## 技术参考

- **功能参考与工具**：[Calibre](https://github.com/kovidgoyal/calibre)、[KFX Input](https://www.mobileread.com/forums/showthread.php?t=291290)。
- **运行依赖**：[Avalonia](https://github.com/AvaloniaUI/Avalonia)、[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)、[EF Core](https://github.com/dotnet/efcore)、[PdfPig](https://github.com/UglyToad/PdfPig)。
- **测试工具**：[xUnit](https://github.com/xunit/xunit)、[VSTest](https://github.com/microsoft/vstest)。

## 代码贡献者

感谢以下开发者为 Libzora 提交代码与改进：

- sonianmu（Mutro）

## 致谢

- 社区：[LINUX DO](https://linux.do/?tl=en)
- 公益站：any

## 许可证

本项目采用 [GPL-3.0 License](LICENSE) 开源；第三方组件和随应用分发的字体遵循各自许可证。
