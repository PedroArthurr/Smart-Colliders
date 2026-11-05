using System;
using System.Collections.Generic;
using System.Linq;

namespace RecLabs.SmartColliders.UI
{
    /// <summary>Editor-session selection of parts per authoring component (not serialized, not undoable).</summary>
    internal static class PartSelection
    {
        static readonly Dictionary<SmartColliderAuthoring, List<int>> s_Selected = new Dictionary<SmartColliderAuthoring, List<int>>();
        static readonly List<int> s_Empty = new List<int>();

        public static event Action Changed;

        public static IReadOnlyList<int> Get(SmartColliderAuthoring a)
        {
            if (a == null || !s_Selected.TryGetValue(a, out var list)) return s_Empty;
            list.RemoveAll(i => i < 0 || i >= a.Parts.Count);
            return list;
        }

        public static bool Contains(SmartColliderAuthoring a, int index) => Get(a).Contains(index);
        public static bool Any(SmartColliderAuthoring a) => Get(a).Count > 0;

        public static void Set(SmartColliderAuthoring a, int index)
        {
            var list = GetOrCreate(a);
            list.Clear();
            if (index >= 0) list.Add(index);
            Changed?.Invoke();
        }

        public static void SetMany(SmartColliderAuthoring a, IEnumerable<int> indices)
        {
            var list = GetOrCreate(a);
            var next = new List<int>();
            foreach (int i in indices) if (i >= 0 && i < a.Parts.Count && !next.Contains(i)) next.Add(i);
            if (next.SequenceEqual(list)) return;
            list.Clear();
            list.AddRange(next);
            Changed?.Invoke();
        }

        public static void Toggle(SmartColliderAuthoring a, int index)
        {
            var list = GetOrCreate(a);
            if (!list.Remove(index)) list.Add(index);
            Changed?.Invoke();
        }

        public static void SelectAll(SmartColliderAuthoring a)
        {
            var list = GetOrCreate(a);
            list.Clear();
            list.AddRange(Enumerable.Range(0, a.Parts.Count));
            Changed?.Invoke();
        }

        public static void Clear(SmartColliderAuthoring a)
        {
            if (a != null && s_Selected.TryGetValue(a, out var list) && list.Count > 0)
            {
                list.Clear();
                Changed?.Invoke();
            }
        }

        static List<int> GetOrCreate(SmartColliderAuthoring a)
        {
            // Drop entries of destroyed components.
            foreach (var dead in s_Selected.Keys.Where(k => k == null).ToList()) s_Selected.Remove(dead);
            if (!s_Selected.TryGetValue(a, out var list)) s_Selected[a] = list = new List<int>();
            return list;
        }
    }
}
