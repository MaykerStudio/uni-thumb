# Changelog

All notable changes to UniThumb are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

## [1.0.0] - 2026-09-13

### Added

Initial release.

- Scene and prefab thumbnails rendered and drawn on assets in the Project window
- Capture settings: resolution presets (16-2048 px, default 128), framing (orbit around bounds or Scene View camera angle, with orbit FOV and tightness controls), background (skybox, solid color, or transparent), lighting override, layer mask, Screen Space Overlay UI capture
- Post-processing supported (URP and HDRP, accessed via reflection with graceful degradation)
- HDR to sRGB readback, corrupt-image detection with fallback render path, downscale retry for very large renders
- Automatic thumbnail invalidation when a scene is saved or modified; opt-in Regenerate on Save
- Batch generate and clear operations with progress display and cancel
- Storage modes: Library cache (default) or tracked in Assets
- Icon overlay with visibility toggle, list padding, and max size settings; configurable cache limit
- Opt-in update checks only; no automatic network access
- Example scenes: `Tools > UniThumb > Generate Example Scenes` writes editable scenes to `Assets/UniThumb/Examples` (no scenes ship with the package)
- Editor-only; no runtime code

[Unreleased]: https://github.com/MaykerStudio/uni-thumb/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/MaykerStudio/uni-thumb/releases/tag/v1.0.0
