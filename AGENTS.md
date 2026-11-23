# AGENTS.md — WindowsTweaks

Standalone Windows 11 desktop tweaking utility and custom shortcut creator built in C# / WPF (.NET Framework 4.x) targeting Windows x64.

## Commands
- Build standalone EXE: `.\src\build.ps1`
- Run app (elevated via UAC): `.\WindowsTweaks.exe`
- Build current Inno installer: `iscc /DMyAppVersion=0.1.0-alpha.1 installer\setup.iss`

## Architecture
- `src/WindowsTweaks.cs` — Main WPF Fluent UI, `TweakEngine` (64-bit registry tweaks, `TweakProfile` JSON import/export), and Win32 `NativeMethods`.
- `src/CustomShortcutCreator.cs` — Embedded helper EXE (`--edit` / `--open`) compiled as a resource inside `WindowsTweaks.exe` for custom `.lnk` shortcut creation.
- `src/ShortcutIntegration.cs` — Shared module that duplicates/impersonates Explorer's unelevated token to install per-user folder context-menu verbs and launch the helper unelevated.
- `src/build.ps1` & `src/app.manifest` — Two-stage inbox `csc.exe` build; embeds `Windows Tweaks.ico` and `CustomShortcutCreator.exe` (`asInvoker`), while `WindowsTweaks.exe` runs as `requireAdministrator`.
- Root `*.reg` & `WinUtil-Advanced.bat` — Standalone registry presets and WinUtil launcher bundled alongside `WindowsTweaks.exe`.
- **Version Manifests:** `installer/setup.iss` (`MyAppVersion` fallback; CI injects version) and `src/app.manifest` (`assemblyIdentity version`).

## Personal Rules (Brain)
- **Commit Format:** Write one plain sentence stating what changed and why; do not use `feat:`/`fix:`/`chore:` conventional-commit prefixes.
- **Commit & Push Policy:** Never run `git commit`, `git tag`, or `git push` unless explicitly asked; never force-push to `main`. When the user says "commit and push", verify the build and tests pass, confirm version manifests are bumped and synced for code/packaging changes, commit on `main`, and push to `origin main` (do not create manual release tags—pushing to `main` triggers `.github/workflows/release.yml` to build with `gloria-setup` and publish to GitHub Releases).
- **Path Privacy:** Always use `C:\Users\you` in documentation, comments, and examples—never hardcode the local machine's real username.
- **Window Chrome:** Disable native OS titlebars; build an integrated frameless titlebar with drag region, double-click maximize, and custom min/max/close controls.
- **Desktop Shell Lock:** Lock the app shell to `100vw x 100vh` (`overflow: hidden`) with internal scroll panes and `user-select: none` on UI chrome.
- **Scrollbars:** Ban raw OS scrollbars; use thin (4–6px) themed scrollbars with transparent tracks and zero layout shift.
- **Interactive Controls:** Custom-style all form controls; define explicit `hover`, `active`, `focus-visible`, and `disabled` states with snappy 120–180ms transitions.
- **Git Branching:** Never create new branches or git worktrees. Always work directly on `main` until explicitly told otherwise.
- **Versioning (Minimal Changes):** When runtime code or packaging changes, bump the alpha number by `+1` (e.g., `0.1.0-alpha.1` -> `0.1.0-alpha.2`) across all version manifests in the repo **at most once per uncommitted change set** (check `git diff HEAD` first—if the working tree version is already ahead of `HEAD`, do not bump again until committed). Skip version bumps on docs-only or read-only tasks.
- **Versioning (Major/Minor Changes):** Before changing minor/major versions or dropping `-alpha` on breaking or major feature work, stop and ask for the exact target version.

## Playbooks (Read Before Task)
- **Building & Packaging:** Read `.brain/building-the-project.md` before packaging, compiling release binaries, or building an installer.
- **GitHub Actions & Releases:** Read `.brain/github-actions.md` before creating/modifying `.github/workflows/*.yml`, setting up release automation, or preparing a first release push.

## Repo Guardrails & Gotchas
- **C# 5 / Inbox Compiler Only:** `src/*.cs` compiles with Windows inbox `.NET Framework 4.0.30319\csc.exe`—never use C# 6+ syntax (`?.`, `$""` interpolation, `nameof`, expression-bodied members) or external NuGet packages.
- **Elevated Main vs. Unelevated Helper:** Keep `WindowsTweaks.exe` elevated (`requireAdministrator`) using `RegistryView.Registry64`, and always use `ShortcutIntegration.cs` Explorer-token impersonation when writing per-user shell verbs or spawning `CustomShortcutCreator.exe`.
- **Two-Stage Build Requirement:** Always rebuild via `.\src\build.ps1` so `CustomShortcutCreator.exe` is recompiled and embedded into `WindowsTweaks.exe`; do not compile `src/WindowsTweaks.cs` standalone.

## Done Criteria
Run `.\src\build.ps1` and confirm zero compiler errors and an updated root `WindowsTweaks.exe`. Verify version manifests are updated/synced when code or packaging changes.

<!-- brain-sync: 8d80a5ed2479 -->
