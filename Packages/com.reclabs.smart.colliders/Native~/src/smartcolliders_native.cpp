// Smart Colliders native decomposition plugin.
// Stable C API over V-HACD v4 (always) and CoACD (when built with SC_WITH_COACD).
// Each job runs on its own thread; the managed side polls progress, can pause (the worker blocks inside the
// engine's progress callback) and cancel.

#define ENABLE_VHACD_IMPLEMENTATION 1
#define VHACD_DISABLE_THREADING 0
#include "VHACD.h"

#ifdef SC_WITH_COACD
#include "coacd.h"
#endif

#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <thread>
#include <vector>

#if defined(_WIN32)
#define SC_API extern "C" __declspec(dllexport)
#else
#define SC_API extern "C" __attribute__((visibility("default")))
#endif

namespace
{
    constexpr int kVersion = 100; // 1.0.0
    constexpr int kEngineVHacd = 1;
    constexpr int kEngineCoAcd = 2;

    struct Params
    {
        int32_t engine;
        int32_t maxHulls;
        int32_t resolution;
        float minVolumeError;
        int32_t maxVerticesPerHull;
        int32_t maxRecursion;
        float concavity;
        int32_t mctsIterations;
        int32_t seed;
        int32_t shrinkWrap;
    };

    struct Hull
    {
        std::vector<float> vertices;   // xyz
        std::vector<int32_t> triangles;
    };

    struct Job : VHACD::IVHACD::IUserCallback
    {
        Params params{};
        std::vector<float> vertices;
        std::vector<uint32_t> triangles;
        std::vector<Hull> hulls;
        std::thread worker;
        std::atomic<int> state{0};          // 0 running, 1 done, -1 failed / cancelled
        std::atomic<float> progress{0.f};
        std::atomic<bool> paused{false};
        std::atomic<bool> cancel{false};
        VHACD::IVHACD* vhacd = nullptr;

        void WaitWhilePaused()
        {
            while (paused.load() && !cancel.load())
                std::this_thread::sleep_for(std::chrono::milliseconds(15));
        }

        // V-HACD progress (worker thread).
        void Update(const double overallProgress, const double, const char* const, const char*) override
        {
            progress.store(static_cast<float>(overallProgress / 100.0));
            WaitWhilePaused();
            if (cancel.load() && vhacd) vhacd->Cancel();
        }

        void RunVHacd()
        {
            vhacd = VHACD::CreateVHACD();
            VHACD::IVHACD::Parameters p;
            p.m_callback = this;
            p.m_maxConvexHulls = static_cast<uint32_t>(params.maxHulls > 0 ? params.maxHulls : 1);
            p.m_resolution = static_cast<uint32_t>(params.resolution > 1000 ? params.resolution : 100000);
            p.m_minimumVolumePercentErrorAllowed = params.minVolumeError > 0 ? params.minVolumeError : 1.0;
            p.m_maxRecursionDepth = static_cast<uint32_t>(params.maxRecursion > 0 ? params.maxRecursion : 10);
            p.m_shrinkWrap = params.shrinkWrap != 0;
            p.m_maxNumVerticesPerCH = static_cast<uint32_t>(params.maxVerticesPerHull >= 8 ? params.maxVerticesPerHull : 64);
            p.m_asyncACD = true;           // V-HACD's own multi-core work; we are already off the main thread
            p.m_findBestPlane = false;

            bool ok = vhacd->Compute(vertices.data(), static_cast<uint32_t>(vertices.size() / 3),
                                     triangles.data(), static_cast<uint32_t>(triangles.size() / 3), p);
            if (ok && !cancel.load())
            {
                uint32_t n = vhacd->GetNConvexHulls();
                hulls.reserve(n);
                for (uint32_t i = 0; i < n; i++)
                {
                    VHACD::IVHACD::ConvexHull ch;
                    if (!vhacd->GetConvexHull(i, ch)) continue;
                    Hull h;
                    h.vertices.reserve(ch.m_points.size() * 3);
                    for (const auto& v : ch.m_points)
                    {
                        h.vertices.push_back(static_cast<float>(v.mX));
                        h.vertices.push_back(static_cast<float>(v.mY));
                        h.vertices.push_back(static_cast<float>(v.mZ));
                    }
                    h.triangles.reserve(ch.m_triangles.size() * 3);
                    for (const auto& t : ch.m_triangles)
                    {
                        h.triangles.push_back(static_cast<int32_t>(t.mI0));
                        h.triangles.push_back(static_cast<int32_t>(t.mI1));
                        h.triangles.push_back(static_cast<int32_t>(t.mI2));
                    }
                    hulls.push_back(std::move(h));
                }
            }
            vhacd->Clean();
            vhacd->Release();
            vhacd = nullptr;
            state.store(ok && !cancel.load() ? 1 : -1);
        }

#ifdef SC_WITH_COACD
        void RunCoAcd()
        {
            CoACD_Mesh mesh;
            std::vector<double> dv(vertices.begin(), vertices.end());
            std::vector<int> it(triangles.begin(), triangles.end());
            mesh.vertices_ptr = dv.data();
            mesh.vertices_count = dv.size() / 3;
            mesh.triangles_ptr = it.data();
            mesh.triangles_count = it.size() / 3;
            // CoACD has no progress/cancel hooks: report indeterminate progress, honour pause only before start.
            WaitWhilePaused();
            if (cancel.load()) { state.store(-1); return; }
            progress.store(0.05f);
            CoACD_setLogLevel("off");
            CoACD_MeshArray result = CoACD_run(mesh, params.concavity, params.maxHulls, preprocess_auto, /*prep res*/ 50,
                                               /*sample res*/ 2000, /*mcts nodes*/ 20, params.mctsIterations, /*mcts depth*/ 3,
                                               /*pca*/ false, /*merge*/ true, /*decimate*/ true, params.maxVerticesPerHull,
                                               /*extrude*/ false, 0.01, /*apx mode*/ apx_ch, static_cast<unsigned>(params.seed),
                                               /*real metric*/ false);
            if (!cancel.load())
            {
                for (uint64_t i = 0; i < result.meshes_count; i++)
                {
                    const CoACD_Mesh& m = result.meshes_ptr[i];
                    Hull h;
                    for (uint64_t k = 0; k < m.vertices_count * 3; k++) h.vertices.push_back(static_cast<float>(m.vertices_ptr[k]));
                    for (uint64_t k = 0; k < m.triangles_count * 3; k++) h.triangles.push_back(m.triangles_ptr[k]);
                    hulls.push_back(std::move(h));
                }
            }
            CoACD_freeMeshArray(result);
            state.store(cancel.load() ? -1 : 1);
        }
#endif

