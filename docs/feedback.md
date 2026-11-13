# 问题反馈

桌面端“关于”页面提供“问题反馈”入口，打开独立的反馈窗口；iOS 原生前端在“我的 → 关于”
中提供同一入口，进入原生反馈页。两端都通过当前区域的账号服务提交，服务端从登录令牌推导
用户身份，客户端提交的 `userId` 一律忽略。本文同时说明客户端流程、仓库内 SQLite 协议服务
的接口，以及正式 PostgreSQL 服务的对应实现（独立仓库 `Libzora_server`）。

## 客户端流程

1. 打开“关于”页，点击“问题反馈”。桌面端打开独立窗口，iOS 进入“问题反馈”原生页面。
2. 未登录时提示先登录，并提供“前往登录”按钮；登录后重新打开即可填写。iOS 登录后会回到
   反馈页并自动显示表单。
3. 选择类型：`问题 Bug`、`功能建议`、`同步问题`、`其他问题`。
4. 填写问题描述（必填，5–4000 字），可填写 QQ 号或邮箱作为联系方式。
5. 可添加最多 3 张截图，添加后在页面内预览，可逐张移除。iOS 使用系统照片选择器，
   无需申请相册权限。
6. “自动附加软件日志”默认勾选。勾选后，提交时会把本地已脱敏的 `software.log`
   快照（最多 256 KB）作为纯文本附件一起上传；取消勾选则不发送任何日志。
7. 点击“提交反馈”。提交期间按钮和输入被禁用，避免重复提交。
8. 成功后显示人类可读的反馈编号；失败时保留已填写内容并显示错误原因。
9. 每次打开反馈页面生成一个 `requestId`；提交失败后在同一页面重试会沿用该值。若第一次请求
   实际已到达服务端，重试返回原反馈而不会生成重复记录。

客户端自动附带：

| 字段 | 取值 |
| --- | --- |
| `appVersion` | 程序集显示版本，例如 `1.0.0` |
| `platform` | `windows`、`macos`、`linux`（iOS 为 `ios`） |
| `platformVersion` | 操作系统版本 |
| `deviceModel` | macOS `hw.model`、Linux DMI 产品名，其他平台回退为“平台 架构” |
| `softwareLog` | 可选；勾选时上传的脱敏软件日志，纯文本，≤ 256 KB |

不收集书籍、阅读进度、批注、书库路径或同步密钥。截图由用户主动选择，桌面端和 iOS 都会
先解码，将最长边缩至 1600 px、转白底 JPEG（质量 82）后再上传。

### 软件日志设计

软件日志只服务于问题反馈，是一个独立于崩溃日志和调试日志的有界文件
（`<数据目录>/logs/software.log`，单文件上限 512 KB，轮转为 `software.1.log`）。
它只记录显式的失败事件和会话头，不记录阅读、搜索、导入、同步、AI 或 TTS 的正常操作。

记录的事件类别：

| 事件 | 触发条件 |
| --- | --- |
| `session.start` | 进程启动，记录版本、平台与系统版本 |
| `app.unhandled.ui` / `app.unhandled.process` | 未处理的 UI / 进程异常 |
| `library.initialize` | 本地书库初始化失败 |
| `library.import` | 导入书籍失败 |
| `reader.open` | 打开 EPUB / PDF / 缓存或系统阅读器失败 |
| `sync.run` / `sync.auto` | 手动或自动云同步失败 |
| `ai.request` | AI 请求失败 |
| `tts.playback` | 朗读合成或播放失败 |
| `plugin.call` / `plugin.script` | 插件桥接或脚本执行失败 |
| `update.check` / `update.install` | 检查更新或启动安装失败 |
| `feedback.submit` | 提交反馈失败 |

每条日志包含 UTC 时间、事件名、异常类型链、`HResult` 和受长度限制的堆栈。自动记录的事件
默认不写异常消息，只记录类型与堆栈，避免把 AI 回复、插件输出、书籍文件名、同步对象键或
其他用户内容写入日志；唯一例外是 `feedback.submit`，它会记录服务端返回的提交失败原因。

所有写入内容在落盘前统一脱敏：

