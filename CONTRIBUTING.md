# Contributing to UniThumb

## Prerequisites

- Unity 6000.4.8f1 (or newer) — primary development target
- Unity 2022.3 LTS — optional, for testing the fallback UI
- Git

## Setup

1. Fork and clone the repository
2. Open Unity Hub
3. Click **Open > Add project from disk**
4. Navigate to `Project~/UniThumb Dev` and open it
5. In Unity, go to **Window > Package Manager**
6. Click the **+** button (top-left) > **Add package from disk**
7. Select the `package.json` at the repository root
8. The package is now installed locally — any changes to `Editor/` are reflected immediately

## Project Layout

```
UniThumb (repo root = package root)
├── Editor/           ← all package code (edit these)
├── Project~/         ← Unity dev project (gitignored, never packaged)
├── package.json      ← UPM package manifest
├── README.md
├── CHANGELOG.md
└── LICENSE.md
```

The `Project~/` trailing tilde excludes the dev project from Unity imports and package tarballs. It is purely a local development sandbox.

## Editing

- All package code lives in `Editor/` at the repo root
- Unity auto-reimports when you save, or force refresh via **Assets > Reimport All**
- Run `csharpier format <file>` on any modified `.cs` files after editing

### Dual-UI files

UniThumb supports both Unity 6 and Unity 2022.3 via preprocessor conditionals:

| File | Purpose |
|------|---------|
| `Editor/UniThumbWindow.uxml` | Unity 6 TabView UI |
| `Editor/UniThumbWindow.uss` | Unity 6 TabView styles |
| `Editor/UniThumbWindow.2022.uxml` | Unity 2022.3 fallback UI (header buttons) |
| `Editor/UniThumbWindow.2022.uss` | Unity 2022.3 fallback styles |
| `Editor/UniThumbWindow.cs` | Version-conditional load via `#if UNITY_6000_0_OR_NEWER` |

- Edit `*.uxml` / `*.uss` for Unity 6 UI
- Edit `*.2022.uxml` / `*.2022.uss` for Unity 2022.3 fallback UI
- Edit `*.cs` for version-conditional logic

### Version guards in C#

```csharp
#if UNITY_6000_0_OR_NEWER
    // Unity 6 code
#else
    // Unity 2022.3 fallback code
#endif
```

Existing guards in `UniThumbCapture.cs` (`FindObjectsByType` vs `FindObjectsOfType`) are correct and must not be modified.

## Verification

- Test in `Project~/UniThumb Dev` (Unity 2022.3) — default dev project
- Optionally verify in `Project~/Verify6000` (Unity 6) or `Project~/Verify2023` (Unity 2023.2)
- Check the Unity console for compile errors after changes
- Verify the window opens and renders correctly

## What NOT to edit

- `Project~/` — gitignored, never packaged
- `docs/` — excluded from the package tarball
- `package.json` — only change for version bumps or metadata updates

## Commit Convention

- Commit `Editor/`, `package.json`, `README.md`, `CHANGELOG.md`, `LICENSE.md`, `images/`
- Do not commit `Project~/` or `docs/`
- Keep `Packages/*.json` out of commits
- Run `csharpier format` before committing `.cs` files

## Questions?

Open an issue on the repository.
