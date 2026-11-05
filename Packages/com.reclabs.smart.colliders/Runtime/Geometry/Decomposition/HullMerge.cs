using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace RecLabs.SmartColliders.Geometry.Decomposition
{
    /// <summary>
    /// Greedy merging of convex pieces given only their hull points (engines that return hulls, e.g. V-HACD):
    /// repeatedly merges the touching pair whose merged hull wastes the least volume relative to the two hulls,
    /// while that waste ≤ tolerance (Auto Merge) or while there are more pieces than allowed.
    /// </summary>
    public static class HullMerge
    {
        sealed class Item
        {
            public float3[] Points;
            public float Volume;
            public float3 Min, Max;
            public List<int> Sources = new List<int>();
        }

        /// <returns>For every output piece, the indices of the input hulls merged into it.</returns>
        public static List<List<int>> Merge(IReadOnlyList<float3[]> hulls, bool autoMerge, float tolerance, int maxCount)
        {
            var items = new List<Item>();
            float3 gmin = float.MaxValue, gmax = float.MinValue;
            for (int i = 0; i < hulls.Count; i++)
            {
                var it = new Item { Points = hulls[i], Volume = HullVolume(hulls[i]) };
                it.Sources.Add(i);
                Bounds(it);
                gmin = math.min(gmin, it.Min);
                gmax = math.max(gmax, it.Max);
                items.Add(it);
            }
            float touch = math.length(gmax - gmin) * 0.01f;
            var cache = new Dictionary<(Item, Item), (float ratio, float3[] pts, float volume)>();

            while (items.Count > 1)
            {
                bool mustMerge = items.Count > maxCount;
                if (!mustMerge && !autoMerge) break;
                int bestA = -1, bestB = -1;
                float bestRatio = float.MaxValue;
                float3[] bestPoints = null;
                float bestVolume = 0f;
                for (int pass = 0; pass < 2 && bestA < 0; pass++)
                {
                    for (int i = 0; i < items.Count; i++)
                    for (int j = i + 1; j < items.Count; j++)
                    {
                        if (pass == 0 && !Touching(items[i], items[j], touch)) continue;
                        if (!cache.TryGetValue((items[i], items[j]), out var entry))
                        {
                            var merged = Concat(items[i].Points, items[j].Points);
                            float vol = HullVolume(merged);
                            float sum = items[i].Volume + items[j].Volume;
                            entry = ((vol - sum) / math.max(sum, 1e-12f), merged, vol);
                            cache[(items[i], items[j])] = entry;
                        }
                        var (ratio, pts, v) = entry;
                        if (ratio < bestRatio)
                        {
                            bestRatio = ratio;
                            bestA = i;
                            bestB = j;
                            bestPoints = pts;
                            bestVolume = v;
                        }
                    }
                    if (!mustMerge) break; // only touching pairs auto-merge
                }
                if (bestA < 0) break;
                if (!mustMerge && bestRatio > tolerance) break;

                var a = items[bestA];
                var b = items[bestB];
                a.Points = bestPoints;
                a.Volume = bestVolume;
                a.Sources.AddRange(b.Sources);
                var stale = new List<(Item, Item)>();
                foreach (var key in cache.Keys)
                    if (key.Item1 == a || key.Item2 == a || key.Item1 == b || key.Item2 == b) stale.Add(key);
                foreach (var key in stale) cache.Remove(key);
                Bounds(a);
                items.RemoveAt(bestB);
            }

            var result = new List<List<int>>(items.Count);
            foreach (var it in items) result.Add(it.Sources);
            return result;
        }

        static void Bounds(Item it)
        {
            it.Min = float.MaxValue;
            it.Max = float.MinValue;
            foreach (var p in it.Points)
            {
                it.Min = math.min(it.Min, p);
                it.Max = math.max(it.Max, p);
            }
        }

        static bool Touching(Item a, Item b, float tol) =>
            math.all(a.Min <= b.Max + tol) && math.all(b.Min <= a.Max + tol);

        static float3[] Concat(float3[] a, float3[] b)
        {
            var r = new float3[a.Length + b.Length];
            System.Array.Copy(a, r, a.Length);
            System.Array.Copy(b, 0, r, a.Length, b.Length);
            return r;
        }

        static float HullVolume(float3[] points)
        {
            if (points.Length < 4) return 0f;
            var pts = new NativeArray<float3>(points, Allocator.TempJob);
            var packed = new NativeArray<int4>(1, Allocator.TempJob);
            var vol = new NativeArray<float>(1, Allocator.TempJob);
            packed[0] = new int4(0, points.Length, 0, 0);
            new PairHullVolumeJob { Points = pts, Pairs = packed, Volumes = vol }.Run();
            float v = vol[0];
            pts.Dispose();
            packed.Dispose();
            vol.Dispose();
            return v;
        }
    }
}
