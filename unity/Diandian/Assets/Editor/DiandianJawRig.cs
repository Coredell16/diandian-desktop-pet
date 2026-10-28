using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Seam traced on this Tripo GLB, in glTFast's imported coordinates (its X is flipped).
// Split the lower mandible along its closed seam; never rotate the upper hooked tip.
public sealed class DiandianJawRig
{
    public Transform jaw;
    public Vector3 axis = Vector3.right;
    public const float MaxOpenDegrees = 6f;
    int jawIndex;
    float[] influence;

    static float Fade(float a, float b, float v) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(a, b, v));

    static float SignedRegion(Vector3 p)
    {
        float seam = Mathf.Max(.742f, .756f - (p.z - .270f) * .70f);
        float hookBack = .290f + (p.y - .730f) * .47f;
        return Mathf.Min(Mathf.Min(seam - p.y, hookBack - p.z),
            Mathf.Min(Mathf.Min(p.z - .280f, p.y - .719f), .026f - Mathf.Abs(p.x + .020f)));
    }

    // The yellow cheek behind the beak is not part of the mandible. Keep its
    // entire seam fixed; only the front horn surface opens, with a soft hinge.
    static float Influence(Vector3 p) => Fade(.283f, .289f, p.z) * Fade(.719f, .730f, p.y)
        * (1f - Fade(.013f, .021f, Mathf.Abs(p.x + .020f)));

    public static DiandianJawRig Build(Transform space, Transform head, Mesh mesh, List<Transform> bones)
    {
        if (!space || !head || !mesh || bones == null || !bones.Contains(head))
            throw new ArgumentException("下喙绑定需要有效的空间、头骨骼、网格及骨骼列表。");
        if (mesh.subMeshCount != 1 || mesh.GetTopology(0) != MeshTopology.Triangles)
            throw new InvalidOperationException("下喙切缝只适用于已核对的 Tripo 单三角网格。");

        int originalCount = mesh.vertexCount;
        var vertices = new List<Vector3>(mesh.vertices);
        var normals = new List<Vector3>(mesh.normals);
        var uv = new List<Vector2>(mesh.uv);
        if (normals.Count != originalCount || uv.Count != originalCount)
            throw new InvalidOperationException("下喙切缝必须保留原始法线与 UV。");
        var tangents = new List<Vector4>(mesh.tangents);
        var colors = new List<Color>(mesh.colors);
        bool hasTangents = tangents.Count == originalCount, hasColors = colors.Count == originalCount;
        var signs = new float[originalCount];
        var weights = new List<float>(originalCount + 1024);
        int selected = 0;
        for (int i = 0; i < originalCount; i++)
        {
            signs[i] = SignedRegion(vertices[i]);
            bool inside = signs[i] >= 0f;
            if (inside) selected++;
            weights.Add(inside ? Influence(vertices[i]) : 0f);
        }
        if (selected < 20 || selected > originalCount / 20)
            throw new InvalidOperationException("下喙选区与核对过的模型不一致，停止自动切缝。");

        // Every original vertex/UV keeps its index. Only seam vertices and dark lining are appended.
        var cuts = new Dictionary<ulong, Vector2Int>();
        int AddVertex(Vector3 p, Vector3 normal, Vector2 tex, Vector4 tangent, Color color, float weight)
        {
            int index = vertices.Count;
            vertices.Add(p); normals.Add(normal); uv.Add(tex); weights.Add(weight);
            if (hasTangents) tangents.Add(tangent);
            if (hasColors) colors.Add(color);
            return index;
        }
        Vector2Int Cut(int a, int b)
        {
            int low = Math.Min(a, b), high = Math.Max(a, b);
            ulong key = ((ulong)(uint)low << 32) | (uint)high;
            if (cuts.TryGetValue(key, out Vector2Int cached)) return cached;
            float t = signs[low] / (signs[low] - signs[high]);
            Vector3 p = Vector3.LerpUnclamped(vertices[low], vertices[high], t);
            Vector3 n = Vector3.Lerp(normals[low], normals[high], t).normalized;
            Vector2 tex = Vector2.Lerp(uv[low], uv[high], t);
            Vector4 tan = hasTangents ? Vector4.Lerp(tangents[low], tangents[high], t) : Vector4.zero;
            Color col = hasColors ? Color.Lerp(colors[low], colors[high], t) : Color.white;
            int fixedSide = AddVertex(p, n, tex, tan, col, 0f);
            int movingSide = AddVertex(p, n, tex, tan, col, Influence(p));
            cached = new Vector2Int(fixedSide, movingSide);
            cuts.Add(key, cached);
            return cached;
        }

        var triangles = new List<int>(mesh.triangles.Length + 4096);
        var polygon = new List<int>(4);
        void ClippedTriangle(int a, int b, int c, bool inside)
        {
            polygon.Clear();
            void Edge(int from, int to)
            {
                bool fromInside = signs[from] >= 0f, toInside = signs[to] >= 0f;
                if (fromInside == inside) polygon.Add(from);
                if (fromInside != toInside)
                {
                    Vector2Int cut = Cut(from, to);
                    polygon.Add(inside ? cut.y : cut.x);
                }
            }
            Edge(a, b); Edge(b, c); Edge(c, a);
            for (int i = 1; i + 1 < polygon.Count; i++)
            { triangles.Add(polygon[0]); triangles.Add(polygon[i]); triangles.Add(polygon[i + 1]); }
        }

        // This UV lies in the authored dark mouth seam (9x9 sampled mean RGB ~14/255).
        // The lining is zero-area when closed, and covers only the tiny opening when the jaw moves.
        Vector2 mouthUV = new Vector2(.4597168f, .4160156f);
        void Lining(Vector2Int a, Vector2Int b)
        {
            if (Mathf.Max(weights[a.y], weights[b.y]) < .001f) return;
            int start = vertices.Count;
            foreach (int index in new[] { a.x, b.x, b.y, a.y })
                AddVertex(vertices[index], normals[index], mouthUV,
                    hasTangents ? tangents[index] : Vector4.zero, Color.white, weights[index]);
            triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3,
                start + 2, start + 1, start, start + 3, start + 2, start });
        }
        int[] source = mesh.triangles;
        int splitTriangles = 0;
        for (int i = 0; i < source.Length; i += 3)
        {
            int a = source[i], b = source[i + 1], c = source[i + 2];
            int count = (signs[a] >= 0 ? 1 : 0) + (signs[b] >= 0 ? 1 : 0) + (signs[c] >= 0 ? 1 : 0);
            if (count == 0 || count == 3)
            { triangles.Add(a); triangles.Add(b); triangles.Add(c); continue; }
            splitTriangles++;
            ClippedTriangle(a, b, c, false);
            ClippedTriangle(a, b, c, true);
            Vector2Int first = default, second = default;
            int found = 0;
            void Boundary(int x, int y)
            {
                if ((signs[x] >= 0f) == (signs[y] >= 0f)) return;
                if (found++ == 0) first = Cut(x, y); else second = Cut(x, y);
            }
            Boundary(a, b); Boundary(b, c); Boundary(c, a);
            if (found == 2) Lining(first, second);
        }
        if (splitTriangles == 0) throw new InvalidOperationException("没有找到闭合喙缝，停止创建下喙动作。");
        mesh.indexFormat = IndexFormat.UInt32;
        mesh.SetVertices(vertices); mesh.SetNormals(normals); mesh.SetUVs(0, uv);
        if (hasTangents) mesh.SetTangents(tangents);
        if (hasColors) mesh.SetColors(colors);
        mesh.SetTriangles(triangles, 0);

        var bone = new GameObject("下喙_沿闭嘴缝分离").transform;
        bone.SetParent(head, false);
        bone.position = space.TransformPoint(new Vector3(-.020f, .746f, .269f));
        bone.rotation = space.rotation;
        var result = new DiandianJawRig { jaw = bone, jawIndex = bones.Count, influence = weights.ToArray() };
        bones.Add(bone);
        Debug.Log("DIANDIAN_JAW_SEAM vertices=" + selected + " splitTriangles=" + splitTriangles
            + " addedVertices=" + (vertices.Count - originalCount));
        return result;
    }

    public void ApplyWeights(BoneWeight[] weights)
    {
        if (weights == null || weights.Length != influence.Length)
            throw new ArgumentException("请在下喙切缝完成后，按新的 vertexCount 创建权重数组。");
        for (int i = 0; i < weights.Length; i++)
        {
            float jawWeight = influence[i];
            if (jawWeight <= 0f) continue;
            BoneWeight w = weights[i];
            if (w.weight3 > .00001f)
                throw new InvalidOperationException("下喙选区已有四个骨骼影响，不能静默丢弃权重。");
            w.weight0 *= 1f - jawWeight; w.weight1 *= 1f - jawWeight; w.weight2 *= 1f - jawWeight;
            w.boneIndex3 = jawIndex; w.weight3 = jawWeight;
            void Swap(ref float left, ref int leftIndex, ref float right, ref int rightIndex)
            {
                if (left >= right) return;
                (left, right) = (right, left); (leftIndex, rightIndex) = (rightIndex, leftIndex);
            }
            // BoneWeight fields are properties, so sort four scalar slots before storing them.
            float a=w.weight0,b=w.weight1,c=w.weight2,d=w.weight3;
            int ai=w.boneIndex0,bi=w.boneIndex1,ci=w.boneIndex2,di=w.boneIndex3;
            Swap(ref a,ref ai,ref b,ref bi); Swap(ref c,ref ci,ref d,ref di);
            Swap(ref a,ref ai,ref c,ref ci); Swap(ref b,ref bi,ref d,ref di); Swap(ref b,ref bi,ref c,ref ci);
            float sum=a+b+c+d;
            if (!float.IsFinite(sum) || sum <= 0f) throw new InvalidOperationException("下喙权重无效。");
            weights[i]=new BoneWeight {boneIndex0=ai,weight0=a/sum,boneIndex1=bi,weight1=b/sum,
                boneIndex2=ci,weight2=c/sum,boneIndex3=di,weight3=d/sum};
        }
    }
}