        void Run()
        {
#ifdef SC_WITH_COACD
            if (params.engine == kEngineCoAcd) { RunCoAcd(); return; }
#endif
            RunVHacd();
        }
    };
}

SC_API int sc_version() { return kVersion; }

SC_API int sc_engine_available(int engine)
{
    if (engine == kEngineVHacd) return 1;
#ifdef SC_WITH_COACD
    if (engine == kEngineCoAcd) return 1;
#endif
    return 0;
}

SC_API void* sc_begin(const float* vertices, int vertexCount, const int* triangles, int triangleCount, const Params* params)
{
    if (!vertices || !triangles || vertexCount < 4 || triangleCount < 4 || !params) return nullptr;
    if (!sc_engine_available(params->engine)) return nullptr;
    auto* job = new Job();
    job->params = *params;
    job->vertices.assign(vertices, vertices + static_cast<size_t>(vertexCount) * 3);
    job->triangles.resize(static_cast<size_t>(triangleCount) * 3);
    for (size_t i = 0; i < job->triangles.size(); i++) job->triangles[i] = static_cast<uint32_t>(triangles[i]);
    job->worker = std::thread([job] { job->Run(); });
    return job;
}

SC_API int sc_poll(void* handle, float* progress)
{
    auto* job = static_cast<Job*>(handle);
    if (!job) return -1;
    if (progress) *progress = job->progress.load();
    return job->state.load();
}

SC_API void sc_pause(void* handle, int paused)
{
    if (auto* job = static_cast<Job*>(handle)) job->paused.store(paused != 0);
}

SC_API void sc_cancel(void* handle)
{
    if (auto* job = static_cast<Job*>(handle))
    {
        job->cancel.store(true);
        job->paused.store(false);
    }
}

SC_API int sc_hull_count(void* handle)
{
    auto* job = static_cast<Job*>(handle);
    return job && job->state.load() == 1 ? static_cast<int>(job->hulls.size()) : 0;
}

SC_API void sc_hull_sizes(void* handle, int hull, int* vertexCount, int* triangleCount)
{
    auto* job = static_cast<Job*>(handle);
    *vertexCount = *triangleCount = 0;
    if (!job || hull < 0 || hull >= static_cast<int>(job->hulls.size())) return;
    *vertexCount = static_cast<int>(job->hulls[hull].vertices.size() / 3);
    *triangleCount = static_cast<int>(job->hulls[hull].triangles.size() / 3);
}

SC_API void sc_hull_data(void* handle, int hull, float* vertices, int* triangles)
{
    auto* job = static_cast<Job*>(handle);
    if (!job || hull < 0 || hull >= static_cast<int>(job->hulls.size())) return;
    const Hull& h = job->hulls[hull];
    std::memcpy(vertices, h.vertices.data(), h.vertices.size() * sizeof(float));
    std::memcpy(triangles, h.triangles.data(), h.triangles.size() * sizeof(int32_t));
}

SC_API void sc_free(void* handle)
{
    auto* job = static_cast<Job*>(handle);
    if (!job) return;
    job->cancel.store(true);
    job->paused.store(false);
    if (job->worker.joinable()) job->worker.join();
    delete job;
}
