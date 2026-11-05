# Smart Colliders native plugin

C API (`src/smartcolliders_native.cpp`) over **V-HACD v4** (always included) and **CoACD** (optional, `SC_WITH_COACD`). Unity loads it as `smartcolliders_native` (Editor only), and `Editor/Decomposition/NativeDecomposer.cs` calls it through P/Invoke.

| API | |
|---|---|
| `sc_version()` | 100 |
| `sc_engine_available(engine)` | 1 = V-HACD, 2 = CoACD |
| `sc_begin(...)` | Starts a job on a worker thread and returns a handle. |
| `sc_poll`, `sc_pause`, `sc_cancel` | Report progress (0..1, or −1 when done), pause by blocking inside the progress callback, and cancel. |
| `sc_hull_count`, `sc_hull_sizes`, `sc_hull_data` | Read the result hulls. |
| `sc_free` | Releases the job. |

## Prebuilt binaries (`../Plugins`)

They contain V-HACD only.

- `Windows/x86_64/smartcolliders_native.dll`
- `macOS/smartcolliders_native.dylib`: universal (arm64 + x86_64)

## Build without a C++ toolchain (zig)

```sh
ZIG=/path/to/zig sh build-zig.sh      # Windows x64 + macOS arm64/x64 slices, then the fat dylib (PowerShell)
```

## Build with CMake (adds CoACD)

```sh
# Windows (Visual Studio 2022)
cmake -S . -B build -A x64 -DSC_WITH_COACD=ON
cmake --build build --config Release
# macOS (universal)
cmake -S . -B build -G Xcode -DCMAKE_OSX_ARCHITECTURES="arm64;x86_64" -DSC_WITH_COACD=ON
cmake --build build --config Release
```

`SC_WITH_COACD` fetches CoACD from GitHub. CoACD bundles its own dependencies (OpenVDB, TBB, Boost subsets), so expect a long first build.

To build on CI instead, run the GitHub Actions workflow `.github/workflows/native-plugins.yml` (manual dispatch, or a push that touches `Native~/`). It uploads the binaries as artifacts. Copy them over `../Plugins/*`, keeping the existing `.meta` files: Editor only, OS-specific, CPU set.

> **Status:** the zig V-HACD build is tested on Windows. The macOS universal binary and the CoACD builds have not been run locally yet: no Mac or MSVC toolchain was available.

## Licenses

V-HACD: BSD-3-Clause (`third_party/vhacd/LICENSE`). CoACD: MIT.
