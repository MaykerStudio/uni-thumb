# UniThumb

UniThumb is an editor tool for Unity that renders thumbnail images of scenes and draws them on the scene assets in the Project window, so scenes are visually identifiable at a glance.

![Scene thumbnails in the Project window](images/thumbnails-comparison.png)

## Features

- Renders a thumbnail of any scene and draws it on the scene asset in the Project window
- Resolution presets: 16, 32, 64, 128, 256, 512 px (default 128)
- Framing modes: orbit around the scene bounds, or reuse the Scene View camera angle
- Background options: skybox or solid color
- Optional lighting override
- Optional capture of Screen Space Overlay UI into the thumbnail
- Post-processing supported when URP is present (accessed via reflection)
- HDR to sRGB readback with corrupt-image detection and a fallback render path
- Automatic downscale retry for very large renders
- Thumbnails invalidate automatically when a scene is saved or modified
- Batch operations with a progress bar and cancel
- Editor-only: no runtime code

## Requirements

- Unity 2022.3 LTS or newer (Unity 6 recommended for the original TabView UI)
- Any render pipeline (Built-in, URP, HDRP); no render pipeline package is required
- UGUI is required (the tool captures and generates UI via CanvasScaler, GraphicRaycaster, Image, Text) and is included with every standard Unity project; no extra install needed
- Editor-only tool; nothing ships in builds

URP post-processing is enabled via reflection when URP is present; a missing URP degrades gracefully instead of failing compilation.

## Installation

### Git URL

Window > Package Manager > + > Add package from git URL, then enter:

```
https://github.com/MaykerStudio/uni-thumb.git
```

The package lives at the repository root, so no path suffix is needed.

### Tarball

Package the repository into a `.tgz` (scripts/pack-unithumb.ps1 does this; `npm pack` also works), then Window > Package Manager > + > Add package from tarball... and select the archive.

### Embedded (development only)

The dev project under `Project~/UniThumb Dev` installs the root package via the git URL `https://github.com/MaykerStudio/uni-thumb.git`, or a dev-only `file:` reference when testing local changes. The trailing `~` keeps the folder out of Unity imports and package tarballs; the whole tree is gitignored and not part of the package.

## Usage

Open the window via `Window > UniThumb`. The window has two sections. On Unity 6 these are native TabView tabs; on Unity 2022.3 they are header buttons that switch between the Scene Thumbnail and Settings sections:

- **Scene Thumbnail**: capture settings, a live preview, and generate/delete actions for the active scene
- **Settings**: storage mode and related options

![UniThumb window](images/ui-main-window.png)

Note: screenshots show the Unity 6 (TabView) layout. Unity 2022.3 uses a header-button switcher instead of tabs.

Keyboard shortcuts (inside the window):

- `Ctrl+Enter` (`Cmd+Enter` on macOS): generate a thumbnail for the current scene
- `Ctrl+Shift+Enter`: generate thumbnails for all scenes in the batch folder
- `Esc`: cancel a running batch

Getting started:

1. Open the window via `Window > UniThumb`.
2. With a scene open, generate a thumbnail for the current scene from the window.
3. Or select a scene asset in the Project window and use `Assets > Generate UniThumb`.
4. The thumbnail appears as an icon on the scene asset in the Project window.

The window also has a batch section: choose a folder (or use the current scene's folder), generate thumbnails for all scenes in it, or clear a folder's thumbnails after confirmation. Batch operations show a progress bar and can be cancelled.

![Batch generation](images/ui-batch.png)

### Menu reference

| Menu item | Description |
| --- | --- |
| `Window > UniThumb` | Opens the UniThumb window |
| `Assets > Generate UniThumb` | Generates a thumbnail for the selected scene asset |
| `Assets > Clear UniThumb` | Deletes the thumbnail of the selected scene asset |
| `Assets > Refresh All UniThumbs` | Regenerates all thumbnails, including stale ones |
| `Assets > Generate UniThumbs in Folder` | Generates thumbnails for all scenes in a selected folder |
| `Tools > UniThumb > Generate Example Scenes` | Recreates 10 example scenes in `Assets/UniThumb/Examples`, useful for evaluating the tool |

## Storage

Thumbnails are PNG files named after the scene GUID, so there are no `.meta` files and no Project window pollution. No texture assets are created; the overlay is drawn by the tool, so there are no import settings and no disk bloat.

Two storage modes, selected in the Settings section:

- **Library cache** (default): `Library/SceneThumbnails/{sceneGuid}.png`, outside `Assets`. Machine-local and regenerable; deleting `Library/SceneThumbnails/` and running `Assets > Refresh All UniThumbs` regenerates everything.
- **Tracked in Assets**: `Assets/UniThumb/Thumbnails/`. Thumbnails can be committed to Git to share them with the team.

Switching modes moves existing thumbnails to the new location.

## Examples

The package ships no example scenes. Example scenes are created on demand: run `Tools > UniThumb > Generate Example Scenes` and the generator writes 10 editable scenes to `Assets/UniThumb/Examples` in your project, where they can be opened, edited, and committed. The folder is created if it does not exist.

## Repository layout

The repository root is the package root, so the plain Git URL installs the package as-is:

```
root/                    <- the UPM package (package.json at the root)
  package.json           package manifest (name, version, dependencies)
  Editor/                UniThumb.Editor assembly: 10 .cs files, asmdef, uxml/uss
  README.md              this document (product + repository)
  CHANGELOG.md           version history
  LICENSE.md             BSD 3-Clause license
  images/                README screenshots
  scripts/               pack-unithumb.ps1 (tarball build)
  Project~/UniThumb Dev/ Unity dev project (gitignored via the trailing ~; installs the package via git URL)
  dist/                  tarball output (gitignored)
  docs/                  plan artifacts, never shipped
```

## Development

- The Unity dev project lives in `Project~/UniThumb Dev` and installs the root package via the git URL `https://github.com/MaykerStudio/uni-thumb.git` (or a dev-only `file:` reference when testing local changes). The trailing `~` excludes the folder from Unity imports and package tarballs.
- scripts/pack-unithumb.ps1 packs the repo root into `dist/com.maykerstudio.unithumb-<version>.tgz` (npm pack primary, tar and zip fallbacks). It excludes the Project/ and Project~/ dev trees, docs/, scripts/, dist/, AGENTS.md, and all .meta/.unity/.prefab files; images/ is included so README images resolve in installed packages.
- Root package files (package.json, Editor/, README.md, CHANGELOG.md, LICENSE.md, images/) are committed; Project~/, docs/, and Packages/*.json stay out of commits.

## License

BSD 3-Clause License. Copyright (c) 2026, MaykerStudio. See LICENSE.md for details.
