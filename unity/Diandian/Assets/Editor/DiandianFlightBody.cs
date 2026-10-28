using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// The closed-wing Tripo mesh remains the rest pose. This separate mesh removes
// its fused wings and supplies a continuous feathered torso underneath them.
public static class DiandianFlightBody
{
    const string MeshPath = "Assets/Tripo/点点飞行躯干.asset";
    const string MaterialPath = "Assets/Tripo/点点翼下羽毛.mat";
    // Native GLB coordinates, (z,y), traced against the original 4K UV texture.
    // Remove the folded wing panel/tips, not the neck, breast, dorsal plumage or
    // the small scapular coverts that conceal the new articulated wing root.
    static readonly Vector2[] WingOutline = {
        new Vector2(-.235f,.33f), new Vector2(-.145f,.37f),
        new Vector2(-.095f,.405f), new Vector2(.025f,.44f),
        new Vector2(.105f,.47f), new Vector2(.16f,.515f),
        new Vector2(.195f,.575f), new Vector2(.20f,.635f),
        new Vector2(.18f,.665f), new Vector2(.12f,.68f),
        new Vector2(.07f,.67f), new Vector2(0,.63f),
        new Vector2(-.065f,.56f), new Vector2(-.14f,.47f), new Vector2(-.23f,.40f)
    };
    // y, center z, horizontal radius, front/back radius. End rings sit inside
    // retained rump/neck geometry; caps close the new surface, not cut-edge fans.
    static readonly Vector4[] BodySections = {
        new Vector4(.27f,-.170f,.020f,.019f), new Vector4(.30f,-.135f,.025f,.038f),
        new Vector4(.34f,-.102f,.039f,.060f), new Vector4(.38f,-.065f,.060f,.084f),
        new Vector4(.40f,-.058f,.068f,.096f), new Vector4(.45f,.005f,.087f,.124f),
        new Vector4(.50f,.046f,.100f,.138f), new Vector4(.55f,.081f,.108f,.142f),
        new Vector4(.60f,.104f,.104f,.132f), new Vector4(.65f,.124f,.088f,.112f),
        new Vector4(.70f,.155f,.058f,.091f), new Vector4(.73f,.170f,.047f,.070f),
        new Vector4(.76f,.183f,.035f,.050f)
    };

    // Face ownership stays independent of articulation. This is the same source
    // pearl-cap selection, stopping below the head and before the breast midline.
    static float ShoulderSelectionDistance(Vector3 p)
    {
        float y = (p.y - .652f) / .056f;
        float z = (p.z - (.142f + .7f * (p.y - .652f))) / .092f;
        float distance = (1 - Mathf.Sqrt(y * y + z * z)) * .056f;
        distance = Mathf.Min(distance, Mathf.Abs(p.x) - .068f);
        distance = Mathf.Min(distance, Mathf.Min(p.z - .045f, .217f - p.z));
        return distance;
    }

    public static bool IsShoulderTriangle(Vector3 a, Vector3 b, Vector3 c) =>
        ShoulderSelectionDistance(a) > 0 || ShoulderSelectionDistance(b) > 0 || ShoulderSelectionDistance(c) > 0;

    public static bool IsProtectedNormalPlumage(Vector3 p, Vector3 normal)
    {
        if (p.y < .46f || Mathf.Abs(normal.x) >= .25f || Mathf.Abs(normal.z) <= .82f) return false;
        Vector4 body = Section(p.y);
        // Folded flight feathers also have front/back-facing bevels. Normals
        // alone therefore preserved a hard closed-wing frame on the body.
        if (Mathf.Abs(p.x) > body.z * .98f) return false;
        return normal.z > 0 ? p.z >= body.y + body.w * .75f : p.z <= body.y - body.w * .75f;
    }

