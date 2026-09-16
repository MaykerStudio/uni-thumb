# AGENTS.md

Unity 6000.4.8f1 (Unity 6), URP, Linear color space. The project is a single Editor-only tool: UniThumb (namespace `MaykerStudio.UniThumb`).

## Layout

```
Editor/  - all package code: UniThumb.Editor assembly (`includePlatforms: Editor`), UniThumbWindow.uxml/.uss
Tests/   - EditMode tests: UniThumb.Editor.Tests assembly (~500 cases across 40 files as of v1.0.0); internals exposed to tests via Editor/AssemblyInfo.cs
Project~ - two Unity dev projects (gitignored, never packaged): 'UniThumb 6 URP' (Unity 6000.4.8f1, primary) and 'UniThumb Dev 6 2D URP' (Unity 2022.3.19f1, fallback-UI checks)
```

- The repository root IS the UPM package root: `package.json` at root, plus `CHANGELOG.md`, `README.md`, `LICENSE.md`, `Documentation~/images/`.
- `Assets/UniThumb/` inside each dev project is runtime-created, not part of the package: it holds the tool's settings asset (`UniThumbSettings.asset`, auto-created) and generated example scenes. Each dev project's `Packages/manifest.json` pins `com.maykerstudio.unithumb` to the repository root as a local file dependency.
- The package ships no example scenes. Example scenes are created on demand: `Tools > UniThumb > Generate Example Scenes` writes 10 scenes to `Assets/UniThumb/Examples/` in the consuming Unity project (here: either dev project under `Project~/`).
- **Core asmdef keeps zero SRP refs** (`references` holds only `UnityEngine.UI`, auto-referenced). Hybrid pipeline access: URP through the constraint-gated `UniThumb.UrpShim.Editor` shim (defineConstraints HAS_URP, refs Universal.Runtime + Core.Runtime; core reaches it ONLY via the `UniThumbUrp` narrow reflection dispatch under `#if HAS_URP`, never `using Universal`) + Core data types direct under HAS_SRP_CORE, narrow dynamic for HDRP frame-settings and VFX (cached, once-log) plus Light2D-absent fail-open guards; no `package.json` deps; Built-in compiles via `#else` fallbacks (out of scope per 20260910 decision: URP + HDRP only, no Built-in proof).
- Tests live in `Tests/Editor/` (EditMode suite, ~500 cases as of v1.0.0; run via Window > General > Test Runner). No runtime code - the whole project is editor tooling.

## Component map (data flow)

`UniThumbWindow` (UI Toolkit window) -> `UniThumbGuard` (re-entrancy: `TryEnter`/`Exit`/`IsGenerating`) -> `UniThumbCapture` (renders PNG) -> `UniThumbStorage` (saves + caches) -> `UniThumbIconService` (Project window overlay).

All components live in root `Editor/`.

