using System;
using System.Collections.Generic;

namespace RecLabs.SmartColliders
{
    /// <summary>
    /// Compact triangle-set encoding: flat (start, count) pairs over the combined source triangle list.
    /// Keeps scene/prefab YAML small (a whole mesh is 2 ints; spatial splits stay mostly contiguous).
    /// </summary>
    public static class TriangleRanges
    {
        public static int[] All(int triangleCount) =>
            triangleCount > 0 ? new[] { 0, triangleCount } : Array.Empty<int>();

        public static int[] Range(int start, int count) =>
            count > 0 ? new[] { start, count } : Array.Empty<int>();

        /// <summary>Encodes ascending, unique triangle indices.</summary>
        public static int[] FromSorted(IReadOnlyList<int> sorted)
        {
            if (sorted == null || sorted.Count == 0) return Array.Empty<int>();
            var result = new List<int>();
            int start = sorted[0], count = 1;
            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i] == start + count) { count++; continue; }
                result.Add(start);
                result.Add(count);
                start = sorted[i];
                count = 1;
            }
            result.Add(start);
            result.Add(count);
            return result.ToArray();
        }

        public static int Count(int[] ranges)
        {
            if (ranges == null) return 0;
            int total = 0;
            for (int i = 1; i < ranges.Length; i += 2) total += ranges[i];
            return total;
        }

        public static IEnumerable<int> Enumerate(int[] ranges)
        {
            if (ranges == null) yield break;
            for (int r = 0; r + 1 < ranges.Length; r += 2)
            for (int t = 0; t < ranges[r + 1]; t++)
                yield return ranges[r] + t;
        }

        /// <summary>True if every range lies inside [0, triangleCount).</summary>
        public static bool IsValidFor(int[] ranges, int triangleCount)
        {
            if (ranges == null) return false;
            for (int r = 0; r + 1 < ranges.Length; r += 2)
                if (ranges[r] < 0 || ranges[r + 1] < 0 || ranges[r] + ranges[r + 1] > triangleCount)
                    return false;
            return true;
        }
    }
}