    public static Mesh Build(Mesh source)
    {
        if (!source) throw new ArgumentNullException(nameof(source));
        if (!source.isReadable || source.subMeshCount != 1 || source.bindposes.Length != 26 ||
            source.GetTopology(0) != MeshTopology.Triangles || source.blendShapeCount != 0)
            throw new InvalidOperationException("飞行躯干需要可读的原始26骨单子网格，不能再次裁切已生成的躯干。");
        if (AssetDatabase.GetAssetPath(source) == MeshPath)
            throw new InvalidOperationException("必须从静止网格生成飞行躯干。");
        var positions = new List<Vector3>(source.vertices);
        var normals = new List<Vector3>(source.normals);
        var uv = new List<Vector2>(source.uv);
        var tangents = new List<Vector4>(source.tangents);
        var weights = new List<BoneWeight>(source.boneWeights);
        int originalVertices = positions.Count;
        if (normals.Count != originalVertices || uv.Count != originalVertices ||
            tangents.Count != originalVertices || weights.Count != originalVertices)
            throw new InvalidOperationException("飞行躯干需要保留原始法线、UV、切线及骨骼权重。");
        var sourceIndices = source.GetTriangles(0);
        var kept = new List<int>(sourceIndices.Length);
        var fill = new List<int>();
        var distances = new float[originalVertices];
        int selectedVertices = 0, preservedPlumage = 0;
        for (int i = 0; i < originalVertices; i++)
        {
            distances[i] = WingDistance(positions[i]);
            if (PreserveOriginalPlumage(positions[i], normals[i], weights[i]))
            {
                if (distances[i] > 0) preservedPlumage++;
                distances[i] = Mathf.Min(distances[i], -.001f);
            }
            // Preserve the already-corrected legs, ankles and toes even at a
            // feather/leg junction that is indistinguishable by position alone.
            if (FootWeight(weights[i]) > .1f) distances[i] = Mathf.Min(distances[i], -.001f);
            if (distances[i] > 0) selectedVertices++;
        }
        var edges = new Dictionary<ulong, int>();
        var cutEdges = new List<Vector2Int>();
        var shoulderTriangles = new bool[sourceIndices.Length / 3];
        var shoulderEdges = new Dictionary<ulong, (Vector2Int edge, int count)>();
        var shoulderWeld = new Dictionary<Vector3Int, int>();
        int shoulderCount = 0;
        int ShoulderVertex(int source)
        {
            Vector3 p = positions[source] * 1000000;
            var key = new Vector3Int(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y), Mathf.RoundToInt(p.z));
            if (!shoulderWeld.TryGetValue(key, out int canonical)) shoulderWeld.Add(key, canonical = source);
            return canonical;
        }
        void ShoulderEdge(int a, int b)
        {
            int pa = ShoulderVertex(a), pb = ShoulderVertex(b);
            ulong key = ((ulong)(uint)Mathf.Min(pa, pb) << 32) | (uint)Mathf.Max(pa, pb);
            if (shoulderEdges.TryGetValue(key, out var old)) shoulderEdges[key] = (old.edge, old.count + 1);
            else shoulderEdges.Add(key, (new Vector2Int(a, b), 1));
        }
        for (int i = 0; i < sourceIndices.Length; i += 3)
        {
            int a = sourceIndices[i], b = sourceIndices[i + 1], c = sourceIndices[i + 2];
            if (!IsShoulderTriangle(positions[a], positions[b], positions[c])) continue;
            shoulderTriangles[i / 3] = true; shoulderCount++;
            ShoulderEdge(a, b); ShoulderEdge(b, c); ShoulderEdge(c, a);
        }
        foreach (var edge in shoulderEdges.Values)
            if (edge.count == 1) cutEdges.Add(new Vector2Int(edge.edge.y, edge.edge.x));
        int shoulderBoundaryEdges = cutEdges.Count;
        var polygon = new int[4];
        int removedTriangles = 0, clippedTriangles = 0, unchangedTriangles = 0;
        int Intersection(int a, int b)
        {
            float t = distances[a] / (distances[a] - distances[b]);
            if (t < .000001f) return a;
            if (t > .999999f) return b;
            ulong key = ((ulong)(uint)Mathf.Min(a, b) << 32) | (uint)Mathf.Max(a, b);
            if (edges.TryGetValue(key, out int existing)) return existing;
            int index = positions.Count;
            positions.Add(Vector3.LerpUnclamped(positions[a], positions[b], t));
            normals.Add(Vector3.LerpUnclamped(normals[a], normals[b], t).normalized);
            uv.Add(Vector2.LerpUnclamped(uv[a], uv[b], t));
            Vector4 tangent = Vector4.LerpUnclamped(tangents[a], tangents[b], t);
            Vector3 direction = new Vector3(tangent.x, tangent.y, tangent.z).normalized;
            tangents.Add(new Vector4(direction.x, direction.y, direction.z, tangent.w < 0 ? -1 : 1));
            weights.Add(InterpolateWeight(weights[a], weights[b], t));
            edges.Add(key, index);
            return index;
        }
        for (int start = 0; start < sourceIndices.Length; start += 3)
        {
            // The wing generator reuses this exact set of source faces/UVs.
            // They must not remain on the body as a second immobile pearl cap.
            if (shoulderTriangles[start / 3]) continue;
            int a = sourceIndices[start], b = sourceIndices[start + 1], c = sourceIndices[start + 2];
            int removed = (distances[a] > 0 ? 1 : 0) + (distances[b] > 0 ? 1 : 0) + (distances[c] > 0 ? 1 : 0);
            if (removed == 0)
            {
                kept.Add(a); kept.Add(b); kept.Add(c); unchangedTriangles++;
                continue;
            }
            if (removed == 3) { removedTriangles++; continue; }
            clippedTriangles++;
            int count = 0, entering = -1, leaving = -1;
            for (int corner = 0; corner < 3; corner++)
            {
                int current = sourceIndices[start + corner], next = sourceIndices[start + (corner + 1) % 3];
                bool inside = distances[current] <= 0;
                if (inside) polygon[count++] = current;
                if (inside != (distances[next] <= 0))
                {
                    int intersection = Intersection(current, next);
                    polygon[count++] = intersection;
                    if (inside) leaving = intersection; else entering = intersection;
                }
            }
            // This directed edge is the newly exposed edge of the retained face.
            if (entering >= 0 && leaving >= 0 && entering != leaving)
                cutEdges.Add(new Vector2Int(leaving, entering));
            for (int i = 1; i + 1 < count; i++)
            {
                int p = polygon[0], q = polygon[i], r = polygon[i + 1];
                // A preserved feather boundary can leave a small but real face.
                // An absolute area cutoff erased isolated protected back vertices;
                // reject only collapsed triangles, not valid clipped geometry.
                if (p == q || q == r || r == p ||
                    Vector3.Cross(positions[q] - positions[p], positions[r] - positions[p]).sqrMagnitude == 0f) continue;
                kept.Add(p); kept.Add(q); kept.Add(r);
            }
        }
        int clippedVertices = positions.Count - originalVertices;
        AddTorso(positions, normals, uv, tangents, weights, fill);
        int torsoVertices = positions.Count - originalVertices - clippedVertices;
        int torsoTriangles = fill.Count / 3;
        float widestBridge = AddCutCollars(cutEdges, shoulderBoundaryEdges, positions, normals, uv, tangents, weights, fill);
        Validate(positions, weights, kept, fill, originalVertices, selectedVertices);