- **Window**: `UniThumbWindow.cs` + `.uxml` + `.uss`. UI Toolkit, theme via `.theme-light`/`.theme-dark` classes on root. Subscribes `EditorSceneManager.sceneSaved`, `activeSceneChangedInEditMode`, `EditorApplication.hierarchyChanged`, and `Undo.undoRedoPerformed` in OnEnable/OnDisable for live preview refresh and Regenerate on Save. Menu: `Window/UniThumb`.
- **Capture**: `UniThumbCapture.cs` - static API, creates temp `Camera` + RenderTexture, never renders through the SceneView camera. Handles framing (Scene View angle or orbit around bounds), skybox/solid-color, lighting override, post-processing (reflection), HDR->sRGB readback, corrupt-image detection with `SubmitRenderRequest` fallback, downscale retry above 16M px.
- **Storage**: `UniThumbStorage.cs` - writes `{guid}.png` per scene or prefab into the ACTIVE mode folder: `Library/SceneThumbnails/` (`LibraryCache`: outside Assets, no `.meta`, no import) or `Assets/UniThumb/Thumbnails/` (`TrackedInAssets`: `.meta` sidecars, `AssetDatabase` deletes). Mode is read from `UniThumbSettings` at call time, never cached; `MoveThumbnailsTo` migrates folders on mode switch. Prefabs use the GUID-keyed API (`SavePrefabThumbnail`/`LoadPrefabThumbnail`/`HasPrefabThumbnail`/`DeleteByGuid`) sharing the same folder, ownership, and LRU rules. Invalidation uses asset-file `LastWriteTimeUtc` ticks; LRU size cap comes from settings.
- **Batch**: `UniThumbBatchMenus.cs` - `Assets/` menu items (Generate/Clear/Refresh All/Generate Folder, priorities 1100-1103). Two-pass `EditorApplication.update` pump; `CollectFolderScenePaths(folderPath)` is the public folder-scene collector, `CollectFolderPrefabPaths` the prefab counterpart.
- **Prefab capture**: `UniThumbCapture.CapturePrefab` - stages each prefab apart from scene content and reverts through Undo (no Prefab Stage needed). Save-triggered regen via `UniThumbPrefabSaveProcessor` -> `UniThumbPrefabAutoRegen` (armed only when the setting is on AND the window is open); all prefab paths reuse the Guard -> Capture -> Storage -> IconService chain.
- **Icon overlay**: `UniThumbIconService.cs` - draws thumbnails over Project window items via `EditorApplication.projectWindowItemOnGUI`; `ApplyIcon`/`ClearIcon`/`ReapplyAllIcons`.
- **Folder menu**: `UniThumbFolderMenu.cs` - Project window folder context menu; shares `UniThumbWindow.uss`.
- **Settings**: `UniThumbSettings.cs` - tool settings asset auto-created at `Assets/UniThumb/UniThumbSettings.asset` (in the dev project).
- **Example scenes**: `ExampleSceneGenerator.cs` - `Tools/UniThumb/Generate Example Scenes` recreates 10 scenes procedurally at `Assets/UniThumb/Examples/` (target folder is `k_ScenesFolder`).

## Ownership rules (critical - violations corrupt the UI)

- `UniThumbStorage` is the **sole owner** of every `Texture2D` it returns. Never `DestroyImmediate` a texture you got from it - only drop the reference. Eviction/Delete destroys them.
- UI refresh paths (repaint, icon overlay) must use only `TryGetCachedTexture` (zero I/O). `Load`/`HasThumbnail` are for mutation points only (after generate/save, menu validation).
- `UniThumbGuard` must wrap every generate/delete/batch flow: `TryEnter` -> try/finally `Exit`. Never hold the guard across a modal dialog (enter after user confirms).

## Features and gotchas

