# 账号与功能权益：本机验证与正式部署配置

本文只列配置入口，不执行公网部署，也不提供任何真实凭据。两区数据库、账号、功能权益、
会话和本地书库必须独立；自备 S3 / WebDAV 使用各自区域前缀，不能用切换域名把中国区回退到国际区。

当前公开方案为 **Free / 试用订阅（Trial）/ 买断订阅（Lifetime）**：Free 包含完整本地阅读、书库、USB Kindle、插件和自配 AI；Trial 为用户主动领取的 **7 天试用、每账号一次**；Lifetime 为 **¥88 永久、无需续费**。Trial 有效期内和 Lifetime 解锁自备 S3 / WebDAV 跨端书籍、进度、书签及批注同步，每账号最多同时登录 **6 台设备**。存储与流量费用由用户承担。

所有档均无官方云存储或官方云同步。账号服务仅保留必要账号认证与邮箱恢复、区域隔离、6 台设备 / 会话 / 封禁安全、试用领取与到期、功能权益验证 / 到期、账号停用及受保护后台权益开通 / 撤销 / 审计；不接收书籍或快照，不处理产品、订单、支付或购买账单，也不提供码生成、兑换或管理。

买断权益直接绑定账号，永久权益由受保护管理员按外部授权开通，客户端不能自授，只刷新并实时校验。**购买和开通方式以实际渠道公告为准；开通后刷新权益。** 7 天与 ¥88 沿用现有规则，不表示购买渠道、自动支付或支付网关已上线。

试用过期只停用付费同步功能，不删除自备存储数据或本地书库；历史已付限时权益按原到期时间继续有效，不自动转为永久。自助“注销”改为当前密码 + 手动输入确认文字的账号停用 / 会话撤销，保留必要许可与审计记录，不表示服务器数据已删除。本文不承诺新方案已在线部署；真实数据库、旧账本、已用码及历史云文件不迁移或删除。

**客户端当前状态**：`src/Libzora.Core/AccountAvailability.cs` 中 `RegistrationAvailable` 与 `EnforceSyncEntitlements` 均返回 `false`。因此当前构建的注册入口提示暂未上线，自备 S3 / WebDAV 同步不校验账号权益，全部功能免费。部署账号服务并经联调后，将两项改为 `true` 并重新构建客户端，才会开放注册并启用同步权益校验。

## 1. 客户端：只改一处两区默认地址

所有客户端前端共用 `src/Libzora.Core/AccountServerEndpoints.cs`：

| 字段 | 当前本机默认值 | 正式部署时修改为 |
| --- | --- | --- |
| `ChinaDefaultServerUrl` | `http://127.0.0.1:5088` | 中国区账号服务的 HTTPS 基址 |
| `InternationalDefaultServerUrl` | `http://127.0.0.1:5089` | 国际区账号服务的 HTTPS 基址 |

修改这两个常量后重新构建要分发的客户端即可；不需要在桌面、共享移动端和原生 iOS 前端
分别搜索替换地址。它们是直接编译的 C# 值，不依赖程序集反射或运行时读取外部配置文件。
示例仅为不可访问的虚构域名：

```csharp
public const string ChinaDefaultServerUrl = "https://accounts-cn.example.invalid";
public const string InternationalDefaultServerUrl = "https://accounts-intl.example.invalid";
```

`LocalDevelopmentServerUrl` 是客户端仓库里 SQLite **协议测试服务**的固定监听默认值，
不要跟随正式域名修改。正式 PostgreSQL 服务在独立的 `~/Desktop/Libzora_server` 项目中。

### 不重建的运行覆盖

进程环境变量优先于编译默认值，分别覆盖对应区域：

- `LIBZORA_ACCOUNT_SERVER_CHINA`
- `LIBZORA_ACCOUNT_SERVER_INTERNATIONAL`

例如在 macOS 终端启动本机 Debug 客户端：

```sh
env LIBZORA_ACCOUNT_SERVER_CHINA=http://127.0.0.1:5088 \
    LIBZORA_ACCOUNT_SERVER_INTERNATIONAL=http://127.0.0.1:5089 \
    ./src/Libzora.Desktop.MacOS/bin/Debug/net10.0/Libzora
```

