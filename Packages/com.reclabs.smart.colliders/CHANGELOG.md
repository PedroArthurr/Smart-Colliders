# Changelog

All notable changes to this package are documented here. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.0.0] - 2026-11-05

### Added
- **Generate**: Single / Per Mesh / Decompose / Per Bone modes; 4 cached fits per part (Sphere, Capsule, Box, Convex Hull) with automatic cheapest-type selection.
- **Decomposition**: V-HACD v4 native plugin (Windows x64, macOS universal), optional CoACD (CMake / GitHub Actions build), built-in C# + Burst voxel engine as fallback; Auto Merge with tolerance presets, Max Colliders presets, background runs with pause / resume / cancel and a Scene view progress overlay.
- **Refine**: Scene view edit tool with Select / Move / Rotate / Resize / Paint modes, Local / Global handles, per-axis ±90° rotation and exact rotation field, Fit to Mesh, Align to Axes, Reset, Merge, Split / Paint triangles (brush, grow, fill connected, add to part), type conversion, metrics HUD (Accuracy, Worst Fit, Physics Cost) and Generate popover (G).
- **Bake**: Same Object / Child Container / Source Objects targets, convex hull meshes saved as assets (updated in place), re-bake reuses the container, Clean Unused Meshes, Bake & Finish.
- **Skinned meshes**: per-bone partition, pose-following preview / picking / bake.
- **2D**: Smart Colliders 2D for sprites — Auto / Circle / Box / Capsule / Polygon / Decompose (ear clipping + Hertel–Mehlhorn + greedy merge), live preview, Collider2D bake.
- **Window** (*Window > Smart Colliders*): object tab (metrics, generate, fit, searchable / renamable part list synced with the Scene selection, bake) and Batch tab (prefabs, folders, scene objects, presets, cancel, report).
- UI Toolkit inspector sharing the window panel; preferences page; vector icon set rasterized for window / tool / component icons.
- Player builds strip the editing components; baked colliders stay.