- **Capture UI toggle** (`_captureUi`, default true): `UiCaptureSession` (nested in `UniThumbCapture.cs`) temporarily switches Screen Space Overlay canvases to the capture camera so UI renders into the thumbnail, restoring everything in `finally`. Only touches `ScreenSpaceOverlay` canvases. Both `CaptureCore` and `RenderLivePreview` use it.
- **Delete flows**: bottom bar has Delete Thumbnail (active scene) and Batch card has Clear Folder Thumbnails (confirm dialog, counts via `HasThumbnail` before dialog, `ReapplyAllIcons` once after loop). After any delete, call `MarkPreviewDirty()` or the live preview stays hidden ("No thumbnail yet") - this bit us once.
- **Default resolution** is 128x128 (`_resolutionIndex = 3` in `k_PresetResolutions {16,32,64,128,256,512,1024,2048}`).
- **Refresh All**: regenerates only scenes missing a thumbnail (existence-only check via `HasThumbnail`); no staleness or fingerprint comparison involved.
- **USS constraints in Unity 6000.4**: no `box-shadow` (unsupported), no `filter: drop-shadow()` (unsupported despite docs), `aspect-ratio` must use plain `1` not `1/1` (the `/` is a hard parse error that silently kills the whole stylesheet import), no `color-mix()` (warnings only). `gap` produces warnings but works. A hard USS error aborts the entire import - check console for "UniThumbWindow.uss" errors after USS edits.
- **Foldout gotcha**: UI Toolkit Foldout children land in the content container, never the header. Styling the foldout header text requires reaching the internal Toggle (`foldout.Q<Toggle>()`, text at `toggle[0][1]`). We removed the B&F foldout entirely - all sections are plain cards now.
- **Icons**: section header icons come from `EditorGUIUtility.IconContent(...)` in C# (`ApplySectionIcons`), verified per-Unity-version - icon names vary across versions, check with `EditorGUIUtility.IconContent(name)?.image != null` before relying on one. Icon+label alignment: keep the border-bottom on the row, not the label, or icons misalign.
- **EnumField**: UXML EnumFields are untyped; must call `_bgModeField.Init((Enum)value)` in `PushState` (no `EnumType` property in Unity 6000.4).
- **TabView vertical fill (Unity 6000.4.8f1)**: five `flex-grow: 1` rules needed for inner ScrollViews to fill the window: `TemplateContainer`, `.unity-tab-view`, `.unity-tab-view__content-container`, `.unity-tab`, `.unity-tab__content-container`. The UXML TemplateContainer root defaults to flex-grow: 0, so without them the whole chain is content-sized (short Settings tab collapsed to 86.4px). The original three-rule chain (TabView -> content-container -> .stt-scroll) was insufficient.
- **USS child combinator**: `.unity-tab-view__content-container > .unity-tab` did NOT apply at runtime in 6000.4.8f1 (resolved flex-grow stayed 0); use plain class selectors (`.unity-tab`).
- **Hidden tab content**: EnumField.Init((Enum)value) and SetValueWithoutNotify run correctly on display:none (inactive tab) content in 6000.4.8f1 - the element is attached to the panel; hidden-tab state initialization needs no special handling. Storage-mode change DisplayDialog also works from a hidden tab (view-independent).
- **Shared stylesheet scope**: UniThumbWindow.uss is loaded by both UniThumbWindow and UniThumbFolderMenu; a `TemplateContainer { flex-grow: 1 }` rule applies to both consumers (safe here - both are editor windows where a stretching root is correct). Any future shared-stylesheet rule with window-specific intent must consider all consumers.

## Verification workflow

- After editing `.cs`/`.uss`/`.uxml`, reimport via Unity (AssetDatabase refresh or editor) and check `read_console` for errors. A stale import can show old styles - force reimport of the specific asset if resolved styles look wrong.
- Run `csharpier format <file>` on every modified `.cs` (note: **`format` subcommand is required**, bare path fails).
- Verify UI state via live `resolvedStyle`/class checks (execute_code) rather than screenshots; the available vision model is unreliable.
- Package-internal paths resolve via `UniThumbPackagePaths` (`Editor/UniThumbPackagePaths.cs`), which uses `PackageInfo.FindForAssembly` to compute the project-relative package path. The package root is the repo root; each dev project's `Packages/manifest.json` pins it as a local file dependency, and inside a dev project the package resolves to Unity's installed `Packages/` asset-path form. UXML/USS are not loaded from `Assets/Editor/UniThumb`.
- Staging: follow the Commit Convention in `CONTRIBUTING.md` (single owner of the committed-file list); `Project~/`, `docs/`, and `Packages/*.json` stay out - ask if unsure. AGENTS.md stays at the root and is excluded from the package tarball (pack policy).
- Release: bump `version` in `package.json` + add a `CHANGELOG.md` entry, commit on main, push tag `vX.Y.Z` matching the version exactly (`release.yml` rejects mismatches). CI builds the tarball via `scripts/pack-unithumb.ps1` and attaches it to the GitHub Release; `UniThumbUpdateChecker` polls `uni-thumb` releases, so the tarball must be attached there.


