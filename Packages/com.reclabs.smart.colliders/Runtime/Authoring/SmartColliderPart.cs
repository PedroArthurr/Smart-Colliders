using System;
using RecLabs.SmartColliders.Geometry;
using UnityEngine;

namespace RecLabs.SmartColliders
{
    /// <summary>
    /// One future collider: a subset of the source triangles plus every fitted candidate shape.
    /// All candidates are kept so switching type is instant and lossless. Space = authoring transform local space.
    /// </summary>
    [Serializable]
    public sealed class SmartColliderPart
    {
        public const int CandidateCount = ShapeFitter.CandidateCount;

        [SerializeField] internal string name = "Part";
        [SerializeField] internal Color color = Color.white;
        /// <summary>True = type picked by the fitter; false = user override.</summary>
        [SerializeField] internal bool autoType = true;
        [SerializeField] internal ShapeType type = ShapeType.Box;
        [SerializeField] internal ColliderShape[] candidates = new ColliderShape[CandidateCount];
        [SerializeField] internal FitReport[] reports = new FitReport[CandidateCount];
        [SerializeField] internal Vector3[] hullVertices = Array.Empty<Vector3>();
        [SerializeField] internal int[] hullTriangles = Array.Empty<int>();
        /// <summary>(start, count) pairs into the combined source triangle list. See <see cref="TriangleRanges"/>.</summary>
        [SerializeField] internal int[] triangleRanges = Array.Empty<int>();
        /// <summary>
        /// Optional explicit point set the fits use instead of the triangles' vertices (decomposition pieces are
        /// clipped regions, not whole triangles). Empty = use the triangles. Authoring-local.
        /// </summary>
        [SerializeField] internal Vector3[] fitPoints = Array.Empty<Vector3>();
        /// <summary>Bone that drives this part (skinned per-bone parts); baked colliders go under it.</summary>
        [SerializeField] internal Transform bone;
        /// <summary>Bone → authoring-local matrix at fit time (parts follow the bone's current pose from there).</summary>
        [SerializeField] internal Matrix4x4 bonePose = Matrix4x4.identity;

        public string Name => name;
        public Color Color => color;
        public bool AutoType => autoType;
        public ShapeType Type => type;
        public ColliderShape Shape => candidates[(int)type];
        public FitReport Report => reports[(int)type];
        public ColliderShape GetCandidate(ShapeType t) => candidates[(int)t];
        public FitReport GetReport(ShapeType t) => reports[(int)t];
        public Vector3[] HullVertices => hullVertices;
        public int[] HullTriangles => hullTriangles;
        public int TriangleCount => TriangleRanges.Count(triangleRanges);
        public bool HasFitPoints => fitPoints != null && fitPoints.Length > 0;
        public Transform Bone => bone;

        /// <summary>
        /// Authoring-local transform carrying the part from its fitted pose to the bone's current pose
        /// (identity for parts without a bone). Preview, picking and bake apply it.
        /// </summary>
        public Matrix4x4 PoseDelta(Transform authoring) =>
            bone == null ? Matrix4x4.identity : authoring.worldToLocalMatrix * bone.localToWorldMatrix * bonePose.inverse;
        public bool HasFit => candidates != null && candidates.Length == CandidateCount && reports[(int)type].Valid;

        /// <summary>Distinct, vivid colors for adjacent parts (golden-ratio hue walk).</summary>
        public static Color PaletteColor(int index)
        {
            float hue = Mathf.Repeat(0.08f + index * 0.618034f, 1f);
            return Color.HSVToRGB(hue, 0.62f, 0.98f);
        }
    }
}