        var result = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
        bool create = !result;
        if (create) result = new Mesh();
        // Mesh setters invalidate Unity 6's cached skinning vertex layout.
        result.Clear(false); result.name = "点点飞行躯干"; result.indexFormat = IndexFormat.UInt32;
        result.SetVertices(positions); result.SetNormals(normals); result.SetUVs(0, uv); result.SetTangents(tangents);
        result.boneWeights = weights.ToArray(); result.bindposes = source.bindposes;
        result.subMeshCount = 2;
        result.SetTriangles(kept, 0, false); result.SetTriangles(fill, 1, false); result.RecalculateBounds();
        if (create) AssetDatabase.CreateAsset(result, MeshPath);
        EditorUtility.SetDirty(result); AssetDatabase.SaveAssetIfDirty(result);
        Debug.Log("DIANDIAN_FLIGHT_BODY_OK sourceVertices=" + originalVertices + " selectedVertices=" + selectedVertices +
            " plumageProtectedFromCut=" + preservedPlumage +
            " transferredShoulderTriangles=" + shoulderCount + " shoulderBoundaryEdges=" + shoulderBoundaryEdges +
            " sourceTriangles=" + sourceIndices.Length / 3 + " unchangedTriangles=" + unchangedTriangles +
            " removedTriangles=" + removedTriangles + " clippedTriangles=" + clippedTriangles +
            " cutVertices=" + clippedVertices + " retainedTriangles=" + kept.Count / 3 +
            " torsoVertices=" + torsoVertices + " torsoTriangles=" + torsoTriangles +
            " joinedCutEdges=" + cutEdges.Count + " collarTriangles=" + (fill.Count / 3 - torsoTriangles) +
            " maxBridge=" + widestBridge.ToString("F5"));
        return result;
    }

    public static Material BodyMaterial()
    {
        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Textures/BodyFeathers.png");
        var shader = Shader.Find("Standard");
        if (!texture || !shader) throw new InvalidOperationException("缺少翼下灰羽纹理或 Standard 材质。");
        var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        bool create = !material;
        if (create) material = new Material(shader);
        material.name = "点点翼下羽毛"; material.shader = shader;
        material.mainTexture = texture; material.mainTextureScale = Vector2.one;
        material.color = new Color(.64f, .65f, .63f, 1);
        material.SetFloat("_Metallic", 0); material.SetFloat("_Glossiness", .05f);
        material.SetFloat("_Mode", 0); material.SetInt("_SrcBlend", (int)BlendMode.One);
        material.SetInt("_DstBlend", (int)BlendMode.Zero); material.SetInt("_ZWrite", 1);
        material.DisableKeyword("_ALPHATEST_ON"); material.DisableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON"); material.renderQueue = -1;
        if (create) AssetDatabase.CreateAsset(material, MaterialPath);
        EditorUtility.SetDirty(material); AssetDatabase.SaveAssetIfDirty(material);
        return material;
    }

    static float WingDistance(Vector3 point)
    {
        var p = new Vector2(point.z, point.y);
        float distance = float.MaxValue;
        bool inside = false;
        for (int i = 0, j = WingOutline.Length - 1; i < WingOutline.Length; j = i++)
        {
            Vector2 a = WingOutline[j], b = WingOutline[i], edge = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, edge) / edge.sqrMagnitude);
            distance = Mathf.Min(distance, (p - a - edge * t).magnitude);
            if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)
                inside = !inside;
        }
        float side = point.y < .5f ? Mathf.Lerp(.025f, .05f, Mathf.InverseLerp(.34f, .5f, point.y)) :
            point.y < .61f ? Mathf.Lerp(.05f, .06f, Mathf.InverseLerp(.5f, .61f, point.y)) :
            Mathf.Lerp(.06f, .045f, Mathf.InverseLerp(.61f, .68f, point.y));
        // The fused closed wing has a second, dark feather layer farther in.
        // Four texture-confirmed points (.027,.385,-.160)..(.044,.50,-.065)
        // sat inside the outline but survived the former x cutoff. Restrict the
        // deeper cut to this rear band; the yellow/barred tail is outside it.
        float rearBand = Mathf.SmoothStep(0,1,Mathf.InverseLerp(.365f,.38f,point.y))
            * (1-Mathf.SmoothStep(0,1,Mathf.InverseLerp(.515f,.53f,point.y)))
            * Mathf.SmoothStep(0,1,Mathf.InverseLerp(0,.01f,.9f*point.y-.48f-point.z));
        side = Mathf.Lerp(side,.015f,rearBand);
        distance = Mathf.Min(inside ? distance : -distance, Mathf.Abs(point.x) - side);
        distance = Mathf.Min(distance, Mathf.Max(point.y - .41f, -point.z));
        // The former y<.40 protection also kept fused wing tips at the rump.
        // Keep the actual long-tail region, while rebuilding the upper side rump.
        return Mathf.Min(distance, Mathf.Max(point.y - .34f, Mathf.Abs(point.x) - .04f));
    }

    static bool PreserveOriginalPlumage(Vector3 p, Vector3 normal, BoneWeight weight)
    {
        if (p.y >= .715f) return true;
        // The source rig assigned some upper wing coverts to the head by height.
        // Their old head weight is not anatomical proof that they belong to it.
        if (ShoulderSelectionDistance(p) > 0) return false;
        float head = 0;
        void Head(int bone, float amount) { if (bone == 2 || bone == 3 || bone == 25) head += amount; }
        Head(weight.boneIndex0, weight.weight0); Head(weight.boneIndex1, weight.weight1);
        Head(weight.boneIndex2, weight.weight2); Head(weight.boneIndex3, weight.weight3);
        if (p.y >= .655f || head >= .035f) return true;
        // Keep a small original scapular cap above the new root (.078,.65,.08).
        if (p.y >= .635f && p.z >= .065f) return true;
        if (p.y >= .42f && p.z >= .60f * p.y - .145f) return true; // breast
        if (p.y >= .50f && p.z <= p.y - .59f && Mathf.Abs(p.x) <= .07f) return true; // dorsal strip
        return IsProtectedNormalPlumage(p, normal);
    }

    static float FootWeight(BoneWeight weight)
    {
        float result = 0;
        if (weight.boneIndex0 >= 5 && weight.boneIndex0 < 25) result += weight.weight0;
        if (weight.boneIndex1 >= 5 && weight.boneIndex1 < 25) result += weight.weight1;
        if (weight.boneIndex2 >= 5 && weight.boneIndex2 < 25) result += weight.weight2;
        if (weight.boneIndex3 >= 5 && weight.boneIndex3 < 25) result += weight.weight3;
        return result;
    }

    static BoneWeight InterpolateWeight(BoneWeight a, BoneWeight b, float t)
    {
        var values = new float[26];
        void Add(BoneWeight w, float amount)
        {
            values[w.boneIndex0] += w.weight0 * amount; values[w.boneIndex1] += w.weight1 * amount;
            values[w.boneIndex2] += w.weight2 * amount; values[w.boneIndex3] += w.weight3 * amount;
        }
        Add(a, 1 - t); Add(b, t);
        var indices = new int[4]; var selected = new float[4]; float total = 0;
        for (int i = 0; i < 4; i++)
        {
            int largest = 0;
            for (int j = 1; j < values.Length; j++) if (values[j] > values[largest]) largest = j;
            indices[i] = largest; selected[i] = values[largest]; total += selected[i]; values[largest] = 0;
        }
        return new BoneWeight {
            boneIndex0 = indices[0], weight0 = selected[0] / total,
            boneIndex1 = indices[1], weight1 = selected[1] / total,
            boneIndex2 = indices[2], weight2 = selected[2] / total,
            boneIndex3 = indices[3], weight3 = selected[3] / total
        };
    }

    static Vector4 Section(float y)
    {
        y = Mathf.Clamp(y, BodySections[0].x, BodySections[BodySections.Length - 1].x);
        int i = 0;
        while (i + 2 < BodySections.Length && y > BodySections[i + 1].x) i++;
        Vector4 a = BodySections[i], b = BodySections[i + 1];
        Vector4 before = BodySections[Mathf.Max(0, i - 1)], after = BodySections[Mathf.Min(BodySections.Length - 1, i + 2)];
        float span = b.x - a.x, t = (y - a.x) / span, t2 = t * t, t3 = t2 * t;
        Vector4 startSlope = (b - before) / (b.x - before.x), endSlope = (after - a) / (after.x - a.x);
        // Continuous slopes avoid the ribbed silhouette caused by independently
        // flattening the derivative at every measured section.
        Vector4 result = (2 * t3 - 3 * t2 + 1) * a + (t3 - 2 * t2 + t) * span * startSlope +
            (-2 * t3 + 3 * t2) * b + (t3 - t2) * span * endSlope;
        // The torso is an underlay, never an exterior coat over the original
        // textured breast/back. Only the actual missing wing underside exposes it.
        result.z *= .88f; result.w *= .88f;
        return result;
    }

    static BoneWeight TorsoWeight(Vector3 p)
    {
        float Fade(float a, float b, float value) => Mathf.SmoothStep(0, 1, Mathf.InverseLerp(a, b, value));
        // Match DiandianTripoRigging exactly: the upper neck follows the head,
        // and the lower rear follows the tail instead of remaining on the pelvis.
        float head = Fade(.645f, .735f, p.y);
        float tail = (1 - Fade(.295f, .425f, p.y)) * (1 - Fade(-.125f, -.065f, p.z));
        BoneWeight result;
        if (head > 0)
            result = new BoneWeight { boneIndex0 = 1, weight0 = 1 - head, boneIndex1 = 2, weight1 = head };
        else
        {
            float body = (1 - tail) * Fade(.42f, .52f, p.y);
            result = new BoneWeight { boneIndex0 = 0, weight0 = 1 - tail - body,
                boneIndex1 = 1, weight1 = body, boneIndex2 = 4, weight2 = tail };
        }
        if (result.weight0 < result.weight1) {
            (result.weight0, result.weight1) = (result.weight1, result.weight0);
            (result.boneIndex0, result.boneIndex1) = (result.boneIndex1, result.boneIndex0);
        }
        if (result.weight1 < result.weight2) {
            (result.weight1, result.weight2) = (result.weight2, result.weight1);
            (result.boneIndex1, result.boneIndex2) = (result.boneIndex2, result.boneIndex1);
        }
        if (result.weight0 < result.weight1) {
            (result.weight0, result.weight1) = (result.weight1, result.weight0);
            (result.boneIndex0, result.boneIndex1) = (result.boneIndex1, result.boneIndex0);
        }
        return result;
    }

    // Both the torso and its collars use this same side projection. Cylindrical
    // UVs would collapse every point along a radial collar to the same texel;
    // the small x contribution gives that surface width without an atlas seam.
    static Vector2 BodyUV(Vector3 p) => new Vector2((p.z + .5f * Mathf.Abs(p.x)) * 4f,
        (p.y - BodySections[0].x) * (1.4f / (BodySections[BodySections.Length - 1].x - BodySections[0].x)));

    static Vector4 BodyTangent(Vector3 p, Vector3 normal)
    {
        Vector3 direction = Vector3.Cross(Vector3.up, normal).normalized;
        if (direction.sqrMagnitude < .0001f) direction = Vector3.forward;
        if (Vector3.Dot(direction, new Vector3(.5f * Mathf.Sign(p.x), 0, 1)) < 0) direction = -direction;
        float handedness = Vector3.Dot(Vector3.Cross(normal, direction), Vector3.up) < 0 ? -1 : 1;
        return new Vector4(direction.x, direction.y, direction.z, handedness);
    }

    static void TorsoPoint(float y, float u, out Vector3 position, out Vector3 normal, out Vector2 uv, out Vector4 tangent)
    {
        Vector4 s = Section(y), derivative = (Section(y + .0001f) - Section(y - .0001f)) / .0002f;
        float angle = u * Mathf.PI * 2, cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
        var across = new Vector3(-s.z * sin, 0, s.w * cos);
        var up = new Vector3(derivative.z * cos, 1, derivative.y + derivative.w * sin);
        position = new Vector3(s.z * cos, y, s.y + s.w * sin);
        normal = Vector3.Cross(up, across).normalized;
        tangent = BodyTangent(position, normal);
        uv = BodyUV(position);
    }

    static void AddTorso(List<Vector3> positions, List<Vector3> normals, List<Vector2> uv,
        List<Vector4> tangents, List<BoneWeight> weights, List<int> triangles)
    {
        const int rings = 64, sides = 64;
        int first = positions.Count;
        int Vertex(Vector3 position, Vector3 normal, Vector2 texcoord, Vector4 tangent)
        {
            int index = positions.Count;
            positions.Add(position); normals.Add(normal); uv.Add(texcoord);
            tangents.Add(tangent);
            weights.Add(TorsoWeight(position));
            return index;
        }
        for (int ring = 0; ring <= rings; ring++)
        {
            float v = ring / (float)rings, y = Mathf.Lerp(BodySections[0].x, BodySections[BodySections.Length - 1].x, v);
            for (int side = 0; side <= sides; side++)
            {
                TorsoPoint(y, side / (float)sides, out Vector3 position, out Vector3 normal, out Vector2 texcoord, out Vector4 tangent);
                Vertex(position, normal, texcoord, tangent);
                if (ring == rings || side == sides) continue;
                int a = first + ring * (sides + 1) + side, b = a + 1, c = a + sides + 1, d = c + 1;
                triangles.Add(a); triangles.Add(c); triangles.Add(b);
                triangles.Add(b); triangles.Add(c); triangles.Add(d);
            }
        }
        for (int end = 0; end < 2; end++)
        {
            Vector4 s = BodySections[end == 0 ? 0 : BodySections.Length - 1];
            var position = new Vector3(0, s.x, s.y);
            Vector3 normal = end == 0 ? Vector3.down : Vector3.up;
            int center = Vertex(position, normal, BodyUV(position), BodyTangent(position, normal));
            int edge = first + (end == 0 ? 0 : rings * (sides + 1));
            for (int side = 0; side < sides; side++)
            {
                triangles.Add(center); triangles.Add(edge + side + (end == 0 ? 0 : 1));
                triangles.Add(edge + side + (end == 0 ? 1 : 0));
            }
        }
    }

    static float AddCutCollars(List<Vector2Int> cutEdges, int shoulderBoundaryEdges, List<Vector3> positions, List<Vector3> normals,
        List<Vector2> uv, List<Vector4> tangents, List<BoneWeight> weights, List<int> triangles)
    {
        const int steps = 6;
        var rows = new Dictionary<int, int[]>();
        var parent = new Dictionary<int, int>();
        var welded = new Dictionary<Vector3Int, int>();
        int Root(int vertex)
        {
            if (!parent.TryGetValue(vertex, out int next))
            {
                Vector3 p = positions[vertex] * 1000000;
                var key = new Vector3Int(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y), Mathf.RoundToInt(p.z));
                if (!welded.TryGetValue(key, out next)) { next = vertex; welded.Add(key, vertex); }
                parent.Add(vertex, next);
            }
            if (next != vertex) parent[vertex] = next = Root(next);
            return next;
        }
        foreach (var edge in cutEdges)
        {
            int a = Root(edge.x), b = Root(edge.y);
            if (a != b) parent[b] = a;
        }
        var groups = new Dictionary<int, List<int>>();
        foreach (int source in new List<int>(parent.Keys))
        {
            int root = Root(source);
            if (!groups.TryGetValue(root, out var group)) groups.Add(root, group = new List<int>());
            group.Add(source);
        }
        // Original feather detail and UV seams create many small cut islands.
        // Only the main opening on each side needs a deep bridge to the torso;
        // extruding every island to the core created concentric walls/ribs.
        int leftMain = -1, rightMain = -1;
        float leftSize = 0, rightSize = 0;
        var centers = new Dictionary<int, Vector3>();
        var centerNormals = new Dictionary<int, Vector3>();
        foreach (var pair in groups)
        {
            var bounds = new Bounds(positions[pair.Value[0]], Vector3.zero);
            Vector3 center = Vector3.zero, normal = Vector3.zero;
            foreach (int source in pair.Value)
            { bounds.Encapsulate(positions[source]); center += positions[source]; normal += normals[source]; }
            centers.Add(pair.Key, center / pair.Value.Count);
            centerNormals.Add(pair.Key, normal.normalized);
            float size = bounds.size.sqrMagnitude;
            if (bounds.center.x < 0 && size > leftSize) { leftSize = size; leftMain = pair.Key; }
            if (bounds.center.x >= 0 && size > rightSize) { rightSize = size; rightMain = pair.Key; }
        }
        var deepGroups = new HashSet<int>();
        if (leftMain >= 0) deepGroups.Add(leftMain);
        if (rightMain >= 0) deepGroups.Add(rightMain);
        var shoulderGroups = new HashSet<int>();
        // A transferred shoulder cap exposes a real body opening even when its
        // boundary is disconnected from the larger old-wing opening. It must
        // join the torso, not receive the shallow backing used for feather islands.
        for (int i = 0; i < shoulderBoundaryEdges; i++)
        {
            int group = Root(cutEdges[i].x);
            shoulderGroups.Add(group); deepGroups.Add(group);
        }
        var caps = new Dictionary<int, int>();
        int shallowEdges = 0;
        float widest = 0;
        int[] Row(int source)
        {
            if (rows.TryGetValue(source, out int[] existing)) return existing;
            int group = Root(source);
            bool shallow = !deepGroups.Contains(group);
            Vector3 start = positions[source]; Vector4 s = Section(start.y);
            float u = Mathf.Atan2((start.z - s.y) / s.w, start.x / s.z) / (2 * Mathf.PI);
            if (u < 0) u += 1;
            TorsoPoint(start.y, u, out Vector3 end, out Vector3 normal, out Vector2 texcoord, out Vector4 tangent);
            // Penetrate the closed torso slightly. A boundary-to-surface ribbon
            // cannot leave the view-dependent gaps of two disconnected shells.
            var axis = new Vector3(0, start.y, s.y);
            end = Vector3.Lerp(axis, end, .985f);
            if (shallow) { end = start - centerNormals[group] * .001f; normal = normals[source]; }
            widest = Mathf.Max(widest, Vector3.Distance(start, end));
            BoneWeight endWeight = shallow ? weights[source] : TorsoWeight(end);
            Vector3 chord = end - start;
            // The old straight radial ramps formed a deep, flat rim around the
            // cut wing. Approach both surfaces along their tangent planes, with
            // a continuous curve rather than three straight radial bands.
            Vector3 startSlope = Vector3.ProjectOnPlane(chord, normals[source]) * 1.25f;
            Vector3 endSlope = Vector3.ProjectOnPlane(chord, normal) * 1.25f;
            int segments = shallow ? 1 : steps;
            var result = new int[segments + 1];
            // The boundary position/normal/weight must coincide with the retained
            // original, but its UV belongs to the under-wing material, not Tripo.
            for (int step = 0; step <= segments; step++)
            {
                float t = step / (float)segments;
                result[step] = positions.Count;
                float t2 = t * t, t3 = t2 * t;
                Vector3 point = (2 * t3 - 3 * t2 + 1) * start + (t3 - 2 * t2 + t) * startSlope
                    + (-2 * t3 + 3 * t2) * end + (t3 - t2) * endSlope;
                Vector3 surfaceNormal = step == 0 ? normals[source] : Vector3.Slerp(normals[source], normal, t).normalized;
                positions.Add(point); normals.Add(surfaceNormal);
                uv.Add(BodyUV(point));
                tangents.Add(BodyTangent(point, surfaceNormal));
                weights.Add(step == 0 ? weights[source] : InterpolateWeight(weights[source], endWeight, t));
            }
            rows.Add(source, result);
            return result;
        }
        foreach (Vector2Int edge in cutEdges)
        {
            int[] a = Row(edge.x), b = Row(edge.y);
            for (int step = 0; step < a.Length - 1; step++)
            {
                // Opposite winding along the retained face's exposed edge.
                triangles.Add(b[step]); triangles.Add(a[step]); triangles.Add(a[step + 1]);
                triangles.Add(b[step]); triangles.Add(a[step + 1]); triangles.Add(b[step + 1]);
            }
            int group = Root(edge.x);
            if (deepGroups.Contains(group)) continue;
            shallowEdges++;
            if (!caps.TryGetValue(group, out int cap))
            {
                // A thin backing closes the retained detail without pulling a
                // second wall across the exposed wing underside.
                Vector3 normal = -centerNormals[group];
                Vector3 point = centers[group] + normal * .001f;
                cap = positions.Count; caps.Add(group, cap);
                positions.Add(point); normals.Add(normal); uv.Add(BodyUV(point));
                tangents.Add(BodyTangent(point, normal)); weights.Add(TorsoWeight(point));
            }
            int ca = a[a.Length - 1], cb = b[b.Length - 1];
            if (Vector3.Cross(positions[ca] - positions[cb], positions[cap] - positions[cb]).sqrMagnitude > 0)
            { triangles.Add(cb); triangles.Add(ca); triangles.Add(cap); }
        }
        if (cutEdges.Count == 0) throw new InvalidOperationException("裁翼没有生成可连接的边界。");
        Debug.Log("DIANDIAN_BODY_COLLARS components=" + groups.Count + " mainOpenings="
            + ((leftMain >= 0 ? 1 : 0) + (rightMain >= 0 ? 1 : 0))
            + " shoulderOpeningGroups=" + shoulderGroups.Count + " shoulderBoundaryEdges=" + shoulderBoundaryEdges
            + " deepGroups=" + deepGroups.Count
            + " shallowCaps=" + caps.Count + " shallowEdges=" + shallowEdges + " shallowDepth=0.001");
        return widest;
    }

    static void Validate(List<Vector3> positions, List<BoneWeight> weights, List<int> kept,
        List<int> fill, int originalVertices, int selectedVertices)
    {
        if (kept.Count == 0 || fill.Count == 0 || selectedVertices < 10000 || selectedVertices > originalVertices * .6f)
            throw new InvalidOperationException("飞行裁翼范围与已核对的 Tripo 模型不符。");
        foreach (int index in kept) if (index < 0 || index >= positions.Count) throw new InvalidOperationException("保留网格索引越界。");
        foreach (int index in fill) if (index < 0 || index >= positions.Count) throw new InvalidOperationException("翼下体表索引越界。");
        for (int i = 0; i < positions.Count; i++)
        {
            BoneWeight w = weights[i];
            if (!float.IsFinite(positions[i].sqrMagnitude) ||
                Mathf.Abs(w.weight0 + w.weight1 + w.weight2 + w.weight3 - 1) > .0001f)
                throw new InvalidOperationException("飞行躯干产生无效顶点或未归一化权重。");
        }
    }
}
