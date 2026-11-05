# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

**Smart Colliders** (`com.reclabs.smart.colliders`) — personal Unity Editor tool for automatic collider generation/editing. Feature reference: Asset Store "Collidify" (Bitwise Games). Original implementation — never copy code, icons or exact visual identity from it or any commercial package.

This Unity project is only the **dev host**. All tool code lives in the embedded UPM package `Packages/com.reclabs.smart.colliders/` (consumed by other projects via git URL). Nothing tool-related goes in `Assets/` except test/demo scenes and sample meshes.

## Environment

- Unity **6000.3.17f1** (min supported 6000.3), URP 17.3. Physics: default PhysX 3D + Box2D 2D.
- Package deps: Burst, Mathematics, Collections (heavy geometry runs in Burst jobs).
- Assemblies: `RecLabs.SmartColliders` (Runtime: authoring components, data, **pure geometry** in `Runtime/Geometry`), `RecLabs.SmartColliders.Editor` (UI, tools, baking, async orchestration), `RecLabs.SmartColliders.Editor.Tests` (EditMode). Internals visible to Editor/Tests via `AssemblyInfo.cs`.
- Avoid a namespace segment named `Editor` for new code (clashes with `UnityEditor.Editor`); editor code uses `RecLabs.SmartColliders.EditorTools` (not `.Tools`: clashes with `UnityEditor.Tools`) (operations, bake) / `.UI` (inspectors, gizmos, windows).

## Authoring model

- `SmartColliderAuthoring` (runtime MonoBehaviour, editor-only data, stripped from scenes in player builds by `StripAuthoringOnBuild`) holds `parts`, `fitSettings`, bake options and bake bookkeeping (`bakedColliders`/`bakedObjects` — re-bake/clear only touch these).
- `SmartColliderPart` = triangle subset (`TriangleRanges`: (start,count) pairs over the combined source triangle list) + **all 4 fitted candidates and reports** cached → type switch is instant. Shapes are in the authoring transform's local space.
- `SourceGeometry` (Editor) merges MeshFilters + baked SkinnedMeshRenderers (children optional) into authoring-local space; `Signature()` detects stale parts.
- `SmartColliderOperations` (Generate Single/PerMesh, Refit, SetType, SetAuto) and `SmartColliderBaker` (Bake, ClearBaked, BakeAndRemoveAuthoring) are the only mutation entry points; each is one Undo group. UI must call these, never mutate fields directly.
- `SmartColliderRefine` = edit ops (FitToMesh keeps orientation, AlignToAxes, ResetParts, Reevaluate) + low-level Translate/Rotate/SetShape/ScaleHull used by handles (caller records Undo). Edited shapes may not enclose the mesh → `FitMetrics.EvaluateEdited` (exact volumes + intersection sampled inside the shape). Any manual edit sets `autoType = false`.

## Scene view (Editor/UI)

- `ColliderPreviewRenderer` (`SceneView.duringSceneGui`, Repaint only) draws every selected authoring: fill via `Hidden/SmartColliders/Preview` (`Editor/Shaders`, pass 0 visible / pass 1 occluded x-ray, `Graphics.DrawMeshNow` ⇒ pipeline-agnostic) + `Handles.DrawLine` wires. Unit meshes + swept hull-mesh cache in `PreviewMeshes`. Colors/sizes only from `SmartCollidersTheme`; prefs in `PreviewPrefs` (EditorPrefs).
- `SmartColliderEditTool` (`EditorTool` for `SmartColliderAuthoring`): default control steals clicks for `PartPicker` (analytic raycast, no physics); modes Select/Move/Rotate/Resize (Unity `*BoundsHandle`s, hulls scale from a drag snapshot); metrics re-evaluated once on drag end (`recordUndo: false`, folded into the edit's Undo).
- `PartSelection` = session-only selection per authoring (sanitized against part count).
- `SceneHud` (UI Toolkit in `sceneView.rootVisualElement`, styled by `SmartCollidersHud.uss`, vector icons in `HudIcons` via Painter2D) = metrics card + 40px icon toolbar anchored under the selected parts, shown only while the tool is active. GUI→panel coords via `GUIToScreenPoint − sceneView.position`.
- Tool claims Unity commands while parts are selected: `SoftDelete/Delete` → remove parts (not the GameObject), `SelectAll`, `FrameSelected` (frames parts); keys 1–5 = modes (5 = Paint), X = Local/Global, M = merge, G = Generate popover, Esc = deselect / clear paint.
- Paint mode: `PaintSession` (cached source geometry, painted triangle set per authoring, brush via `MeshQueries.Raycast/Gather` Burst jobs) → `SmartColliderEditTool.SplitPaint` → `SmartColliderOperations.AssignTriangles`. `SceneHud` has Rotate (±90/field per axis) and Paint subbars; `GeneratePopover` (G) edits mode/quality/max/merge/hull verts.
- `DecompositionOverlay` = Scene view progress card (pause/resume/cancel) driven by `DecompositionRunner.Changed`.
- Entry point for users: Hierarchy context menu `GameObject/Smart Colliders/Generate Colliders` (adds component, generates per mesh, activates tool).
- Batchmode tests can't run while the project is open in the Editor → use the **test host** `Y:/Github-PA/SmartColliders-TestHost` (references this package by file path): `run-tests.sh [filter]`, `failures.ps1`, `capture.sh <scenario>` (GUI screenshot via `Assets/Editor/CaptureHarness.cs` + `CaptureScenarios.cs`: hud, hud_rotate, hud_paint, hud_generate, hud_progress, window, window_decompose, window_bake, window_batch, window_empty, inspector → `Captures/*.png`). A compile error makes `capture.sh` hang until its timeout — run `run-tests.sh` first. Never kill Unity by image name (the user's Editor is usually open): kill the test-host PID only.

