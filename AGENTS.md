# AGENTS.md

Unity 6000.4.8f1 (Unity 6), URP, Linear color space. The project is a single Editor-only tool: UniThumb (namespace `MaykerStudio.UniThumb`).

## Layout

- All code lives in `Assets/Editor/UniThumb/` — one assembly `UniThumb.Editor` (`includePlatforms: Editor`).
- **The asmdef has `"references": []`** — `UnityEngine.UI` (UGUI) is auto-referenced, but URP APIs are NOT. URP access (e.g. `UniversalAdditionalCameraData`) goes through **reflection** in `UniThumbCapture.cs` (`TryEnablePostProcessing`). Do not add asmdef references for URP.
- No tests exist anywhere. No runtime code — the whole project is editor tooling.

## Component map (data flow)

`UniThumbWindow` (UI Toolkit window) -> `UniThumbGuard` (re-entrancy: `TryEnter`/`Exit`/`IsGenerating`) -> `UniThumbCapture` (renders PNG) -> `UniThumbStorage` (saves + caches) -> `UniThumbIconService` (Project window overlay).

- **Window**: `UniThumbWindow.cs` + `.uxml` + `.uss`. UI Toolkit, theme via `.theme-light`/`.theme-dark` classes on root. Menu: `Window/UniThumb`.
- **Capture**: `UniThumbCapture.cs` — static API, creates temp `Camera` + RenderTexture, never renders through the SceneView camera. Handles framing (Scene View angle or orbit around bounds), skybox/solid-color, lighting override, post-processing (reflection), HDR->sRGB readback, corrupt-image detection with `SubmitRenderRequest` fallback, downscale retry above 16M px.
- **Storage**: `UniThumbStorage.cs` — PNGs in `Library/SceneThumbnails/{sceneGuid}.png` (GUID-named, outside Assets, no `.meta`). EditorPrefs invalidation keys `SceneThumbs.v4.{guid}` store scene last-write ticks as invariant-culture strings.
- **Batch**: `UniThumbBatchMenus.cs` — `Assets/` menu items (Generate/Clear/Refresh All/Generate Folder, priorities 1100-1103). Two-pass `EditorApplication.update` pump; `CollectFolderScenePaths(folderPath)` is the public folder-scene collector.
- **Icon overlay**: `UniThumbIconService.cs` — draws thumbnails over Project window items via `EditorApplication.projectWindowItemOnGUI`; `ApplyIcon`/`ClearIcon`/`ReapplyAllIcons`.
- **Example scenes**: `ExampleSceneGenerator.cs` — `Tools/UniThumb/Generate Example Scenes` recreates 10 scenes in `Assets/Scenes/`.

## Ownership rules (critical — violations corrupt the UI)

- `UniThumbStorage` is the **sole owner** of every `Texture2D` it returns. Never `DestroyImmediate` a texture you got from it — only drop the reference. Eviction/Delete destroys them.
- UI refresh paths (repaint, icon overlay) must use only `TryGetCachedTexture` (zero I/O). `Load`/`HasThumbnail` are for mutation points only (after generate/save, menu validation).
- `UniThumbGuard` must wrap every generate/delete/batch flow: `TryEnter` -> try/finally `Exit`. Never hold the guard across a modal dialog (enter after user confirms).

## Features and gotchas

- **Capture UI toggle** (`_captureUi`, default true): `UiCaptureSession` (nested in `UniThumbCapture.cs`) temporarily switches Screen Space Overlay canvases to the capture camera so UI renders into the thumbnail, restoring everything in `finally`. Only touches `ScreenSpaceOverlay` canvases. Both `CaptureCore` and `RenderLivePreview` use it.
- **Delete flows**: bottom bar has Delete Thumbnail (active scene) and Batch card has Clear Folder Thumbnails (confirm dialog, counts via `HasThumbnail` before dialog, `ReapplyAllIcons` once after loop). After any delete, call `MarkPreviewDirty()` or the live preview stays hidden ("No thumbnail yet") — this bit us once.
- **Default resolution** is 128x128 (`_resolutionIndex = 3` in `k_PresetResolutions {16,32,64,128,256,512}`).
- **Prefix bumps**: changing capture defaults that alter thumbnail look requires bumping `k_PrefsPrefix` ("SceneThumbs.v4") in **BOTH** `UniThumbStorage.cs` and the mirror in `UniThumbBatchMenus.cs` (comment says keep in sync). This makes old thumbnails stale so Refresh All regenerates.
- **Staleness**: `UniThumbStorage.IsSceneStale` returns false when the prefs key is missing (UI badge path); the Refresh All migration check is BatchMenus' private `IsStale`.
- **USS constraints in Unity 6000.4**: no `box-shadow` (unsupported), no `filter: drop-shadow()` (unsupported despite docs), `aspect-ratio` must use plain `1` not `1/1` (the `/` is a hard parse error that silently kills the whole stylesheet import), no `color-mix()` (warnings only). `gap` produces warnings but works. A hard USS error aborts the entire import — check console for "UniThumbWindow.uss" errors after USS edits.
- **Foldout gotcha**: UI Toolkit Foldout children land in the content container, never the header. Styling the foldout header text requires reaching the internal Toggle (`foldout.Q<Toggle>()`, text at `toggle[0][1]`). We removed the B&F foldout entirely — all sections are plain cards now.
- **Icons**: section header icons come from `EditorGUIUtility.IconContent(...)` in C# (`ApplySectionIcons`), verified per-Unity-version — icon names vary across versions, check with `EditorGUIUtility.IconContent(name)?.image != null` before relying on one. Icon+label alignment: keep the border-bottom on the row, not the label, or icons misalign.
- **EnumField**: UXML EnumFields are untyped; must call `_bgModeField.Init((Enum)value)` in `PushState` (no `EnumType` property in Unity 6000.4).

## Verification workflow

- After editing `.cs`/`.uss`/`.uxml`, reimport via Unity (AssetDatabase refresh or editor) and check `read_console` for errors. A stale import can show old styles — force reimport of the specific asset if resolved styles look wrong.
- Run `csharpier format <file>` on every modified `.cs` (note: **`format` subcommand is required**, bare path fails).
- Verify UI state via live `resolvedStyle`/class checks (execute_code) rather than screenshots; the available vision model is unreliable.
- Staging/commit convention: user prefers committing "everything except `docs/` and `Packages/*.json`" — ask if unsure.
