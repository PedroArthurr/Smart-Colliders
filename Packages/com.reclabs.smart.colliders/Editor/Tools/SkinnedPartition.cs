using System.Collections.Generic;
using UnityEngine;

namespace RecLabs.SmartColliders.EditorTools
{
    /// <summary>
    /// Per-bone partition of skinned meshes: every triangle goes to the bone with the largest summed weight over its
    /// three vertices. Tiny groups fold into their nearest ancestor bone group. Static meshes stay one part each.
    /// </summary>
    internal static class SkinnedPartition
    {
        /// <summary>Groups smaller than this fraction of the skinned triangles fold into an ancestor.</summary>
        const float MinGroupFraction = 0.004f;

        /// <returns>Parts, or null when there is no skinned mesh with bones.</returns>
        public static List<SmartColliderPart> Build(SmartColliderAuthoring authoring, SourceGeometry geo)
        {
            var parts = new List<SmartColliderPart>();
            bool anySkinned = false;

            foreach (var src in geo.Sources)
            {
                if (src.TriangleCount == 0) continue;
                var smr = src.Component as SkinnedMeshRenderer;
                var mesh = smr != null ? smr.sharedMesh : null;
                var bones = smr != null ? smr.bones : null;
                var weights = mesh != null ? mesh.boneWeights : null;
                if (smr == null || bones == null || bones.Length == 0 || weights == null || weights.Length != src.VertexCount)
                {
                    parts.Add(NewPart(parts.Count, src.Name, null, Range(src.TriangleStart, src.TriangleCount)));
                    continue;
                }
                anySkinned = true;

                // Dominant bone per triangle.
                var groups = new Dictionary<int, List<int>>();
                var score = new Dictionary<int, float>(8);
                for (int t = 0; t < src.TriangleCount; t++)
                {
                    int tri = src.TriangleStart + t;
                    score.Clear();
                    for (int k = 0; k < 3; k++)
                    {
                        var w = weights[geo.Indices[tri * 3 + k] - src.VertexStart];
                        Add(score, w.boneIndex0, w.weight0);
                        Add(score, w.boneIndex1, w.weight1);
                        Add(score, w.boneIndex2, w.weight2);
                        Add(score, w.boneIndex3, w.weight3);
                    }
                    int best = -1;
                    float bestW = -1f;
                    foreach (var kv in score)
                        if (kv.Value > bestW && kv.Key >= 0 && kv.Key < bones.Length && bones[kv.Key] != null)
                        {
                            bestW = kv.Value;
                            best = kv.Key;
                        }
                    if (best < 0) best = 0;
                    if (!groups.TryGetValue(best, out var list)) groups[best] = list = new List<int>();
                    list.Add(tri);
                }

                FoldSmallGroups(groups, bones, Mathf.Max(4, Mathf.CeilToInt(src.TriangleCount * MinGroupFraction)));

                var order = new List<int>(groups.Keys);
                order.Sort();
                foreach (int b in order)
                {
                    var tris = groups[b];
                    tris.Sort();
                    var bone = bones[b];
                    parts.Add(NewPart(parts.Count, bone != null ? bone.name : $"Bone {b}", bone, TriangleRanges.FromSorted(tris)));
                }
            }
            return anySkinned ? parts : null;
        }

        static void Add(Dictionary<int, float> score, int bone, float w)
        {
            if (w <= 0f) return;
            score.TryGetValue(bone, out float s);
            score[bone] = s + w;
        }

        /// <summary>Moves groups below <paramref name="min"/> triangles into the closest ancestor bone that has a group.</summary>
        static void FoldSmallGroups(Dictionary<int, List<int>> groups, Transform[] bones, int min)
        {
            var index = new Dictionary<Transform, int>();
            for (int i = 0; i < bones.Length; i++)
                if (bones[i] != null && !index.ContainsKey(bones[i])) index[bones[i]] = i;

            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (int b in new List<int>(groups.Keys))
                {
                    if (!groups.TryGetValue(b, out var list) || list.Count >= min || groups.Count == 1) continue;
                    int target = -1;
                    for (var t = bones[b] != null ? bones[b].parent : null; t != null; t = t.parent)
                        if (index.TryGetValue(t, out int ti) && groups.ContainsKey(ti) && ti != b) { target = ti; break; }
                    if (target < 0)
                    {
                        // No ancestor group: fold into the largest group.
                        int largest = -1;
                        foreach (var kv in groups)
                            if (kv.Key != b && (largest < 0 || kv.Value.Count > groups[largest].Count)) largest = kv.Key;
                        target = largest;
                    }
                    if (target < 0) continue;
                    groups[target].AddRange(list);
                    groups.Remove(b);
                    changed = true;
                }
            }
        }

        static int[] Range(int start, int count) => TriangleRanges.Range(start, count);

        static SmartColliderPart NewPart(int index, string name, Transform bone, int[] ranges) => new SmartColliderPart
        {
            name = name,
            color = SmartColliderPart.PaletteColor(index),
            triangleRanges = ranges,
            bone = bone,
        };
    }
}