- 数据根目录与用户主目录替换为 `<app>` / `<home>`；
- 邮箱替换为 `<email>`，GUID 替换为 `<id>`，IPv4 替换为 `<ip>`；
- URL 只保留协议和主机，去掉路径、查询参数和用户信息；
- Bearer/Basic 凭据、`api_key`、`token`、`password` 等键值替换为 `<redacted>`；
- POSIX / Windows 绝对路径、含路径或书籍扩展名的引号内容替换为 `<path>`；
- `《书名》` 与 `.epub`、`.azw3`、`.mobi`、`.pdf` 等书籍文件名替换为占位符；
- 32 位以上令牌样字符串替换为 `<redacted>`。

日志文件本身也是脱敏后的版本。“关于 → 导出软件日志”导出的 ZIP 只包含该文件及其轮转文件
（`software.log`、`software.1.log`）和环境诊断，不再打包其他原始调试日志或崩溃日志。
客户端上传时只取当前文件及上一份轮转文件的尾部；服务端只做大小、控制字符和 UTF-8 形状校验，
不解析日志内容。

### 关联代码

| 位置 | 作用 |
| --- | --- |
| `src/Libzora.App/FeedbackWindow.axaml(.cs)` | 桌面反馈窗口、截图选择与压缩、日志开关 |
| `src/Libzora.App/MainWindow.Feedback.cs` | 从“关于”页打开/复用反馈窗口 |
| `src/Libzora.Core/FeedbackModels.cs` | 共享类型、限制与校验 |
| `src/Libzora.Infrastructure/SoftwareLog.cs` | 脱敏、限长、轮转的软件日志 |
| `src/Libzora.Infrastructure/SoftwareLogExportService.cs` | “导出软件日志”ZIP：仅打包脱敏日志与诊断 |
| `src/Libzora.Infrastructure/AccountService.cs` | `SubmitFeedbackAsync`、`GetFeedbackAsync` |
| `src/Libzora.Mobile.iOS/NativeUI/NativeFeedback.swift` | iOS 原生反馈页、照片选择与截图压缩 |
| `src/Libzora.Mobile.iOS/NativeFeedbackRequest.cs` | Swift 表单到共享反馈契约的转换与截图校验 |
| `src/Libzora.Mobile.iOS/IOSNativeFrontend.Settings.cs` | `settings.feedback.submit` 桥接命令 |
| `src/Libzora.AccountServer/FeedbackEndpoints.cs` | 仓库内 SQLite 协议服务端点 |
| `src/Libzora.AccountServer/FeedbackStorage.cs` | 截图私有文件存储 |
| `tests/Libzora.App.Tests/FeedbackWindowTests.cs` | 登录门槛与提交流程 UI 测试 |
| `tests/Libzora.Mobile.Tests/NativeFrontendContractTests.cs` | iOS 反馈桥接字段与截图校验测试 |
| `tests/Libzora.AccountServer.Tests/AccountIntegrationTests.Feedback.cs` | 协议服务集成测试 |

## HTTP API

所有请求必须携带：

```http
Authorization: Bearer <SESSION_TOKEN>
X-Libzora-Region: china | international
```

正式服务还要求 HTTPS；区域头必须与服务器固定区域一致，否则返回 403。用户 ID 永远取自
令牌对应的会话，表单中的 `userId` 不存在于接口契约中，服务端也不会读取。

### POST `/api/v1/feedback`

`multipart/form-data`，字段如下：

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `requestId` | 是 | 客户端生成的 UUID，用于幂等 |
| `type` | 是 | `bug`、`feature`、`sync`、`other` |
| `content` | 是 | 问题描述，5–4000 字 |
| `contact` | 否 | QQ 号（5–12 位数字）或有效邮箱，最多 100 字 |
| `appVersion` | 是 | 最多 32 字 |
| `platform` | 是 | 最多 16 字；仅有 `iosVersion` 时兼容推断为 `ios` |
| `platformVersion` | 是 | 最多 64 字；为空时回退到 `iosVersion` |
| `deviceModel` | 是 | 最多 128 字 |
| `softwareLog` | 否 | 已脱敏的纯文本日志，≤ 256 KB；拒绝 NUL 等控制字符 |
| `attachments` | 否 | 可重复；最多 3 个文件，每个 ≤ 4 MB，总计 ≤ 10 MB |