## Window / inspector / prefs (Editor/UI/Window, UI Toolkit)

- `SmartCollidersPanel` = the whole per-object UI (header + Edit in Scene, metrics, Generate with decomposition options + inline progress, Fit, part list synced with `PartSelection` incl. rename/search, Bake). Hosted by `SmartCollidersWindow` (Window/Smart Colliders, Object/Batch tabs) and `SmartColliderAuthoringEditor` (inspector). Sprites → `Panel2D`.
- Building blocks in `UIKit` (`Segmented`, `ChipRow`, `Bar`, cards); styles in `SmartCollidersWindow.uss` (all rules scoped under `.sc-root`). `MetricsSummary` = shared aggregation (HUD, window).
- `SmartCollidersPreferences` (Preferences/Smart Colliders): fill opacity, wire width, HUD, hints, edit-after-generate, generated folder.
- `MetricsSummary.Fill` = `authoring.sourceVolume` (closed welded mesh volume from `SmartColliderOperations.ClosedVolume`, 0 if open) ÷ Σ collider volume — whole-object aggregate shown in the panel metrics card.
- `GizmoIconSetup` hides the components' Scene gizmo icons once per project/type (annotations exist only after `AnnotationUtility.GetAnnotations` populates them; keys in EditorPrefs; skipped in batchmode).
- Icons: `HudIcons.Draw(IIconPainter…)` draws to Painter2D or to `IconRaster` (CPU AA rasterizer) → `SmartCollidersIcons` (window/tool textures); component/preset icons are PNGs in `Editor/Icons` rasterized from the same vectors.

## Decomposition / skinned / batch / 2D

- `DecompositionRunner` (Editor/Decomposition): `Start` async (EditorApplication.update time slices) or `RunSync`; engine `Resolve` (Auto → V-HACD for Low/Medium, CoACD for High/Ultra, fallback Built-in). Applies results as one Undo group "Decompose Colliders". Pieces carry explicit `fitPoints` (part fitted from its own points, not its triangles).
- Built-in engine: `Runtime/Geometry/Decomposition` (`VoxelDecomposer` stepwise, `VoxelKernels` Burst jobs: conservative voxelization + border flood fill, summed-volume tables, best-first axis-plane splits, greedy concavity merge, pieces = mesh clipped to region boxes). `HullMerge` = same merge policy for native hulls.
- `NativeDecomposer` P/Invokes `smartcolliders_native` (`Native~/`, V-HACD v4 always, CoACD with `SC_WITH_COACD`, see `Native~/README.md`). PluginImporter metas must use the list form `- first: Editor: Editor`.
- Skinned: `SkinnedPartition` (dominant bone) → parts with `bone` + `bonePose`; `part.PoseDelta(authoringTransform)` moves preview/picking/bake to the current pose.
- Batch: `BatchProcessor` (time-sliced, cancel, `Run` sync for scripts/tests; prefabs via `LoadPrefabContents`/`SaveAsPrefabAsset`, `SmartColliderBaker.BankOwnerOverride` names the mesh bank) + `SmartColliderPreset` (ScriptableObject). Sprites in a batch use the 2D path.
- 2D: `Fit2D` (Runtime/Geometry, managed: circle/box/capsule fits, Douglas–Peucker, ear clipping + Hertel–Mehlhorn, greedy merge) + `SmartCollider2DAuthoring` (runtime, stripped in builds) + `SmartCollider2D` (Outline from sprite physics shape → Compute → Bake/Clear, rotated box/capsule → child) + `Preview2D` (Handles).
- Bake rules: Unity primitive colliders can't rotate → rotated primitives get a child GameObject (SameObject mode) or every part gets a named child under the container (ChildContainer mode). Children inherit layer + static flags. Hulls → convex `MeshCollider` with mesh sub-assets in a `SmartColliderMeshBank` under `SmartCollidersProjectSettings.GeneratedFolder` (ProjectSettings/SmartColliders.asset); meshes are updated in place, never deleted (undo safety).

