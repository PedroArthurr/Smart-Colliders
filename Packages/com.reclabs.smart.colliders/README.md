# Smart Colliders

Automatic collider generation and editing for Unity 6.3+. You fit colliders to static, skinned and 2D sprite geometry, refine them in the Scene view, and bake them into ordinary Unity colliders.

**Workflow:** Generate → Refine → Bake.

## Install

Add the package by git URL (*Window > Package Manager > + > Install package from git URL…*):

```
https://github.com/PedroArthurr/Smart-Colliders.git?path=/Packages/com.reclabs.smart.colliders#v1.0.0
```

You can also add it to `Packages/manifest.json` directly:

```json
"com.reclabs.smart.colliders": "https://github.com/PedroArthurr/Smart-Colliders.git?path=/Packages/com.reclabs.smart.colliders#v1.0.0"
```

Or install from a tarball (*+ > Install package from tarball…*) and pick `com.reclabs.smart.colliders-1.0.0.tgz` (a GitHub release asset, or built locally with `npm pack` inside the package folder). In `manifest.json`: `"com.reclabs.smart.colliders": "file:../path/to/com.reclabs.smart.colliders-1.0.0.tgz"`.

Dependencies (installed automatically): Burst, Mathematics, Collections, Physics, Physics 2D.

## Quick start

1. Right-click a GameObject in the Hierarchy and choose **Smart Colliders > Generate Colliders**. This adds `Smart Colliders`, fits one collider per mesh and activates the Scene edit tool. Sprites get `Smart Colliders 2D` instead.
2. Refine the colliders in the Scene view. You can also use **Window > Smart Colliders** or the component inspector; both show the same panel.
3. Click **Bake** to write real colliders. **Bake & Finish** also removes the editing component.

Nothing runs automatically. There is no `AssetPostprocessor`, so generation only happens when you ask for it.

## Generate

| Mode | Result |
|---|---|
| Single | One collider around every mesh. |
| Per Mesh | One collider per `MeshFilter` / `SkinnedMeshRenderer`. |
| Decompose | Approximate convex decomposition into several colliders. |
| Per Bone | Skinned meshes only: one collider per dominant bone, parented to that bone. |

For each part, the tool fits a Sphere, Capsule, Box and Convex Hull and caches all four, so switching type is instant. In **auto** mode the cheapest type that reaches *Min Accuracy* wins, and a convex hull is used when no primitive does.

### Decomposition

| Setting | Meaning |
|---|---|
| Quality | Low / Medium favour speed. High / Ultra favour precise concavity cuts. |
| Engine | **Auto** picks V-HACD for Low/Medium and CoACD for High/Ultra, falling back to Built-in when a native engine is unavailable. |
| Max Colliders | Upper bound on the number of colliders (presets 2 / 5 / 10 / 25 / 50 / 100). |
| Auto Merge + tolerance | Merges neighbouring pieces whose combined hull wastes at most this fraction of their volume. |
| Background + speed | Runs asynchronously with pause / resume / cancel and a progress overlay in the Scene view. *Speed* is the work done per editor frame. |

Engines:

- **V-HACD v4** is a native plugin, built for Windows x64 and macOS universal (arm64 + x64).
- **CoACD** is native and optional. It must be built with `SC_WITH_COACD`; see *Native plugin* below.
- **Built-in** is pure C# + Burst and always available. It uses voxel-based best-first plane splits plus greedy merging, and pieces are clipped to the real mesh.

## Refine (Scene view)

The edit tool is active after generation, or you can turn it on with **Edit in Scene** or the Tools overlay. Click a collider to select it. Shift- or Ctrl-click adds to the selection.

| Key | Action |
|---|---|
| 1 – 5 | Select · Move · Rotate · Resize · Paint |
| G | Generate popover (mode, quality, max colliders, merge, hull vertices) |
| X | Local / Global handle space |
| M | Merge the selected colliders |
| Del | Remove the selected colliders |
| F | Frame the selection |
| Ctrl+A | Select all |
| Esc | Deselect / clear paint |
| [ ] or Shift+Wheel | Brush size (Paint) |
| Ctrl (Paint) | Erase |
| Enter (Paint) | Split the painted triangles into a new collider |