Finder 启动的应用通常不会继承终端变量；iOS/Android 分发版应修改编译默认值，开发运行
也可在其启动环境里设置变量。这里没有单独的构建属性或开关，不会加入生产授权绕过。

地址只允许 HTTP/HTTPS，不允许嵌入用户名、密码、查询参数或片段。公网必须 HTTPS；
明文 HTTP 仅限回环开发地址和 Android 开发机别名 `10.0.2.2`。变量**未设置**才使用编译
默认值；设置为空或设置非法地址会明确报配置错误，不会静默回退或打印错误值。

### 已安装客户端如何更新地址

本机 `data/account-session.json` 的每区连接可包含非敏感标记 `usesDefaultServerUrl`：

- 未登录且使用默认地址：跟随本次启动的对应区域默认值；退出登录后的下一次状态读取也会更新。
- 已保存的自定义地址：继续使用该地址，不被部署默认值覆盖。
- 已登录会话：始终绑定保存的原服务器，验证会话、退出登录和权益请求都不会把旧令牌
  发到新域名。先退出旧服务，再连接新服务；新默认值不能迁移账号数据库、权益或自备存储内容。
- 旧记录没有标记时，只把该区域已知的本机开发默认地址视为默认；自定义 HTTPS 地址、
  非默认端口或带基路径的连接保留。显式 `false` 的本机连接也保留。

标记与账号会话一样只留在设备上，不进入便携备份或云同步。即使新默认配置非法，本机
退出登录仍先清除令牌，再在后续状态读取时报告配置错误。

修改默认域名不等于迁移账号或权益，不能据此承诺线上迁移已完成。用户自备存储仍沿用独立的区域命名空间、凭据和加密密钥；同步密钥由用户自行保管，账号服务不接收、不保管、不提供找回。已有旧加密格式的只读兼容见[快照兼容约定](s3-snapshot-compatibility.md)。

## 2. 独立服务端：部署字段

Docker Compose 的入口是服务端项目的 `.env.example` 和 `compose.yaml`。将示例复制到
被忽略的 `.env` 后由部署环境填入真实值，不把 `.env`、连接串或任何凭据提交到仓库。
`CHINA_` 与 `INTERNATIONAL_` 字段必须分别填写；域名字段仅填主机名，不含 `https://`。

| Compose `.env` 字段 | 对应作用 |
| --- | --- |
| `CHINA_HOST` / `INTERNATIONAL_HOST` | 各区 HTTPS 网关域名，同时用于 `AllowedHosts`；须与客户端基址对应 |
| `CHINA_DB_PASSWORD` / `INTERNATIONAL_DB_PASSWORD` | 各自 PostgreSQL 账号密码；不得共用数据库或凭据 |
| `*_REGISTRATIONS_PER_IP_PER_DAY` / `*_REGISTRATIONS_PER_IP_PER_30_DAYS` | 各区注册限制，默认 3 / 10 |

不使用 Compose 时，以下是 .NET 的实际环境变量名称；双下划线表示配置层级：

| 服务进程环境变量 | 说明 |
| --- | --- |
| `Server__Region` | `China` 或 `International`，与数据库、数据目录及网关固定绑定 |
| `ConnectionStrings__Accounts` | 当前区域 PostgreSQL 连接串，由私有部署环境提供 |
| `Server__DataDirectory` | 当前区域独立、持久化的数据根目录 |
| `ASPNETCORE_URLS` | 内部 HTTP 监听地址；对外由 HTTPS 网关代理，不等同于客户端基址 |
| `AllowedHosts` | 当前区域允许的公开主机名，不使用任意主机通配符代替正式配置 |
| `Server__KnownProxies__0` | 确实可信的反向代理 IP；更多地址依次使用 `__1` 等 |
| `Server__LocalDevelopment` | 只供 `Development` 环境下的回环本机验证；生产不得启用 |

