# Changelog

All notable changes to UniThumb are documented in this file.

## [Unreleased]

- Added Unity 2022.3 LTS support (fallback header-button UI when TabView is unavailable)
- Unity 6 (6000.4+) retains the original TabView-based UI
- Fixed UGUI compile failure on Unity 6 by adding explicit `UnityEngine.UI` asmdef reference

## [1.0.0] - 2026-08-16

Initial release.

- Scene thumbnails rendered and drawn on scene assets in the Project window
- Capture settings: resolution presets (16-512 px, default 128), framing (orbit around scene bounds or Scene View camera angle), background (skybox or solid color), lighting override, Screen Space Overlay UI capture
- Post-processing supported (URP, accessed via reflection with graceful degradation)
- HDR to sRGB readback, corrupt-image detection with fallback render path, downscale retry for very large renders
- Automatic thumbnail invalidation when a scene is saved or modified
- Batch generate and clear operations with progress bar and cancel
- Storage modes: Library cache (default) or tracked in Assets
- Example scenes: `Tools > UniThumb > Generate Example Scenes` writes 10 editable scenes to `Assets/UniThumb/Examples` (no scenes ship with the package)
- Editor-only; no runtime code
