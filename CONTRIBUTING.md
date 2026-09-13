# Contributing to UniThumb

## Prerequisites

- Git
- Unity 6 (or newer) - primary development target
- Unity 2022.3 LTS - optional, for verifying the unified UI (single layout/stylesheet on 2022.3 and Unity 6)

## Setup

1. Fork and clone the repository
2. Open (or create) any Unity 2022.3+ project
3. In that project, open **Window > Package Manager**, click **+**, and choose **Add package from disk**
4. Select the cloned repository root (it contains `package.json`)
5. Alternatively, add a `file:` entry pointing at the clone to that project's `Packages/manifest.json`
6. Changes to `Editor/` are picked up immediately - the consuming project reimports on save or AssetDatabase refresh

## Project Layout

```
UniThumb (repo root = package root)
├── Editor/           ← all package code (edit these)
├── Tests/            ← EditMode tests (Test Runner)
├── package.json      ← UPM package manifest
├── README.md
├── CONTRIBUTING.md
├── CHANGELOG.md
└── LICENSE.md
```

## Editing

- All package code lives in `Editor/` at the repo root
- Unity auto-reimports when you save, or force refresh via **Assets > Reimport All**
- Run `csharpier format <file>` on any modified `.cs` files after editing (the `format` subcommand is required)

### Dual-UI files

UniThumb supports both Unity 6 and Unity 2022.3 via preprocessor conditionals:

| File | Purpose |
|------|---------|
| `Editor/UniThumbWindow.uxml` | UI layout (shared across Unity versions) |
| `Editor/UniThumbWindow.uss` | UI styles (shared across Unity versions) |
| `Editor/UniThumbWindow.cs` | Window logic |

- Edit `.uxml` / `.uss` for UI layout and styles
- Edit `.cs` for window logic

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

- Run the EditMode test suite (~74 tests):
  - In Unity: **Window > General > Test Runner**, select the **EditMode** tab, click **Run All**
    (tests are discoverable because the package lists itself in `testables` in `package.json`)
  - CLI alternative (adjust the editor path for your install):
    ```
    "<path-to-Unity>" -batchmode -projectPath "<path-to-your-test-project>" -runTests -testPlatform EditMode -testResults "<repo>/results.xml" -logFile "<repo>/tests.log"
    ```
    Exit code 0 means all tests passed; details land in the results XML.
- Check the Unity console for compile errors after changes
- Verify the window opens and renders correctly (**Window > UniThumb**)

## AI-assisted contributions

AI assistance is welcome. State in the PR where it was used. Show first-hand understanding of the change:

- Report the test run and its result (see Verification)
- Check behavior in the editor instead of trusting the diff
- Answer review questions from understanding, not with pasted output

## Release tarball

Build with:

```powershell
pwsh ./scripts/pack-unithumb.ps1
```

Output lands in `dist/`; `-DryRun` previews without writing.

## What NOT to edit

- `Project~/` — gitignored, never packaged
- `docs/` — excluded from the package tarball
- `package.json` — only change for version bumps or metadata updates

## Commit Convention

- Commit `Editor/`, `Tests/`, `.github/`, `package.json`, `README.md`, `CONTRIBUTING.md`, `CHANGELOG.md`, `LICENSE.md`, `ROADMAP.md`, `Documentation~/images/`
- Do not commit `docs/` or other gitignored local folders
- Keep `Packages/*.json` out of commits
- Run `csharpier format` before committing `.cs` files

## Questions?

Open an issue on the repository.