截图只接受 JPEG、PNG、WebP，按文件头魔数判断，扩展名和声明的 `Content-Type` 不作为依据。
单张超过 4 MB、总计超过 10 MB 或超过 3 张时返回 413。`softwareLog` 超过 256 KB 或包含
不允许的控制字符时返回 400。

成功：

- 首次创建：`201 Created`
- 同一用户重复提交相同 `requestId`：`200 OK`，返回原反馈，不重复写入或上传

```json
{ "id": "3f0c...", "number": 128, "status": "pending", "createdAt": "2026-11-11T08:00:00+00:00" }
```

`number` 是人类可读的反馈编号，在单个区域内递增；`id` 是 UUID，用于详情查询。

### GET `/api/v1/feedback`

返回当前用户最近 100 条反馈，按提交时间倒序：

```json
[
  {
    "id": "3f0c...",
    "number": 128,
    "type": "bug",
    "content": "阅读页切换章节时闪退。",
    "contact": "12345678",
    "appVersion": "1.0.0",
    "platform": "macos",
    "platformVersion": "15.0",
    "deviceModel": "MacBookPro18,3",
    "status": "pending",
    "adminReply": null,
    "createdAt": "2026-11-11T08:00:00+00:00",
    "updatedAt": "2026-11-11T08:00:00+00:00",
    "attachmentCount": 2,
    "hasSoftwareLog": true
  }
]
```

`attachmentCount` 只统计截图数量；`hasSoftwareLog` 表示本次反馈是否附带软件日志。

### GET `/api/v1/feedback/{id}`

返回当前用户名下的一条反馈。其他用户的反馈返回 `404 feedback_not_found`，不泄露是否存在。

### 错误码

错误通过响应头 `X-Libzora-Error` 返回稳定错误码，响应体包含可直接展示的中文消息：

| HTTP | 错误码 | 场景 |
| --- | --- | --- |
| 400 | `invalid_feedback` | 类型、描述、联系方式或元数据不合法 |
| 400 | `invalid_feedback_attachment` | 截图不是受支持的图片格式 |
| 401 | 认证失败 | 未登录、令牌过期或会话被撤销 |
| 403 | `region_mismatch` | 区域头与服务端不一致 |
| 404 | `feedback_not_found` | 反馈不存在或不属于当前用户 |
| 413 | `feedback_too_large` | 单张、总计或请求体超过大小限制 |
| 429 | `feedback_rate_limited` | 超过用户或 IP 频率限制 |

客户端对 401 会使本机会话失效并要求重新登录；429 保留表单内容，提示稍后再试。

## 限流

| 范围 | 限制 |
| --- | --- |
| 单用户滚动 10 分钟 | 5 条 |
| 单用户滚动 24 小时 | 20 条 |
| 单 IP 滚动 10 分钟 | 10 条（正式服务，ASP.NET Core 限流） |

同一 `requestId` 的重试不计入新的反馈，但 HTTP 匿名/IP 限流仍按请求计数。

## 存储与数据

### 仓库内 SQLite 协议服务

表结构：

```sql
CREATE TABLE IF NOT EXISTS Feedback (
    Id TEXT PRIMARY KEY, Number INTEGER NOT NULL UNIQUE,
    UserId TEXT NOT NULL REFERENCES Accounts(Id) ON DELETE CASCADE,
    RequestId TEXT NOT NULL, Type TEXT NOT NULL, Content TEXT NOT NULL, Contact TEXT,
    AppVersion TEXT NOT NULL, Platform TEXT NOT NULL, PlatformVersion TEXT NOT NULL,
    DeviceModel TEXT NOT NULL, AttachmentUrls TEXT NOT NULL, Status TEXT NOT NULL,
    AdminReply TEXT, CreatedAt INTEGER NOT NULL, UpdatedAt INTEGER NOT NULL,
    AdminRepliedAt INTEGER, UNIQUE(UserId, RequestId)
);
```

- 表由现有 `AccountDatabase` 初始化脚本幂等创建，无需单独迁移。
- 截图保存在账号存储目录的 `feedback/{feedbackId:N}/{storageId:N}.{jpg|png|webp}`，
  与同步对象前缀分离；同步列举、清理和 Plus 保留策略不会触碰。
- 软件日志保存在同一目录下，固定为 `feedback/{feedbackId:N}/{storageId:N}.txt`，
  最多一个；`AttachmentUrls` 同时记录截图与日志键，`hasSoftwareLog` 由 `.txt` 键推导。
