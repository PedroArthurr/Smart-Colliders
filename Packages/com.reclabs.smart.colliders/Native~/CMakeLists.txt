# Smart Colliders native plugin (V-HACD v4 always, CoACD optional).
# Windows:  cmake -S . -B build -A x64 -DSC_WITH_COACD=ON && cmake --build build --config Release
# macOS:    cmake -S . -B build -G Xcode -DCMAKE_OSX_ARCHITECTURES="arm64;x86_64" -DSC_WITH_COACD=ON && cmake --build build --config Release
# Without a C++ toolchain, build-zig.sh produces the V-HACD-only binaries (Windows x64 + macOS universal).
cmake_minimum_required(VERSION 3.20)
project(smartcolliders_native CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
option(SC_WITH_COACD "Build with CoACD (fetches and builds CoACD and its dependencies)" OFF)

add_library(smartcolliders_native SHARED src/smartcolliders_native.cpp)
# Same file names as the prebuilt binaries in ../Plugins (keeps their .meta import settings).
set_target_properties(smartcolliders_native PROPERTIES PREFIX "")
target_include_directories(smartcolliders_native PRIVATE third_party/vhacd)

if(SC_WITH_COACD)
  include(FetchContent)
  FetchContent_Declare(coacd GIT_REPOSITORY https://github.com/SarahWeiii/CoACD.git GIT_TAG main GIT_SHALLOW TRUE)
  FetchContent_MakeAvailable(coacd)
  target_compile_definitions(smartcolliders_native PRIVATE SC_WITH_COACD=1 COACD_STATIC=1)
  target_include_directories(smartcolliders_native PRIVATE ${coacd_SOURCE_DIR}/public)
  target_link_libraries(smartcolliders_native PRIVATE coacd)
endif()

if(MSVC)
  target_compile_options(smartcolliders_native PRIVATE /O2 /EHsc)
  set_property(TARGET smartcolliders_native PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded")
endif()
