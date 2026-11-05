using System.IO;
using RecLabs.SmartColliders.Geometry;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RecLabs.SmartColliders.EditorTools
{
    /// <summary>
    /// Turns parts into real Unity colliders. Fully undoable as one step; re-bake replaces only what
    /// the previous bake produced. Hull meshes live in a <see cref="SmartColliderMeshBank"/> asset.
    /// </summary>
    public static class SmartColliderBaker
    {
        const float AxisAlignedTolerance = 1e-4f;

        /// <returns>Number of colliders created.</returns>
        public static int Bake(SmartColliderAuthoring authoring)
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Bake Smart Colliders");

            Undo.RecordObject(authoring, "Bake Smart Colliders");
            bool container = authoring.bakeTarget == BakeTarget.ChildContainer;
            // Re-bake keeps the existing container GameObject (external references to it stay valid).
            GameObject keep = container ? FindContainer(authoring) : null;
            ClearInternal(authoring, keep);

            Transform host = authoring.transform;
            if (container)
            {
                if (keep != null)
                {
                    authoring.bakedObjects.Add(keep);
                    host = keep.transform;
                }
                else host = CreateChild(authoring, authoring.transform, ContainerName(authoring), float3.zero, quaternion.identity).transform;
            }

            // Source Objects: each single-mesh part goes onto the GameObject that owns that mesh.
            SourceGeometry geo = authoring.bakeTarget == BakeTarget.SourceObjects ? SourceGeometry.Gather(authoring) : null;
            try
            {
                int hullIndex = 0;
                foreach (var source in authoring.parts)
                {
                    if (!source.HasFit) continue;
                    // Bone parts follow the current pose.
                    var part = Posed(source, authoring.transform);
                    Collider collider;

                    if (container)
                    {
                        collider = BakeIntoContainer(authoring, host, part, ref hullIndex);
                    }
                    else
                    {
                        Transform target = authoring.transform;
                        if (geo != null && source.bone != null)
                        {
                            target = source.bone;
                        }
                        else if (geo != null)
                        {
                            int src = geo.SourceIndexOf(part.triangleRanges);
                            if (src >= 0) target = geo.Sources[src].Component.transform;
                        }
                        collider = BakeOnHost(authoring, target, part, ref hullIndex);
                    }
                    if (collider == null) continue;

                    collider.sharedMaterial = authoring.material;
                    collider.isTrigger = authoring.isTrigger;
                    authoring.bakedColliders.Add(collider);
                }
            }
            finally
            {
                geo?.Dispose();
            }

            if (authoring.meshBank != null) AssetDatabase.SaveAssetIfDirty(authoring.meshBank);
            Undo.CollapseUndoOperations(group);
            return authoring.bakedColliders.Count;
        }

        public static void ClearBaked(SmartColliderAuthoring authoring)
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Clear Baked Colliders");
            Undo.RecordObject(authoring, "Clear Baked Colliders");
            ClearInternal(authoring);
            Undo.CollapseUndoOperations(group);
        }

        /// <summary>Bakes, then removes the authoring component (colliders stay, editing state is gone).</summary>
        public static int BakeAndRemoveAuthoring(SmartColliderAuthoring authoring)
        {
            int group = Undo.GetCurrentGroup();
            int count = Bake(authoring);
            Undo.DestroyObjectImmediate(authoring);
            Undo.CollapseUndoOperations(group);
            return count;
        }

        static void ClearInternal(SmartColliderAuthoring authoring, GameObject keep = null)
        {
            foreach (var c in authoring.bakedColliders)
                if (c != null) Undo.DestroyObjectImmediate(c);
            for (int i = authoring.bakedObjects.Count - 1; i >= 0; i--)
            {
                var go = authoring.bakedObjects[i];
                if (go == null) continue;
                if (go == keep)
                {
                    // Kept container: drop only the children we created (user-added children survive).
                    continue;
                }
                Undo.DestroyObjectImmediate(go);
            }
            authoring.bakedColliders.Clear();
            authoring.bakedObjects.Clear();
        }

        /// <summary>Copy of a bone part moved to the bone's current pose (same instance for parts without a bone).</summary>
        internal static SmartColliderPart Posed(SmartColliderPart part, Transform authoring)
        {
            if (part.bone == null) return part;
            Matrix4x4 d = part.PoseDelta(authoring);
            if (d == Matrix4x4.identity) return part;
            var copy = new SmartColliderPart
            {
                name = part.name,
                color = part.color,
                autoType = part.autoType,
                type = part.type,
                candidates = (ColliderShape[])part.candidates.Clone(),
                reports = part.reports,
                hullTriangles = part.hullTriangles,
                triangleRanges = part.triangleRanges,
                bone = part.bone,
                hullVertices = new Vector3[part.hullVertices.Length],
            };
            for (int i = 0; i < copy.hullVertices.Length; i++) copy.hullVertices[i] = d.MultiplyPoint3x4(part.hullVertices[i]);
            Quaternion rot = d.rotation;
            for (int i = 0; i < copy.candidates.Length; i++)
            {
                copy.candidates[i].Center = d.MultiplyPoint3x4(copy.candidates[i].Center);
                copy.candidates[i].Rotation = rot * (Quaternion)copy.candidates[i].Rotation;
            }
            return copy;
        }

        /// <summary>Container from the previous bake (direct child named like the container), if still present.</summary>
        static GameObject FindContainer(SmartColliderAuthoring a)
        {
            string name = ContainerName(a);
            foreach (var go in a.bakedObjects)
                if (go != null && go.transform.parent == a.transform && go.name == name)
                    return go;
            return null;
        }

        /// <summary>
        /// Deletes hull meshes in the bank that no baked collider references any more (after fewer hulls were baked).
        /// Not undoable (asset deletion) — explicit user action only.
        /// </summary>
        /// <returns>Number of meshes removed.</returns>
        public static int CleanUnusedMeshes(SmartColliderAuthoring authoring)
        {
            var bank = authoring.meshBank;
            if (bank == null) return 0;
            var used = new System.Collections.Generic.HashSet<Mesh>();
            foreach (var c in authoring.bakedColliders)
                if (c is MeshCollider mc && mc.sharedMesh != null) used.Add(mc.sharedMesh);

            int removed = 0;
            for (int i = bank.meshes.Count - 1; i >= 0; i--)
            {
                var mesh = bank.meshes[i];
                if (mesh != null && used.Contains(mesh)) continue;
                if (mesh != null)
                {
                    AssetDatabase.RemoveObjectFromAsset(mesh);
                    Object.DestroyImmediate(mesh, true);
                }
                bank.meshes.RemoveAt(i);
                removed++;
            }
            if (removed > 0)
            {
                // List compacts: used meshes keep their order, so the next bake reuses them.
                EditorUtility.SetDirty(bank);
                AssetDatabase.SaveAssetIfDirty(bank);
            }
            return removed;
        }

        /// <summary>Number of bank meshes not referenced by the current baked colliders.</summary>
        public static int CountUnusedMeshes(SmartColliderAuthoring authoring)
        {
            if (authoring.meshBank == null) return 0;
            var used = new System.Collections.Generic.HashSet<Mesh>();
            foreach (var c in authoring.bakedColliders)
                if (c is MeshCollider mc && mc.sharedMesh != null) used.Add(mc.sharedMesh);
            int n = 0;
            foreach (var m in authoring.meshBank.meshes) if (m == null || !used.Contains(m)) n++;
            return n;
        }

        static string ContainerName(SmartColliderAuthoring a) =>
            string.IsNullOrWhiteSpace(a.containerName) ? "Colliders" : a.containerName;

        static GameObject CreateChild(SmartColliderAuthoring authoring, Transform parent, string name, float3 localPos, quaternion localRot)
        {
            var go = new GameObject(name);
            go.layer = parent.gameObject.layer;
            GameObjectUtility.SetStaticEditorFlags(go, GameObjectUtility.GetStaticEditorFlags(parent.gameObject));
            go.transform.SetParent(parent, false);
            go.transform.SetLocalPositionAndRotation(localPos, localRot);
            Undo.RegisterCreatedObjectUndo(go, "Bake Smart Colliders");
            authoring.bakedObjects.Add(go);
            return go;
        }

        /// <summary>
        /// Unity primitive colliders cannot rotate. True if the shape's rotation is a signed axis
        /// permutation (box), or the capsule axis lies on a local axis.
        /// </summary>
        internal static bool TryAxisAligned(in ColliderShape shape, out float3x3 r)
        {
            r = new float3x3(shape.Rotation);
            switch (shape.Type)
            {
                case ShapeType.Sphere:
                    return true;
                case ShapeType.Capsule:
                    return IsUnitAxis(r.c1);
                case ShapeType.Box:
                    return IsUnitAxis(r.c0) && IsUnitAxis(r.c1) && IsUnitAxis(r.c2);
                default:
                    return false;
            }
        }

        static bool IsUnitAxis(float3 v) => math.cmax(math.abs(v)) > 1f - AxisAlignedTolerance;

        static Collider AddPrimitive(GameObject go, in ColliderShape shape, float3 center, float3x3 r)
        {
            switch (shape.Type)
            {
                case ShapeType.Box:
                {
                    var c = Undo.AddComponent<BoxCollider>(go);
                    float3x3 abs = new float3x3(math.abs(r.c0), math.abs(r.c1), math.abs(r.c2));
                    c.center = center;
                    c.size = math.mul(abs, shape.HalfExtents) * 2f;
                    return c;
                }
                case ShapeType.Sphere:
                {
                    var c = Undo.AddComponent<SphereCollider>(go);
                    c.center = center;
                    c.radius = shape.Radius;
                    return c;
                }
                default:
                {
                    var c = Undo.AddComponent<CapsuleCollider>(go);
                    float3 axis = math.abs(r.c1);
                    c.direction = axis.x >= axis.y && axis.x >= axis.z ? 0 : axis.y >= axis.z ? 1 : 2;
                    c.center = center;
                    c.radius = shape.Radius;
                    c.height = 2f * (shape.HalfHeight + shape.Radius);
                    return c;
                }
            }
        }

        // ---------- placement ----------

        /// <summary>Container mode: one named child per part under the container (identity relative to authoring).</summary>
        static Collider BakeIntoContainer(SmartColliderAuthoring a, Transform container, SmartColliderPart part, ref int hullIndex)
        {
            if (part.type == ShapeType.ConvexHull)
            {
                if (part.hullVertices.Length < 4) return null;
                var go = CreateChild(a, container, part.name, float3.zero, quaternion.identity);
                return AddHull(go, WriteHullMesh(a, hullIndex++, part.hullVertices, part.hullTriangles));
            }
            var s = part.Shape;
            var child = CreateChild(a, container, part.name, s.Center, s.Rotation);
            return AddPrimitive(child, s, float3.zero, float3x3.identity);
        }

        /// <summary>
        /// Places a part on <paramref name="host"/> (authoring object or the mesh's own GameObject), converting
        /// from authoring space to host-local space exactly. Primitives that stay axis-aligned in host space go
        /// on the host; rotated ones get a child (only under uniform host scale — otherwise the child would shear),
        /// anything not representable becomes a convex MeshCollider.
        /// </summary>
        static Collider BakeOnHost(SmartColliderAuthoring a, Transform host, SmartColliderPart part, ref int hullIndex)
        {
            Matrix4x4 l = host.worldToLocalMatrix * a.transform.localToWorldMatrix;
            bool uniformHost = IsUniform(host.lossyScale);
            var s = part.Shape;

            switch (part.type)
            {
                case ShapeType.ConvexHull:
                {
                    if (part.hullVertices.Length < 4) return null;
                    var verts = new Vector3[part.hullVertices.Length];
                    for (int i = 0; i < verts.Length; i++) verts[i] = l.MultiplyPoint3x4(part.hullVertices[i]);
                    return AddHull(host.gameObject, WriteHullMesh(a, hullIndex++, verts, part.hullTriangles));
                }

                case ShapeType.Sphere:
                {
                    // Keep the world radius: Unity scales sphere radius by the max lossy scale component.
                    float world = s.Radius * MaxAbs(a.transform.lossyScale);
                    var c = Undo.AddComponent<SphereCollider>(host.gameObject);
                    c.center = l.MultiplyPoint3x4(s.Center);
                    c.radius = world / Mathf.Max(MaxAbs(host.lossyScale), 1e-6f);
                    return c;
                }

                case ShapeType.Box:
                {
                    float3x3 r = new float3x3(s.Rotation);
                    Vector3 a0 = l.MultiplyVector(r.c0 * s.HalfExtents.x);
                    Vector3 a1 = l.MultiplyVector(r.c1 * s.HalfExtents.y);
                    Vector3 a2 = l.MultiplyVector(r.c2 * s.HalfExtents.z);
                    if (!Orthogonal(a0, a1, a2)) break;
                    Vector3 he = new Vector3(a0.magnitude, a1.magnitude, a2.magnitude);
                    Vector3 d0 = a0 / Mathf.Max(he.x, 1e-12f), d1 = a1 / Mathf.Max(he.y, 1e-12f);
                    Vector3 center = l.MultiplyPoint3x4(s.Center);

                    if (IsUnitAxis(d0) && IsUnitAxis(d1))
                    {
                        var c = Undo.AddComponent<BoxCollider>(host.gameObject);
                        Vector3 size = Vector3.zero;
                        size[AxisIndex(d0)] = 2f * he.x;
                        size[AxisIndex(d1)] = 2f * he.y;
                        size[3 - AxisIndex(d0) - AxisIndex(d1)] = 2f * he.z;
                        c.center = center;
                        c.size = size;
                        return c;
                    }
                    if (!uniformHost) break;
                    var go = CreateChild(a, host, $"{part.name} Collider", center, Quaternion.LookRotation(Vector3.Cross(d0, d1), d1));
                    var bc = Undo.AddComponent<BoxCollider>(go);
                    bc.size = 2f * he;
                    return bc;
                }

                case ShapeType.Capsule:
                {
                    float3x3 r = new float3x3(s.Rotation);
                    Vector3 u = l.MultiplyVector(r.c0), d = l.MultiplyVector(r.c1), v = l.MultiplyVector(r.c2);
                    // Cross-section must stay a circle: radial axes equal length and all mutually orthogonal.
                    if (!Orthogonal(u, d, v) || Mathf.Abs(u.magnitude - v.magnitude) > 1e-3f * u.magnitude) break;
                    Vector3 dir = d.normalized;
                    float radius = s.Radius * u.magnitude;
                    float height = 2f * (s.HalfHeight * d.magnitude + radius);
                    Vector3 center = l.MultiplyPoint3x4(s.Center);

                    if (IsUnitAxis(dir))
                    {
                        var c = Undo.AddComponent<CapsuleCollider>(host.gameObject);
                        c.direction = AxisIndex(dir);
                        c.center = center;
                        c.radius = radius;
                        c.height = height;
                        return c;
                    }
                    if (!uniformHost) break;
                    var go = CreateChild(a, host, $"{part.name} Collider", center, Quaternion.FromToRotation(Vector3.up, dir));
                    var cc = Undo.AddComponent<CapsuleCollider>(go);
                    cc.direction = 1;
                    cc.radius = radius;
                    cc.height = height;
                    return cc;
                }
            }

            return ConvexFallback(a, host, part, l, ref hullIndex);
        }

        /// <summary>Primitive not representable on the host (shear / non-uniform scale + rotation) → convex mesh of its surface.</summary>
        static Collider ConvexFallback(SmartColliderAuthoring a, Transform host, SmartColliderPart part, Matrix4x4 l, ref int hullIndex)
        {
            var samples = new Unity.Collections.NativeList<float3>(Unity.Collections.Allocator.Temp);
            part.Shape.SampleSurface(samples, 8);
            for (int i = 0; i < samples.Length; i++) samples[i] = l.MultiplyPoint3x4(samples[i]);
            var hv = new Unity.Collections.NativeList<float3>(Unity.Collections.Allocator.Temp);
            var ht = new Unity.Collections.NativeList<int>(Unity.Collections.Allocator.Temp);
            try
            {
                if (!ConvexHull3D.Build(samples.AsArray(), 64, hv, ht)) return null;
                var mesh = WriteHullMesh(a, hullIndex++, hv.AsArray().Reinterpret<Vector3>().ToArray(), ht.AsArray().ToArray());
                return AddHull(host.gameObject, mesh);
            }
            finally
            {
                samples.Dispose();
                hv.Dispose();
                ht.Dispose();
            }
        }

        static MeshCollider AddHull(GameObject go, Mesh mesh)
        {
            var mc = Undo.AddComponent<MeshCollider>(go);
            mc.sharedMesh = mesh;
            mc.convex = true;
            return mc;
        }

        static bool IsUniform(Vector3 s)
        {
            Vector3 a = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
            float max = Mathf.Max(a.x, Mathf.Max(a.y, a.z)), min = Mathf.Min(a.x, Mathf.Min(a.y, a.z));
            return max > 0f && (max - min) / max < 1e-3f;
        }

        static float MaxAbs(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z)));

        static bool Orthogonal(Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 na = a.normalized, nb = b.normalized, nc = c.normalized;
            return Mathf.Abs(Vector3.Dot(na, nb)) < 1e-3f && Mathf.Abs(Vector3.Dot(na, nc)) < 1e-3f && Mathf.Abs(Vector3.Dot(nb, nc)) < 1e-3f;
        }

        static bool IsUnitAxis(Vector3 v) => IsUnitAxis((float3)v.normalized);

        static int AxisIndex(Vector3 v)
        {
            Vector3 a = new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
            return a.x >= a.y && a.x >= a.z ? 0 : a.y >= a.z ? 1 : 2;
        }

        // ---------- hull mesh assets ----------

        static Mesh WriteHullMesh(SmartColliderAuthoring authoring, int index, Vector3[] vertices, int[] triangles)
        {
            var bank = EnsureBank(authoring);
            Undo.RecordObject(bank, "Bake Smart Colliders");

            Mesh mesh = index < bank.meshes.Count ? bank.meshes[index] : null;
            if (mesh == null)
            {
                mesh = new Mesh();
                AssetDatabase.AddObjectToAsset(mesh, bank);
                if (index < bank.meshes.Count) bank.meshes[index] = mesh;
                else bank.meshes.Add(mesh);
            }

            // In-place update keeps references from earlier bakes / undo states valid.
            mesh.Clear();
            mesh.name = $"{authoring.name} Hull {index}";
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            EditorUtility.SetDirty(mesh);
            EditorUtility.SetDirty(bank);
            return mesh;
        }

        /// <summary>Batch: names mesh banks after the asset being processed (prefab contents live in a nameless preview scene).</summary>
        internal static string BankOwnerOverride;

        static SmartColliderMeshBank EnsureBank(SmartColliderAuthoring authoring)
        {
            if (authoring.meshBank != null) return authoring.meshBank;

            string folder = SmartCollidersProjectSettings.instance.GeneratedFolder;
            EnsureFolder(folder);

            string owner = authoring.gameObject.scene.IsValid() ? authoring.gameObject.scene.name : null;
            var stage = PrefabStageUtility.GetPrefabStage(authoring.gameObject);
            if (stage != null) owner = Path.GetFileNameWithoutExtension(stage.assetPath);
            if (!string.IsNullOrEmpty(BankOwnerOverride)) owner = BankOwnerOverride;
            if (string.IsNullOrEmpty(owner)) owner = "Untitled";

            string fileName = Sanitize($"{owner}_{authoring.name} Colliders");
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{fileName}.asset");

            var bank = ScriptableObject.CreateInstance<SmartColliderMeshBank>();
            AssetDatabase.CreateAsset(bank, path);
            authoring.meshBank = bank;
            return bank;
        }

        internal static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        static string Sanitize(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
    }
}