The HUD shows **Accuracy**, **Worst Fit** and **Physics Cost** for the selection, with the whole object as fallback. For the whole object, the window also shows **mesh fill**: closed mesh volume ÷ total collider volume. It is a lower bound, because overlapping colliders count twice. It also has toolbar actions for each part:

- Fit to Mesh
- Align to Axes
- Reset
- Merge
- Remove
- Convert type
- Per-axis ±90° rotation and an exact rotation field

Every change can be undone.

## Bake

| Target | Result |
|---|---|
| Same Object | Colliders go on the GameObject. Rotated primitives get a child. |
| Container | A child container holds one named child per collider. |
| Source Objects | Each collider goes on the GameObject that owns its mesh or bone, so it follows animated parts. |

- Convex hulls are saved as mesh assets in the **Generated folder** (*Preferences > Smart Colliders*). They are never stored only in the scene, so prefabs stay valid.
- **Re-bake** updates the existing container and meshes in place.
- **Clean unused meshes** deletes bank meshes that no baked collider references. This cannot be undone.
- Editing components (`Smart Colliders`, `Smart Colliders 2D`) are stripped from scenes in player builds. Baked colliders stay.

## Skinned meshes

Per Bone mode partitions triangles by dominant bone weight and folds small groups into an ancestor bone. Fits are stored relative to the bind pose. The preview, picking and bake follow the bone's current pose.

## 2D

`Smart Colliders 2D` on a `SpriteRenderer` works from the sprite physics shape (or the mesh outline when there is none) and respects flipX/flipY.

Modes:

- **Auto**: cheapest primitive above *Min Accuracy*, otherwise a polygon.
- **Circle**, **Box**, **Capsule**: one collider of that type.
- **Polygon**: a `PolygonCollider2D` built from the simplified outline.
- **Decompose**: convex pieces, each fitted with its best primitive or as a polygon path. Uses Max Colliders and Merge.

Rotated boxes and capsules get a child GameObject. The Scene view previews the result live.

## Batch

**Window > Smart Colliders > Batch**:

1. Drop prefabs, folders or scene objects.
2. Pick a **Preset** (*Create > Smart Colliders > Preset*).
3. Choose Bake / Keep editable / Skip existing.
4. Click **Process**.

Prefabs are edited in isolation and saved. Scene objects can be undone. The run can be cancelled, and a report lists colliders and accuracy per object; click a row to ping its asset. Model files (`.fbx`) are read-only, so wrap them in a prefab first.

Scripting:

```csharp
var results = BatchProcessor.Run(new Object[] { folderAsset }, preset, bake: true, keepAuthoring: false);
```

## Preferences

*Preferences > Smart Colliders* covers:

- Preview on/off, x-ray, fill opacity, wire width
- HUD and keyboard hints
- Edit after generate
- Generated folder
- Engine availability

## Native plugin

Sources are in `Native~/` (ignored by Unity). The prebuilt binaries in `Plugins/` contain **V-HACD only**:

- Without a C++ toolchain, `Native~/build-zig.sh` cross-compiles Windows x64 and both macOS slices with zig, then `make-universal.ps1` merges the macOS slices into a fat dylib.
- With CMake, the configure and build commands are in `Native~/CMakeLists.txt`. Add `-DSC_WITH_COACD=ON` to fetch and build CoACD (needs CMake ≥ 3.20, a C++17 compiler and network access).
- The workflow `.github/workflows/native-plugins.yml` builds both platforms with CoACD on GitHub Actions and uploads the binaries as artifacts. The CoACD build has not been verified locally.

## Development

- Tests: EditMode, assembly `RecLabs.SmartColliders.Editor.Tests`. The package is listed in `testables`.
- The geometry core (`Runtime/Geometry`) is Burst-compatible static code. Heavy work runs in Burst jobs.

## License notes

V-HACD is BSD-3-Clause (`Native~/third_party/vhacd/LICENSE`). CoACD is MIT and is fetched at build time.
