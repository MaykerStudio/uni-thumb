# Visual Regression Baseline: UniThumb UI/UX Redesign

**Plan**: `20260808-scene-thumbnail-ui-ux`
**Date**: 2026-08-08
**Unity**: 6000.3.11f1 (URP 17.3.0)
**Threshold**: 0.95 (95% similarity)

## Files Analyzed

| File | Lines | Role |
|------|-------|------|
| `UniThumbWindow.cs` | 1318 | Window logic, keyboard shortcuts, batch ETA, result formatting |
| `UniThumbWindow.uxml` | 79 | UI structure, tooltips, view-data-key persistence |
| `UniThumbWindow.uss` | 249 | Theming, flex layout, hover effect, ProgressBar accent |

---

## Intentional Visual Changes (All Verified in Code)

### Wave 1: Layout & Keyboard

| # | Change | File:Line | Before | After | Verification |
|---|--------|-----------|--------|-------|-------------|
| 1 | **Flexible Preview Sizing** | USS:91-99 | Fixed 256x256px box | `width:100%; max-width:256px; aspect-ratio:1/1` | Resize 320-600px, preview scales proportionally |
| 2 | **Preset Row Alignment** | USS:147-149 | `margin-left:120px` | `flex-direction:row; margin-top:6px` | Preset buttons align with other form controls |
| 3 | **Indented Button Alignment** | USS:187-190 | `margin-left:120px` | `align-self:flex-start; margin-top:4px` | "Use current scene's folder" aligns with controls |
| 4 | **Keyboard Shortcuts (Footer)** | CS:553-557 | Folder path only | Folder + `Ctrl+Enter: Generate \| Ctrl+Shift+Enter: Batch \| Esc: Cancel` | Footer text visible in bottom bar |
| 5 | **Escape Key Handler** | CS:1105-1115 | No Escape support | `OnKeyDown` handles `KeyCode.Escape` -> CancelBatch | Press Esc during batch, batch cancels |

### Wave 2: Window Constraints

| # | Change | File:Line | Before | After | Verification |
|---|--------|-----------|--------|-------|-------------|
| 6 | **Window Max Size** | CS:178 | No maxSize | `maxSize = new Vector2(600f, 800f)` | Cannot resize past 600w x 800h |

### Wave 3: Batch UX & Polish

| # | Change | File:Line | Before | After | Verification |
|---|--------|-----------|--------|-------|-------------|
| 7 | **Batch ETA** | CS:803-813 | "Scene X/Y: name" | "Scene X/Y: name \| ~Ns remaining" | Run batch, observe ETA in progress caption |
| 8 | **Refined Result Message** | CS:1074-1082 | "Completed N scenes: X generated, Y failed, Z skipped." | Conditional: only shows "failed"/"skipped" when >0 | Complete batch, verify message omits zero counts |
| 9 | **ProgressBar Accent Color** | USS:205-207 | Default Unity progress color | `.unity-progress-bar__progress { background-color: var(--stt-accent) }` | ProgressBar fill matches theme accent (blue) |
| 10 | **Preview Hover Effect** | USS:101-103 | No hover state | `.stt-preview-box:hover { border-color: var(--stt-accent) }` | Hover over preview box, border turns accent blue |
| 11 | **Tooltips (9 elements)** | UXML:5,26,34-40,42-44,49-53,57,62-65,67,71,77 | No tooltips | tooltip attributes on orbit sliders, browse button, toggles, etc. | Hover each element, read tooltip text |
| 12 | **view-data-key Persistence** | UXML:7,48 | No scroll/foldout state memory | `view-data-key="stt-settings-scroll"` + `view-data-key="stt-bg-effects-foldout"` | Scroll, close window, reopen - scroll position preserved |

---

## Non-Regression Checklist

These areas must NOT have changed (visual diff < 0.05 threshold):

- [ ] **Theme variables**: `.theme-light` and `.theme-dark` color tokens unchanged
- [ ] **Section headers**: Font size 12px, bold, border-bottom, same spacing
- [ ] **Scene path label**: 11px secondary color, same margin
- [ ] **Generate button**: 28px height, same margin
- [ ] **Empty label**: Centered, 11px, same padding
- [ ] **Caption row**: Flex-direction row, centered, same margin
- [ ] **Stale badge**: 10px, same colors/border-radius/padding
- [ ] **Custom resolution row**: Flex row, same field spacing
- [ ] **Orbit controls**: Same margin-top
- [ ] **Preset buttons**: Flex-grow 1, same margins
- [ ] **Foldout border-top**: Same 1px border, same margin/padding
- [ ] **Batch folder row**: Flex row, same alignment
- [ ] **Batch folder field**: Flex-grow 1, min-width 0, same margin
- [ ] **Drag-over state**: Same accent-tint background, dashed border
- [ ] **Generate folder button**: 24px height, same margin
- [ ] **Batch progress section**: Same margin-top
- [ ] **Progress caption**: Same margin-top
- [ ] **Result help**: Same margin-top
- [ ] **Status help**: Same margin/padding
- [ ] **Footer**: Same font-size, color, padding, border-top
- [ ] **Folder picker styles**: All unchanged (search, placeholder, list, row)
- [ ] **Hidden utility**: `display: none` unchanged
- [ ] **Error text**: Same error color variable
- [ ] **Mini/hint text**: Same font-size, color, margin

---

## Dark/Light Theme Verification

Both themes define identical structure with theme-appropriate colors:
- `--stt-accent`: `#0078d7` (light) / `#4f8fff` (dark)
- `--stt-accent-tint`: `rgba(0,120,215,0.12)` (light) / `rgba(79,143,255,0.16)` (dark)
- All hover/active states reference `var(--stt-accent)`, ensuring theme consistency

**Required**: Window must be opened in both Pro (dark) and Personal (light) skins to verify.

---

## Cross-Version Compatibility

| Feature | Unity 2022.3 LTS | Unity 6000.3 |
|---------|------------------|--------------|
| UI Toolkit UXML | Supported | Supported |
| USS custom properties | Supported | Supported |
| `aspect-ratio` CSS | Unity 2022.3+ | Supported |
| `view-data-key` | Supported | Supported |
| `tooltip` attribute | Supported | Supported |
| `ProgressBar` | Supported | Supported |
| `FlexGrow`/`FlexDirection` | Supported | Supported |

**Note**: `aspect-ratio` was introduced in Unity 2022.2. Verify on 2022.3 LTS specifically.

---

## Manual Verification Procedure

1. Open Unity 6000.3.11f1, open any saved scene
2. Open `Window > UniThumb`
3. Verify window opens at default size, cannot resize past 600x800
4. Verify footer shows shortcut documentation
5. Resize window from 320px to 600px width: preview should scale proportionally
6. Hover over preview box: accent border appears
7. Hover over orbit yaw/pitch sliders, browse button, toggles: tooltips appear
8. Generate a thumbnail, verify preview updates
9. Start a batch (Ctrl+Shift+Enter), observe ETA in progress caption
10. Cancel batch with Esc key
11. Complete batch, verify result message format (no zero-count sections)
12. Switch to Pro skin (dark theme), repeat steps 5-11
13. Close and reopen window: scroll position and foldout state should persist

---

## Evidence Paths (for manual capture)

When manual screenshots are taken, store at:
```
Assets/Editor/UniThumb/Evidence/
  baseline-400x600-dark.png
  baseline-400x600-light.png
  hover-preview-dark.png
  batch-progress-dark.png
  batch-result-dark.png
```