Compose 的可信代理 IP 与其独立网络绑定；换网关或网络后应同步调整，不能信任用户随意传来的 `X-Forwarded-For` / `X-Forwarded-Proto`。TLS、数据库备份、管理员认证、CSRF 和日志脱敏仍按服务端 README 执行，运行用户与目录须遵守最小权限。

活动服务彻底移除官方云、额度 / 用量、商品 / 订单 / 购买账单目录、码生成 / 兑换 / 管理、反馈截图上传和云清理模块。旧 `Storage__*`、购买开关及截图存储配置不再属于部署要求，不能通过保留空商品目录或零配额响应维持兼容。不代理官方书籍 `/api/sync/`、`/sync-blobs/`、旧购买或反馈接口，也不把用户自备 S3 / WebDAV 的凭据写入账号服务。移除模块不是数据清理：不迁移或删除真实数据库、旧账本、已用码及历史云文件。

## 3. 新方案上线前验证

- Free 的完整本地功能可用，但无付费跨端同步权益。
- 用户主动领取一次 7 天 Trial；注册、登录、重复或并发领取不重复发放。
- Trial 过期仅停用付费同步，不删除用户自备存储数据或本地书库；已购 Lifetime 仍有效。
- ¥88 Lifetime 由受保护管理员按外部授权直接为对应区域账号开通，永久有效、无需续费；开通后客户端刷新并实时校验，不可自行授予。服务不处理产品、订单或支付，不提供码生成、兑换、管理及相关响应协议。
- 历史已购永久与有效限时权益保留原期；限时权益不自动变永久，刷新不恢复已撤销权益；历史已用码与账本仅必要只读兼容，不恢复兑换。
- 每账号最多同时登录 6 台设备，踢出、改密码、账号停用与会话失效正确撤销会话及阻止权益验证；停用须验证当前密码和手动输入的确认文字，保留必要许可 / 审计及本地、自备数据，不返回“服务器数据已删除”的成功提示。
- 两区账号、权益、本地书库和自备存储前缀继续隔离，旧令牌不能发往新域名。
- 在真实自备 S3 / WebDAV 上核验 TLS、连接凭据保护、读写、两设备书籍 / 进度 / 书签 / 批注同步、按需下载、加密及备份恢复。
- 账号服务没有接收书籍、快照或签发官方书籍上传 / 下载链接的入口；官方云、额度 / 用量、商品 / 订单 / 账单、反馈截图及云清理路由和后台模块均不可用，不返回空目录或零配额兼容响应。
- 会员 JSON 仅含 `accountId`、`region`、`tier`、`hasLifetimeFeatures`、`featureExpiresAt`、`deviceLimit`、`loggedInDeviceCount`、`activatedDeviceCount`、`trialAvailable`、`trialClaimed`、`trialClaimedAt`、`trialExpiresAt`；不输出旧云字段，不存在激活码兑换响应协议。
- 精简活动服务不迁移或删除真实数据库、旧账本、已用码和历史云文件；管理员入口保留必要认证、CSRF 与许可 / 安全审计。

同步最低读取协议仍为 6；参与设备应一起升级。保留旧快照及旧对象的只读兼容，不能追溯隐藏旧名字已暴露的元数据。账号服务的 `X-Libzora-Server-Time` 响应头仅供客户端时序校准，代理不得删除或改写。

历史 PostgreSQL 官方云、签名直传及额度计量测试不证明新方案已通过验证，更不能代替真实自备 S3 / WebDAV 验证。新服务端配置、字段及线上迁移方案须另行核验，不能据历史测试承诺自动迁移。

## 4. 问题反馈与日志边界

最小权益验证服务不提供反馈提交、截图上传、反馈附件存储或对应后台模块，也不运行云文件清理。问题可通过 [GitHub Issues](https://github.com/kingstacker/Libzora/issues) 反馈；用户自行检查并决定是否分享脱敏日志或截图，不承诺自动附加上传。

反向代理及应用日志不记录请求体、Authorization 头、密码或邮箱验证码。历史记录和已有文件不因移除反馈 / 云模块或账号停用而自动销毁；本轮不处理真实数据迁移或删除。
