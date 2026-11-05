# Third Party Notices

This package contains third-party software components governed by the license(s) indicated below.

## V-HACD

- Component: V-HACD v4 (Volumetric Hierarchical Approximate Convex Decomposition), single-header `VHACD.h`
- Used in: `Plugins/` native binaries (`smartcolliders_native`), sources in `Native~/third_party/vhacd/`
- License type: BSD 3-Clause
- License: see `Native~/third_party/vhacd/LICENSE`

## CoACD (optional, not included in the prebuilt binaries)

- Component: CoACD (Approximate Convex Decomposition for 3D Meshes with Collision-Aware Concavity and Tree Search)
- Used in: native binaries built with `SC_WITH_COACD` (fetched at build time, not redistributed in this package)
- License type: MIT
- License: https://github.com/SarahWeiii/CoACD/blob/main/LICENSE
