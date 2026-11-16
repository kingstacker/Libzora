# Contributing to Libzora

Thanks for your interest in improving Libzora. This page describes the branch
model, review flow, and expectations for pull requests.

## Branch model

| Branch | Purpose |
| --- | --- |
| `dev` | Integration branch. **All pull requests target `dev`.** Day-to-day work lands here first. |
| `master` | Release branch. Only the maintainer merges `dev` into it when preparing a release. |

`dev` is the repository's default branch, so clones, forks, and new pull
requests point at it automatically. If you opened a pull request against
`master` by mistake, **switch its base branch to `dev`** before requesting
review.

Releases are produced from `master` by `.github/workflows/release.yml` (on a
push to `master` or a `v*` tag). Changes merged directly into `master` skip
integration testing and can trigger a release, so `master` is protected.

## Workflow

1. Fork the repository and create a topic branch from `dev`:

   ```bash
   git fetch origin
   git checkout -b fix/kindle-reconnect origin/dev
   ```

2. Make a focused change. Add or update tests when behavior changes.
3. Build the app and run the relevant tests locally (see below).
4. Open a pull request with **`dev` as the base branch**. Fill in the template,
   including what changed and how you verified it.
5. A maintainer reviews the change. If adaptation is needed, the maintainer may
   push follow-up commits directly to your branch, so keep **Allow edits by
   maintainers** enabled. The pull request is then merged through GitHub so
   authorship and history are preserved.

Pull requests are merged with a merge commit or rebase; they are not
copy-pasted and closed. This keeps contributor credit and pull request links
intact.

## Build and test

Build the desktop app for your platform:

```bash
# macOS
dotnet build src/Libzora.Desktop.MacOS/Libzora.Desktop.MacOS.csproj

# Windows
dotnet build src/Libzora.Desktop.Windows/Libzora.Desktop.Windows.csproj

# Linux
dotnet build src/Libzora.Desktop.Linux/Libzora.Desktop.Linux.csproj
```

Run the fast test suites:

```bash
dotnet test tests/Libzora.App.Tests/Libzora.App.Tests.csproj
dotnet test tests/Libzora.Tests/Libzora.Tests.csproj
```

Headless UI, rendering, and GC stress tests are tagged `Slow` and excluded by
default. Run them deliberately with:

```bash
dotnet test tests/Libzora.App.Tests/Libzora.App.Tests.csproj -p:RunSlowTests=true
```

## Commit messages

Use short Conventional Commits-style subjects in the imperative mood:

```text
feat(reader): add shelf-to-reader opening transition
fix(macos): finish quitting after closing an open reader
docs: document the contribution workflow
```

Keep one logical change per commit when practical.

## Localization

User-facing strings live in the resource files used by
`src/Libzora.App`. If you add a string, add it for every supported language
rather than hard-coding text in a view.

## License

By contributing, you agree that your contributions are licensed under the
repository's license.

## 简体中文约定

- **所有 PR 的目标分支是 `dev`**（仓库默认分支，GitHub 会自动选中）。如果误开到了
  `master`，提交审查前把 base 改为 `dev`。
- `master` 是发布分支，只由维护者在发版时从 `dev` 合并；直接向 `master` 推送会
  触发发布工作流。
- PR 请保持改动聚焦，行为变化要补测试；本地跑通 `dotnet build` 和相关
  `dotnet test` 后再提交。
- 需要适配时，维护者会直接向你的 PR 分支补提交（请保持 **Allow edits by
  maintainers** 开启），然后通过 GitHub 合并，保留作者署名和 PR 关联。
- 提交信息使用英文、Conventional Commits 风格，例如
  `fix(reader): ...`、`feat(macos): ...`。
