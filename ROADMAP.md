# Roadmap

UniThumb is feature-complete as of the current release. This file documents what exists today and areas that are explicitly out of scope.

## Current features

### Scene and prefab thumbnails

- Capture thumbnails from 2D or 3D scenes (Scene View angle or orbit around bounds) and from prefab assets
- URP and HDRP post-processing enabled automatically when Volume components are present, with no render-pipeline package dependency
- Screen Space Overlay UI capture (on by default)
- Background modes: skybox, solid color, transparent
- Lighting override and layer mask options
- Resolution presets from 16 px to 2048 px, with orbit FOV and framing tightness controls

### Icon overlay

- Draws thumbnails over scene and prefab assets in the Project window
- Configurable: toggle visibility, list padding, max icon size

### Batch operations

- Generate/Clear/Refresh All via Assets menu
- Generate per folder via right-click or window
- Progress display, cancellable with Esc
- Refresh All regenerates only missing thumbnails (existence-only check)

### Settings

- Storage mode: Library (machine-local) or Assets (committable)
- Regenerate on Save (opt-in, requires window open)
- Icon overlay toggle, padding, max size
- Configurable cache limit
- Capture settings persist across editor sessions

### Update checks

- Opt-in via Settings tab toggle (default off)
- Explicit `Tools > UniThumb > Check for Updates` menu action
- Results cached for 24 hours
- No automatic network access

## Explicitly out of scope

- Thumbnail staleness detection: explored during development, removed in favor of opt-in Regenerate on Save
- Runtime components: editor-only tool, nothing ships in builds
- Built-in Render Pipeline: untested (URP and HDRP are supported)

## Test suite

EditMode tests live in `Tests/Editor/`, run from Window > General > Test Runner.
