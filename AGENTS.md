# Local iOS simulator preference

When building, installing, or opening the iOS app locally, default to the existing
**iPhone 17** simulator (UDID `4A14BDC2-FC9E-4C58-8112-50834D1BC74E`).
Do not substitute iPhone 17 Pro unless the user requests it. Keep the existing
library and app data when updating the installed app. The populated local preview
on this simulator uses bundle ID `com.kingstacker.libzora.nativepreview`.

# Code orientation

Read `docs/code-map.md` before exploring. It maps common tasks to the two or
three files that matter and documents the `MainWindow` partial layout
(Reader, Stage 3, Library, window shell).

# Review and file size

- Keep a source file under ~1,500 lines. When a `MainWindow` partial grows past
  that, split it along its existing feature boundary instead of adding a region.
- Review a diff with `/code-review` before pushing.
- `.github/workflows/review.yml` runs an incremental AI review on pull requests.
  It needs the `ANTHROPIC_API_KEY` repository secret; without it the job fails.
