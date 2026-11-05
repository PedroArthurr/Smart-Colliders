using System.Collections.Generic;
using RecLabs.SmartColliders.Geometry;
using UnityEngine;

namespace RecLabs.SmartColliders.UI
{
    /// <summary>Aggregated fit metrics of a set of parts (HUD card, window, inspector).</summary>
    internal struct MetricsSummary
    {
        public int Parts;
        public float Accuracy;      // volume-weighted
        public float WorstFit;      // max
        public float Cost;          // sum
        public PhysicsCostTier Tier;
        public int Sphere, Capsule, Box, Hull;
        /// <summary>Closed source mesh volume / total collider volume (all parts, 0 = unknown). Overlaps count twice, so this is a lower bound.</summary>
        public float Fill;

        public static readonly Color Good = new Color32(0x6E, 0xE7, 0xA0, 0xFF);
        public static readonly Color Bad = new Color32(0xFF, 0x6B, 0x6B, 0xFF);

        public static MetricsSummary Of(SmartColliderAuthoring a, IReadOnlyList<int> selection)
        {
            var s = new MetricsSummary();
            float vol = 0f, acc = 0f;
            int n = selection != null && selection.Count > 0 ? selection.Count : a.Parts.Count;
            for (int k = 0; k < n; k++)
            {
                int i = selection != null && selection.Count > 0 ? selection[k] : k;
                if (i < 0 || i >= a.Parts.Count) continue;
                var p = a.Parts[i];
                if (!p.HasFit) continue;
                s.Parts++;
                float w = Mathf.Max(p.Report.Volume, 1e-9f);
                vol += w;
                acc += p.Report.Accuracy * w;
                s.WorstFit = Mathf.Max(s.WorstFit, p.Report.WorstFit);
                s.Cost += p.Report.Cost;
                switch (p.Type)
                {
                    case ShapeType.Sphere: s.Sphere++; break;
                    case ShapeType.Capsule: s.Capsule++; break;
                    case ShapeType.Box: s.Box++; break;
                    default: s.Hull++; break;
                }
            }
            s.Accuracy = vol > 0f ? acc / vol : 0f;
            if ((selection == null || selection.Count == 0) && a.sourceVolume > 0f && vol > 0f) s.Fill = Mathf.Clamp01(a.sourceVolume / vol);
            s.Tier = FitMetrics.TierOf(s.Cost);
            return s;
        }

        public int CountOf(ShapeType t) => t switch
        {
            ShapeType.Sphere => Sphere,
            ShapeType.Capsule => Capsule,
            ShapeType.Box => Box,
            _ => Hull,
        };

        public string AccuracyText => Parts == 0 ? "–" : $"{Accuracy * 100f:F0}%";
        public Color AccuracyColor => Accuracy >= 0.9f ? Good : Accuracy >= 0.75f ? SmartCollidersTheme.Accent : Bad;
        public string WorstText => Parts == 0 ? "–" : WorstFit < 0.001f ? "0" : WorstFit.ToString(WorstFit < 1f ? "0.###" : "0.##", System.Globalization.CultureInfo.InvariantCulture);
        public string CostText => Parts == 0 ? "–" : Tier == PhysicsCostTier.HeavyPlus ? "Heavy+" : Tier.ToString();
        public Color CostColor => Tier == PhysicsCostTier.Light ? Good : Tier == PhysicsCostTier.Medium ? SmartCollidersTheme.Accent : Bad;
    }
}