## Geometry core (`Runtime/Geometry`, namespace `RecLabs.SmartColliders.Geometry`)

All Burst-compatible static code (no managed types, Temp allocations inside); entry point for jobs = `ShapeFitJob` (`Run()` sync / `Schedule()` async, `CompileSynchronously`).

- `ConvexHull3D` — quickhull with conflict lists; always expands the globally furthest point, so `maxVertices` gives a progressive *inner* hull. Cap 129 verts ⇒ ≤254 tris (PhysX 255 limit). Winding: `cross(b-a,c-a)` outward.
- `MinEnclosing` — exact Welzl sphere/circle (iterative nested loops, seeded shuffle), monotone-chain 2D hull, min-area rect.
- `PrimitiveFitter` — OBB (world/PCA/hull-face-normal + min-area-rect candidates; world-aligned preferred within 0.5%; axes canonicalized toward identity), capsule (min circle per candidate axis + cap push-out). All fits are **enclosing**.
- `FitMetrics` — Accuracy = nested volume IoU vs part's full hull; WorstFit = Hausdorff approx (surface samples vs hull planes); heuristic cost + tiers.
- `ShapeFitter.FitAll` — fits all 4 types, picks max `Accuracy − CostWeight·Cost` among `Accuracy ≥ MinAccuracy`, else hull. Flat input ⇒ thin box (`MinThickness`).
- Feed fitters full-hull vertices, not raw mesh vertices (same result, much faster). Benchmark: 100k points ≈ 40 ms.
- Package registered in `manifest.json` `testables` so its tests show in Test Runner.
- Root `*.csproj`/`*.sln` are Unity-generated (gitignored) — never edit.
- Git LFS configured in `.gitattributes`.

## Commands

```powershell
# EditMode tests headless (fails if project already open in Editor — use Test Runner window / Unity MCP then)
& "Y:\Unity\6000.3.17f1\Editor\Unity.exe" -batchmode -projectPath . -runTests -testPlatform EditMode -assemblyNames RecLabs.SmartColliders.Editor.Tests -testResults TestResults.xml -logFile -

# Single test
... -testFilter "RecLabs.SmartColliders.Tests.ClassName.MethodName"
```

## Product spec (decided)

Workflow: **Generate → Refine → Bake**.

- **Part**: unit of work = subset of source triangles + chosen collider type. Selection, conversion and decomposition operate on parts. Each part can be shown as Mesh (convex hull) / Box / Capsule / Sphere; when auto, pick the cheapest primitive that meets the fit tolerance, else convex hull.
- **Auto Generate**: decompose selection into N parts. Params: Quality, Max Colliders (presets 2/5/10/25/50/100), Auto Merge + Tolerance (presets 0.01/0.05/0.1/0.25/0.5), Async + Yield speed. Runs async with **pause / resume / cancel**, live progress overlay (elapsed, count).
- **Decomposition engines (selectable)**: V-HACD v4 (fast, Quality Low/Medium) and CoACD (best, Quality High/Ultra), both as native plugins built per platform: **Windows x64 + macOS universal (arm64/x64)**. Own primitive-fitting refinement on top. Convex hulls capped at 255 tris (Max Hull Vertices param, simplify hulls above limit).
- **Metrics per selection** (stats panel): Worst Fit (max surface deviation, world units), Accuracy % (volume overlap), Physics Cost (Light → Heavy+ from collider count/type), count per type.
- **Refine tools**: shrink/fit-to-mesh, recenter pivot, split/paint part, convert type, regenerate, per-axis rotation snap + flip, Local/Global space. Full Undo everywhere.
- **Preview** before bake: translucent per-part colored fills + wireframe in Scene view. **Bake** writes real Unity colliders and removes editing state.
- **Output target** user-chosen: same GameObject, child container, or per-bone (skinned).
- **Skinned meshes**: partition by dominant bone weight; colliders parented to bones.
- **2D** (secondary): Box/Circle/Capsule2D fit, PolygonCollider2D from sprite outline, convex decomposition into compound 2D.
- **Batch** (EditorWindow): selection, prefabs, folders, presets. **Never** auto-run on import (no `AssetPostprocessor`).
- Generated hull meshes saved as assets (never only in-scene) so prefabs stay valid.

UI: primary = **EditorWindow**; plus Scene view **Overlays** (floating toolbar + stats panel + contextual popovers with param description) and a component Inspector. UI Toolkit + USS. Visual quality is a first-class requirement: dark rounded panels, dual accent — **amber orange `#FFA53D`** (primary actions, active tool) + **violet `#9B7BFF`** (selection, highlights) — icon-driven toolbar, keyboard shortcuts, smooth state transitions.
