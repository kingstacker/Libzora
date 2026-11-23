# Libzora 官网

纯静态站点，无构建依赖即可预览；发布前用脚本重新生成更新日志和版本快照。

## 本地预览

```bash
python3 -m http.server -d website 8765
# http://127.0.0.1:8765/
```

## 发布构建

```bash
./scripts/build_website.sh [输出目录，默认 artifacts/website]
```

输出目录会原地覆盖（不会先清空）；发布前建议传入一个全新的目录。

脚本会：

1. 从 GitHub 最新 Release 刷新 `website/_release.json`；
2. 把最新更新日志写入 `website/index.html`；
3. 输出可部署目录 `artifacts/website/`（不包含 `dev/` 调试页）。

把 `artifacts/website/` 部署到 `libzora.stacker.beauty` 即可。`_release.json` 必须一并部署：页面先显示这份同源快照，再向 GitHub 复核最新稳定版；GitHub API 被限流或暂时不可用时保留快照并显示提示。安装包、校验文件、更新清单和对应标签的源码归档均直链 GitHub，暂不使用本站镜像。macOS 分别提供 Apple Silicon（ARM64）与 Intel（x64）的 DMG；Linux 明确区分 amd64（Intel / AMD x64）和 ARM64。

## 图片资源

```bash
python3 -m pip install Pillow==11.3.0
python3 -I scripts/optimize_website_assets.py
python3 -I scripts/test_optimize_website_assets.py
```

生成各截图的完整 WebP、响应式变体（640w / 1024w）及 1200×630 分享图。`library.png` 与 `library-grid.png` 是两个独立来源，不会再用固定侧栏宽度裁剪并覆盖第二张图；共享品牌图标及 Apple Touch Icon 由 `scripts/generate_app_icons.py` 统一管理。

本次更新的桌面图从 macOS 上实际运行的 Avalonia 应用内容区域直接采集，避开系统窗口边角与阴影合成残留，移动端图截取自 iPhone 17 模拟器；中文页面使用中文界面截图，英文页面使用 `*-en.png` 英文界面截图。截图演示库只包含来自本地 `books/公版书` 目录的六本公版书（The House of the Arrow、The Sign of the Four、The Vultures、The Gold Bag、Hand and Ring、Carmilla）；Kindle 图来自真实连接的 Kindle Scribe，已传入对应 AZW3 文件。演示库不包含账号、连接设置或批注数据；测试用书统一收录于仓库根目录 `公版书/`；临时截图构建不得提交。

## 目录

- `index.html` — 首页（展示 Windows、macOS、Linux、Android 和 iOS 的下载状态）
- `assets/` — 截图与优化后的图片变体
- `privacy.html` / `terms.html` — 隐私政策与用户协议（中英双语同页，`legal.js` 切换语言；应用内“关于”页链接到这两个地址）
- `dev/` — 本地检查用页面（`noindex`，构建产物中排除）
- `styles.css` / `app.js` — 样式与交互

## 下载入口回归

```bash
node --test scripts/test-website-downloads.mjs
```

覆盖全部发布文件与源码归档、macOS DMG 与 Linux 架构区分、GitHub 最新版本复核、限流时保留快照，以及无 JavaScript 构建的 GitHub 直链。

首页下载入口集中在顶部平台按钮中：展开选择安装包或架构；“版本与源码”提供版本日期、Release、校验值、更新清单和源码归档。菜单使用原生 details，关闭 JavaScript 仍可选包下载。
