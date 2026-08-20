# Roadmap: Staleness Detection Improvements

## Current State

`UniThumbFingerprint.cs` captures 5 metrics: ObjectCount, LightCount, MaterialCount, TotalVertexCount, BoundsHash. Primary staleness signal is file timestamp (EditorPrefs `LastWriteTimeUtc` ticks). Secondary signal is fingerprint mismatch.

**Known issues:**
- BoundsHash uses world-space AABB — moving/rotating/scaling an object changes bounds and triggers staleness even when the thumbnail would look identical.
- No user control over sensitivity — binary stale/fresh with no way to configure what counts as "changed."
- Rename-after-save changes file timestamp, marking the thumbnail stale despite identical content.

## Phase 1: Smarter Fingerprint

**Goal:** Eliminate false-positive staleness from cosmetic transforms (position, rotation, scale).

**Changes:**
- **UniThumbFingerprint.cs**: Replace `BoundsHash` (world-space AABB XOR) with a transform-invariant alternative that hashes object hierarchy and material/shader identity instead.
- **UniThumbStorage.cs**: Update `IsSceneStale` and the BatchMenus `IsStale` mirror to use the new fingerprint comparison logic.

**Acceptance criteria:**
- Moving, rotating, or scaling an existing object does NOT trigger staleness.
- Adding or removing an object DOES trigger staleness.
- Changing a material (shader swap, color change, texture swap) DOES trigger staleness.
- Changing light count or type DOES trigger staleness.
- No measurable performance regression on scenes with >1000 objects.

## Phase 2: Sensitivity Presets

**Goal:** Give users control over staleness sensitivity without exposing individual toggles.

**Changes:**
- **UniThumbSettings.cs**: Add `StalenessSensitivity` enum (Strict / Balanced / Relaxed), default `Balanced`.
  - Strict: current behavior — any metric mismatch triggers staleness.
  - Balanced: structural + material changes only (ignores transforms).
  - Relaxed: only ObjectCount changes (add/remove objects).
- **UniThumbStorage.cs / UniThumbBatchMenus.cs**: Read sensitivity setting and select comparison strategy accordingly.
- **UniThumbWindow.cs**: Add sensitivity dropdown to the Settings tab UI.

**Acceptance criteria:**
- Dropdown appears in Settings tab, persists via UniThumbSettings asset.
- Strict mode matches current behavior (regression-safe).
- Balanced mode ignores transform changes.
- Relaxed mode only flags add/remove.
- Switching presets does not require editor restart.

## Phase 3: Individual Signal Toggles

**Goal:** Fine-grained control for power users.

**Changes:**
- **UniThumbSettings.cs**: Add boolean toggle fields per signal (ObjectCount, Material, Light, Transform, VertexCount).
- **UniThumbFingerprint.cs**: Add a `CompareFingerprint(stored, live, settings)` overload that checks only enabled signals.
- **UniThumbWindow.cs**: Add toggle section below the preset dropdown in Settings tab.

**Acceptance criteria:**
- Toggles persist via UniThumbSettings.
- Disabling a signal means changes to that metric never trigger staleness.
- Toggles override preset selection.
- No performance impact when toggles are at defaults.

## Phase 4: Thumbnail Overlay Position

**Goal:** Let users configure where and how the staleness indicator appears on Project window thumbnails.

**Changes:**
- **UniThumbSettings.cs**: Add overlay settings:
  - `OverlayPosition` enum (TopRight / TopLeft / BottomRight / BottomLeft), default `TopRight`.
  - `OverlaySize` float (8-20px range), default `12`.
  - `OverlayOpacity` float (0.3-1.0 range), default `0.9`.
- **UniThumbIconService.cs**: `DrawStaleIndicator` reads position/size/opacity from settings instead of hardcoded constants.
- **UniThumbWindow.cs**: Add overlay controls to the Settings tab (position dropdown, size slider, opacity slider).

**Acceptance criteria:**
- Position dropdown changes where the yellow warning icon draws on thumbnails.
- Size and opacity sliders apply immediately (next Project window repaint).
- Settings persist via UniThumbSettings asset.
- Default behavior matches current (top-right, 12px, 90% opacity).