- `AttachmentUrls` 列保存服务端生成的附件键，不接受客户端路径。
- 删除账号时 `Feedback` 行随外键级联删除，附件目录由账号删除流程一并清理。

### 正式 PostgreSQL 服务

正式服务使用独立仓库 `~/Desktop/Libzora_server`，schema v16 新增 `feedback` 表（当前为 v17，
v17 增加上传完成幂等记录）；截图通过现有 `ObjectBackend` 存入 `{account}/f/` 私有命名空间
（本地文件或 S3）。详见该仓库的 `docs/feedback.md`。

## 管理后台

正式服务 `/admin` 新增：

- `/admin/feedback`：按类型、状态、编号/用户/内容关键词筛选，分页列出全部反馈。
- `/admin/feedback/{id}`：查看完整描述、用户、App/系统/设备信息、联系方式和截图；
  修改状态（待处理 / 处理中 / 已解决），填写管理员回复。
- `/admin/feedback/{id}/attachment/{attachmentId}`：需要管理员 Cookie 认证的截图读取端点，
  不生成公开 URL。

状态修改和回复写入现有审计日志（`feedback.status_changed`、`feedback.replied`），记录
管理员、原因和前后状态。管理页面沿用现有 Cookie 认证与 CSRF 防护。

仓库内 SQLite 协议服务只实现客户端 API，不包含管理页面；后台功能只在正式 PostgreSQL
服务提供。

## 隐私与安全

- 用户身份完全由服务端会话推导，忽略客户端传入的用户 ID。
- 用户只能列出和读取自己的反馈；跨用户详情统一返回 404。
- 截图私有存储，仅管理员认证后可读；不写入数据库 BLOB，不提供公开链接。
- 软件日志由客户端在写入前脱敏，服务端只按纯文本验证和存储；不解析、不执行日志内容。
- 日志不记录令牌、密码、反馈正文或截图内容。
- 文本字段限制长度并拒绝控制字符；SQL 全部使用参数化命令。
- 管理操作需要管理员认证、CSRF 校验并写审计日志。
- 反馈不采集书籍、阅读记录、批注或同步密钥；截图和软件日志由用户主动附上（日志可取消勾选）。

## 扩展点

- 用户查看自己反馈与管理员回复：客户端已有 `GetFeedbackAsync()`，列表和详情 API 已返回
  `status` / `adminReply`，可直接接入“我的反馈”页面。
- 回复通知：在 `UpdateFeedbackAsync` 成功写入回复后接入邮件或推送任务即可，状态与回复人
  已持久化。
- 用户追问：可按现有 `feedback` 行增加子表，复用同一用户作用域和管理端点。
- 管理员邮件通知：在创建反馈成功后投递，失败不影响提交结果。

## 验证

```bash
# 桌面客户端
dotnet test tests/Libzora.App.Tests/Libzora.App.Tests.csproj
dotnet test tests/Libzora.AccountServer.Tests/Libzora.AccountServer.Tests.csproj

# iOS 桥接契约与原生界面
dotnet test tests/Libzora.Mobile.Tests/Libzora.Mobile.Tests.csproj
python3 tests/Libzora.Mobile.iOS.UITests/run-tests.py --simulator <模拟器ID> --results <新的结果目录> \
  --only testFeedbackSubmissionUsesTheSignedInAccount

# 正式 PostgreSQL 服务（独立仓库）
cd ~/Desktop/Libzora_server && python3 scripts/local.py test
```

iOS 反馈用例需要本地中国区账号服务（默认 `http://127.0.0.1:5088`）和模拟器相册中至少一张
照片。用例注册一次性账号，选择“功能建议”、填写描述、通过系统照片选择器添加一张截图，
确认收到反馈编号，并在结束后退出登录；默认的 iOS 界面套件跳过该用例。

覆盖：未登录拒绝、伪造用户 ID 无效、跨用户隔离、截图私有存储、软件日志存储与
`hasSoftwareLog`、非法字段/格式/数量/大小、幂等重试、用户频率限制、每日上限、账号删除清理、
管理员认证/CSRF/回复/状态/审计，以及 iOS 表单/截图/日志开关桥接、登录门槛和真实提交流程。
