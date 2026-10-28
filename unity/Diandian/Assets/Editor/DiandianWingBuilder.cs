using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Authored in the Tripo mesh's native coordinates. The original GLB is never changed.
// The shoulder/elbow/wrist chain moves every feather root. Fold only rotates
// feather vanes about those fixed roots; it never moves roots independently.
public static class DiandianWingBuilder
{
    const string AssetPath = "Assets/Tripo/点点三维双翼.asset";
    const int FeatherRows = 13, FeatherColumns = 7;
    static readonly float[] FoldFrames = { .125f, .25f, .375f, .50f, .625f, .75f, .875f, 1f };
    // Deliberately unequal outer feathers: a rounded hand fan followed by a tapered tip.
    static readonly float[] PrimaryTipX = { .455f, .492f, .535f, .580f, .624f, .664f, .700f, .728f, .746f, .738f };
    static readonly float[] PrimaryTipZ = { -.205f, -.235f, -.255f, -.265f, -.260f, -.245f, -.225f, -.200f, -.170f, -.135f };
    static readonly int[] CovertCounts = { 17, 16, 15 };
    static readonly float[] CovertLengths = { .035f, .050f, .070f };
    static readonly float[] CovertWidths = { .014f, .017f, .020f };
    static readonly float[] ProfileTimes = { 0f, .12f, .35f, .60f, .80f, .94f };
    static readonly float[] PrimaryProfile = { .26f, .82f, 1f, .99f, .80f, .28f };
    static readonly float[] SecondaryProfile = { .30f, .82f, 1f, .98f, .82f, .42f };

    // Shared landmarks drive bones, feather attachment rows and the wing skin.
    // The elbow sweeps behind the shoulder; the wrist returns forward, making a shallow bend.
    // The original pearl shoulder patch is centred at z=.153-.159; the joint
    // sits just inside it rather than behind it in the grey dorsal plumage.
    public static Vector3 Shoulder(int side) => new Vector3(side * .078f, .650f, .135f);
    public static Vector3 Elbow(int side) => new Vector3(side * .235f, .650f, -.020f);
    public static Vector3 Wrist(int side) => new Vector3(side * .430f, .635f, .055f);
    public static Vector3 Hand(int side) => new Vector3(side * .515f, .632f, .010f);

    static Vector3 Joint(int side, int bone) => bone == 0 ? Shoulder(side) : bone == 1 ? Elbow(side) : Wrist(side);
    static Vector3 OpenDirection(int side, int bone) => bone == 0 ? Elbow(side) - Shoulder(side)
        : bone == 1 ? Wrist(side) - Elbow(side) : Hand(side) - Wrist(side);
    static Vector3 ClosedDirection(int side, int bone) => (bone == 0 ? new Vector3(side * .045f, -.60f, -.80f)
        : bone == 1 ? new Vector3(side * .08f, .58f, .81f) : new Vector3(side * .02f, -.80f, -.60f)).normalized;

    public static Quaternion ClosedWorldRotation(int side, int bone)
    {
        if (bone < 0 || bone > 2) throw new ArgumentOutOfRangeException(nameof(bone));
        return Quaternion.LookRotation(ClosedDirection(side, bone), new Vector3(side, 0, 0))
            * Quaternion.Inverse(Quaternion.LookRotation(OpenDirection(side, bone), Vector3.up));
    }

    public static float SegmentOpen(float spread, int bone) => DiandianFlight.SegmentOpen(spread, bone);

    public static Matrix4x4 ClosedSkinMatrix(int side, int bone)
    {
        Quaternion rotation = ClosedWorldRotation(side, bone);
        Vector3 pivot = Shoulder(side);
        for (int segment = 0; segment < bone; segment++)
            pivot += ClosedDirection(side, segment) * OpenDirection(side, segment).magnitude;
        return Matrix4x4.TRS(pivot, rotation, Vector3.one) * Matrix4x4.Translate(-Joint(side, bone));
    }

    static Vector3 LeadingEdge(int side, float span)
    {
        Vector3 a, b;
        if (span < .235f) { a = Shoulder(side); b = Elbow(side); }
        else if (span < .430f) { a = Elbow(side); b = Wrist(side); }
        else { a = Wrist(side); b = Hand(side); }
        Vector3 p = Vector3.Lerp(a, b, Mathf.InverseLerp(Mathf.Abs(a.x), Mathf.Abs(b.x), span));
        p.x = side * span;
        return p;
    }

    sealed class Geometry
    {
        public readonly List<Vector3> vertices = new List<Vector3>();
        public readonly List<Vector2> uv = new List<Vector2>();
        public readonly List<BoneWeight> weights = new List<BoneWeight>();
        public readonly Dictionary<int, Vector3> originalNormals = new Dictionary<int, Vector3>();
        public readonly List<int>[] triangles = {
            new List<int>(), new List<int>(), new List<int>(), new List<int>(), new List<int>(), new List<int>()
        };

        public int Vertex(Vector3 p, Vector2 tex, BoneWeight weight)
        {
            int index = vertices.Count;
            vertices.Add(p); uv.Add(tex); weights.Add(weight);
            return index;
        }

        public void Triangle(int material, int a, int b, int c)
        {
            triangles[material].Add(a); triangles[material].Add(b); triangles[material].Add(c);
        }

        // Both skins and every feather have two distinct surfaces and a sealed rim.
        public void SealGrid(int top, int bottom, int rows, int columns, int upperMaterial, int lowerMaterial)
        {
            for (int r = 0; r < rows - 1; r++) for (int c = 0; c < columns - 1; c++)
            {
                int a = r * columns + c, b = a + columns;
                Triangle(upperMaterial, top + a, top + b, top + a + 1);
                Triangle(upperMaterial, top + a + 1, top + b, top + b + 1);
                Triangle(lowerMaterial, bottom + a, bottom + a + 1, bottom + b);
                Triangle(lowerMaterial, bottom + a + 1, bottom + b + 1, bottom + b);
            }
            void Rim(int a, int b)
            {
                Triangle(lowerMaterial, top + a, bottom + a, bottom + b);
                Triangle(lowerMaterial, top + a, bottom + b, top + b);
            }
            for (int r = 0; r < rows - 1; r++) Rim(r * columns, (r + 1) * columns);
            for (int c = 0; c < columns - 1; c++) Rim((rows - 1) * columns + c, (rows - 1) * columns + c + 1);
            for (int r = rows - 1; r > 0; r--) Rim(r * columns + columns - 1, (r - 1) * columns + columns - 1);
            for (int c = columns - 1; c > 0; c--) Rim(c, c - 1);
        }
    }

    public static DiandianFlight.Wing[] Build(Transform space, Transform bodyBone, Mesh original, Material originalMaterial)
    {
        if (!space || !bodyBone || !original || !originalMaterial)
            throw new ArgumentException("三维双翼需要原模型、材质、坐标空间与胸骨。");
        var originalSkin = space.GetComponent<SkinnedMeshRenderer>();
        if (!originalSkin || originalSkin.bones.Length != 26)
            throw new InvalidOperationException("原肩覆羽需要保留26骨源模型的胸、头和骨盆引用。");
        var pearl = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Textures/WingPearl.png");
        var down = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Textures/BodyFeathers.png");
        if (!pearl || !down) throw new InvalidOperationException("三维双翼缺少已有羽毛贴图。");

        Mesh main = AssetDatabase.LoadAssetAtPath<Mesh>(AssetPath);
        if (!main)
        {
            main = new Mesh { name = "点点左翼" };
            AssetDatabase.CreateAsset(main, AssetPath);
        }
        var materials = new[] {
            MaterialAsset("点点翼上羽色", pearl, Color.white),
            MaterialAsset("点点翼下羽色", pearl, new Color(.94f, .96f, .95f)),
            MaterialAsset("点点翼皮细羽", down, new Color(.96f, .97f, .96f)),
            // Long remiges are grey; cream pearl coverts keep their separate material.
            MaterialAsset("点点灰飞羽上面", pearl, new Color(.63f, .66f, .66f)),
            MaterialAsset("点点灰飞羽下面", pearl, new Color(.65f, .67f, .68f)),
            originalMaterial
        };
        var result = new DiandianFlight.Wing[2];
        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            int side = sideIndex == 0 ? -1 : 1;
            string prefix = side < 0 ? "左翼" : "右翼";
            // Rebuilding the same model replaces only nodes owned by this generator.
            var oldShoulder = bodyBone.Find(prefix + "_肩");
            if (oldShoulder) UnityEngine.Object.DestroyImmediate(oldShoulder.gameObject);
            var oldRenderer = space.Find(prefix + "_三维羽片");
            if (oldRenderer) UnityEngine.Object.DestroyImmediate(oldRenderer.gameObject);
            Transform Bone(string name, Transform parent, Vector3 p)
            {
                var bone = new GameObject(name).transform;
                bone.SetParent(parent, false);
                bone.SetPositionAndRotation(space.TransformPoint(p), space.rotation);
                return bone;
            }
            var shoulder = Bone(prefix + "_肩", bodyBone, Shoulder(side));
            var elbow = Bone(prefix + "_肘", shoulder, Elbow(side));
            var wrist = Bone(prefix + "_腕", elbow, Wrist(side));
            // The soft wing skin anchors to the chest; the original coverts
            // move rigidly, without stretching feather texture between bones.
            // Slot 6 uses the same shoulder with a closed-pose bind matrix for
            // the original feather patch. Its vertices need no inverse-LBS warp.
            Transform[] bones = { shoulder, elbow, wrist, bodyBone, originalSkin.bones[2], originalSkin.bones[0], shoulder };
            var bindposes = new Matrix4x4[bones.Length];
            for (int i = 0; i < bones.Length; i++) bindposes[i] = bones[i].worldToLocalMatrix * space.localToWorldMatrix;
            bindposes[6] = bindposes[0] * ClosedSkinMatrix(side, 0).inverse;
            Mesh mesh = side < 0 ? main : MeshAsset("点点右翼");
            WriteMesh(mesh, side, bindposes, original);
            var node = new GameObject(prefix + "_三维羽片");
            node.transform.SetParent(space, false);
            var renderer = node.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh; renderer.sharedMaterials = materials;
            renderer.bones = bones; renderer.rootBone = shoulder; renderer.quality = SkinQuality.Bone4;
            renderer.localBounds = new Bounds(new Vector3(side * .36f, .57f, -.07f), new Vector3(1.05f, 1.10f, .90f));
            renderer.updateWhenOffscreen = true;
            renderer.SetBlendShapeWeight(0, 100f);
            Quaternion closedShoulder = ClosedWorldRotation(side, 0), closedElbow = ClosedWorldRotation(side, 1), closedWrist = ClosedWorldRotation(side, 2);
            result[sideIndex] = new DiandianFlight.Wing { side = side, shoulder = shoulder, elbow = elbow, wrist = wrist, renderer = renderer,
                closedShoulder = closedShoulder, closedElbow = Quaternion.Inverse(closedShoulder) * closedElbow,
                closedWrist = Quaternion.Inverse(closedElbow) * closedWrist };
        }
        AssetDatabase.SaveAssets();
        return result;
    }

    static Material MaterialAsset(string name, Texture2D texture, Color tint)
    {
        Material material = null;
        foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(AssetPath))
            if (asset is Material candidate && candidate.name == name) { material = candidate; break; }
        Shader shader = Shader.Find("Standard");
        if (!shader) throw new InvalidOperationException("找不到 Standard 羽毛材质。");
        if (!material)
        {
            material = new Material(shader) { name = name };
            AssetDatabase.AddObjectToAsset(material, AssetPath);
        }
        material.shader = shader; material.mainTexture = texture; material.color = tint;
        material.SetFloat("_Metallic", 0); material.SetFloat("_Glossiness", .12f);
        EditorUtility.SetDirty(material);
        return material;
    }

    static Mesh MeshAsset(string name)
    {
        foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(AssetPath))
            if (asset is Mesh mesh && mesh.name == name) return mesh;
        var created = new Mesh { name = name };
        AssetDatabase.AddObjectToAsset(created, AssetPath);
        return created;
    }

    static BoneWeight Weight(int bone) => new BoneWeight { boneIndex0 = bone, weight0 = 1f };
    static BoneWeight FeatherWeight(float span) => Weight(span < .235f ? 0 : span < .430f ? 1 : 2);

    static BoneWeight SpanWeight(float span)
    {
        if (span < .25f)
        {
            float t = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.16f, .25f, span));
            return t < .5f ? new BoneWeight { boneIndex0 = 0, weight0 = 1 - t, boneIndex1 = 1, weight1 = t }
                : new BoneWeight { boneIndex0 = 1, weight0 = t, boneIndex1 = 0, weight1 = 1 - t };
        }
        float u = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.36f, .45f, span));
        return u < .5f ? new BoneWeight { boneIndex0 = 1, weight0 = 1 - u, boneIndex1 = 2, weight1 = u }
            : new BoneWeight { boneIndex0 = 2, weight0 = u, boneIndex1 = 1, weight1 = 1 - u };
    }

    static BoneWeight AttachmentWeight(float span)
    {
        BoneWeight moving = SpanWeight(span);
        // Only the buried skin edge is fixed to the chest. Farther out the
        // shoulder must carry the axilla instead of leaving a rigid little wing.
        float chest = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.060f, .140f, span));
        float first = moving.weight0 * (1 - chest), second = moving.weight1 * (1 - chest);
        // Keep weights in descending order for Unity's four-influence skinning path.
        if (chest >= first)
            return new BoneWeight { boneIndex0 = 3, weight0 = chest, boneIndex1 = moving.boneIndex0, weight1 = first,
                boneIndex2 = moving.boneIndex1, weight2 = second };
        if (chest >= second)
            return new BoneWeight { boneIndex0 = moving.boneIndex0, weight0 = first, boneIndex1 = 3, weight1 = chest,
                boneIndex2 = moving.boneIndex1, weight2 = second };
        return new BoneWeight { boneIndex0 = moving.boneIndex0, weight0 = first, boneIndex1 = moving.boneIndex1, weight1 = second,
            boneIndex2 = 3, weight2 = chest };
    }

    static float FeatherProfile(float t, bool primary, bool covert)
    {
        if (covert)
        {
            float root = Mathf.Lerp(.30f, 1f, Mathf.SmoothStep(0, 1, t / .20f));
            float end = Mathf.Clamp01((t - .65f) / .35f);
            return Mathf.Max(.025f, root * Mathf.Sqrt(Mathf.Max(0, 1 - end * end)));
        }
        float[] widths = primary ? PrimaryProfile : SecondaryProfile;
        if (t > .94f)
        {
            float cap = (t - .94f) / .06f;
            return .025f + (widths[widths.Length - 1] - .025f) * Mathf.Sqrt(Mathf.Max(0, 1 - cap * cap));
        }
        int i = 0;
        while (i < ProfileTimes.Length - 2 && t > ProfileTimes[i + 1]) i++;
        return Mathf.Lerp(widths[i], widths[i + 1], Mathf.SmoothStep(0, 1,
            Mathf.InverseLerp(ProfileTimes[i], ProfileTimes[i + 1], t)));
    }

    static Geometry MakeGeometry(int side, float fold, Geometry shoulderPatch)
    {
        var g = new Geometry();
        WingSkin(g, side);
        ShoulderCoverts(g, side, fold);
        // Secondary shafts are spread along the forearm, not radiating from one point.
        for (int i = 0; i < 10; i++)
        {
            float t = i / 9f;
            float span = .104f + .290f * t, layer = (9 - i) * .00030f;
            var a = LeadingEdge(side, span) + new Vector3(0, .0006f + layer, -.012f);
            var b = new Vector3(side * (.135f + .290f * t), .620f + layer, a.z - (.190f + .055f * t));
            Feather(g, side, fold, a, b, new Vector3(side * .015f, -.77f, -.64f),
                .028f + .003f * (1 - Mathf.SmoothStep(0, 1, t / .35f)), .0023f, FeatherWeight(span), i, false, false);
        }
        // Ten primaries form the long outer wing. The longest silhouette reaches .746 native units.
        for (int i = 0; i < 10; i++)
        {
            float t = i / 9f;
            float layer = (9 - i) * .00032f;
            // Closely packed hand roots keep the proximal 75-80% of the fan
            // overlapped. Only distal feather tips separate; widths stay asymmetric.
            var a = LeadingEdge(side, .425f + .055f * t) + new Vector3(0, .0003f + layer, -.010f);
            var b = new Vector3(side * PrimaryTipX[i], .620f - .006f * Mathf.Sin(t * Mathf.PI) + layer, PrimaryTipZ[i]);
            // The hand bone moves the attachment row; only vanes fan around it.
            Feather(g, side, fold, a, b, new Vector3(side * -.015f, -.80f, -.60f),
                .024f + .004f * Mathf.Sin(t * Mathf.PI), .0028f, Weight(2), i + 10, false, false);
        }
        // Three staggered rows cover the shoulder/elbow/wrist on BOTH sides of the wing.
        for (int underside = 0; underside < 2; underside++)
            for (int row = 0; row < 3; row++) for (int i = 0; i < CovertCounts[row]; i++)
            {
                float stagger = (row % 2) * .35f;
                float t = (i + stagger) / (CovertCounts[row] - 1f + stagger);
                float x = .103f + .405f * t;
                // Successive rows sit below the row ahead, with overlap in length,
                // rather than three thick scales occupying the same plane.
                float layer = underside == 0 ? .0048f + (2 - row) * .0008f : -.0040f - (2 - row) * .0008f;
                var a = LeadingEdge(side, x) + new Vector3(0, layer, -.003f - row * .018f);
                Vector3 direction = new Vector3(side * .10f, underside == 0 ? -.025f : .025f, -1).normalized;
                var b = a + direction * CovertLengths[row];
                Feather(g, side, fold, a, b, new Vector3(side * .012f, -.78f, -.625f),
                    CovertWidths[row], .00065f, FeatherWeight(x), i + row * 32, true, underside != 0);
            }
        AppendPatch(g, shoulderPatch);
        return g;
    }

    static void ShoulderCoverts(Geometry g, int side, float fold)
    {
        // The shoulder joint is buried under the preserved Tripo shoulder plumage.
        // These short coverts wrap the fleshy root on both sides, not a visible thin spar.
        // Their blades follow the shoulder; only the underlying skin has a chest anchor.
        for (int underside = 0; underside < 2; underside++)
            for (int row = 0; row < 3; row++) for (int i = 0; i < 7; i++)
            {
                float t = i / 6f, x = .062f + .111f * t;
                float root = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.090f, .200f, x));
                // The transferred original shoulder cap sits about .031 above
                // the open wing plane. Give the humerus matching feather volume.
                float height = underside == 0 ? .0055f + .0245f * root + (2 - row) * .0007f
                    : -.0035f - .0205f * root - (2 - row) * .0007f;
                var a = LeadingEdge(side, x) + new Vector3(0, height, .021f * root - row * .027f);
                Vector3 direction = new Vector3(side * .38f, underside == 0 ? -.04f : .04f, -.925f).normalized;
                var b = a + direction * (.047f + row * .018f);
                Feather(g, side, fold, a, b, new Vector3(side * .012f, -.67f, -.74f),
                    .016f + row * .003f, .0007f, Weight(0), row * 32 + i * 2, true, underside != 0);
            }
    }

    static void Feather(Geometry g, int side, float fold, Vector3 openA, Vector3 openB,
        Vector3 restDirection, float width, float curve, BoneWeight weight, int textureStrip, bool covert, bool underside)
    {
        if (weight.boneIndex0 > 2 || weight.weight0 != 1 || weight.weight1 != 0 || weight.weight2 != 0 || weight.weight3 != 0)
            throw new InvalidOperationException("每片羽毛必须刚性跟随一根翼骨，跨骨平滑仅用于翼皮。");
        bool primary = !covert && textureStrip >= 10;
        fold = 1 - SegmentOpen(1 - fold, weight.boneIndex0);
        Vector3 openDirection = openB - openA;
        float length = openDirection.magnitude;
        Vector3 openNormal = underside ? Vector3.down : Vector3.up;
        Vector3 restNormal = new Vector3(side * (underside ? -1 : 1), 0, 0);
        // The posed feather should point down/back when closed. Convert that
        // frame through the same closed bone transform used by the native FK rig.
        Quaternion closedFrame = Quaternion.Inverse(ClosedWorldRotation(side, weight.boneIndex0))
            * Quaternion.LookRotation(restDirection.normalized, restNormal);
        Quaternion rotation = Quaternion.Slerp(Quaternion.LookRotation(openDirection, openNormal), closedFrame, fold);
        Vector3 root = openA;
        int top = g.vertices.Count;
        // The UV uses the existing photo: upper half = pearl coverts; bottom = long grey/white feathers.
        int covertRow = covert ? textureStrip / 32 : 0, covertColumn = textureStrip % 32;
        float stagger = (covertRow % 2) * .35f;
        float covertSpan = covert ? (covertColumn + stagger) / (CovertCounts[covertRow] - 1f + stagger) : 0;
        // One coherent atlas band per covert row, rather than repeatedly placing
        // several tiny photographed feathers on every geometric feather.
        float uCenter = .06f + .88f * covertSpan;
        float uHalf = covert ? .027f + covertRow * .004f : primary ? .036f : .030f;
        float vRoot = .99f - covertRow * .14f;
        float vTip = vRoot - (.12f + covertRow * .025f);
        // Underwing coverts are soft grey, without a second identical pearl mantle.
        if (covert && underside) { vRoot = .41f - covertRow * .06f; vTip = vRoot - .17f; }
        // Follow two actual long-feather shafts in the supplied photograph. The
        // transverse UV direction follows that shaft, not a random vertical strip.
        Vector2 shaftRoot = textureStrip % 2 == 0 ? new Vector2(.42f, .43f) : new Vector2(.60f, .43f);
        Vector2 shaftTip = textureStrip % 2 == 0 ? new Vector2(.41f, .03f) : new Vector2(.53f, .02f);
        Vector2 shaftDirection = (shaftTip - shaftRoot).normalized;
        Vector2 shaftAcross = new Vector2(-shaftDirection.y, shaftDirection.x);
        const float halfThickness = .00012f;
        for (int surface = 0; surface < 2; surface++)
            for (int r = 0; r < FeatherRows; r++) for (int c = 0; c < FeatherColumns; c++)
            {
                // Concentrate rows near the rounded end so it is not one long triangular spike.
                float t = 1f - Mathf.Pow(1f - r / (FeatherRows - 1f), 1.4f);
                float across = c / (FeatherColumns - 1f) * 2 - 1;
                float profile = FeatherProfile(t, primary, covert);
                float narrowVane = primary ? .65f : covert ? .90f : .84f;
                float asymmetry = across * side < 0 ? narrowVane : 1f;
                float ridge = (1 - across * across) * (covert ? .00008f : .00018f);
                float y = Mathf.Sin(t * Mathf.PI) * curve + ridge + (surface == 0 ? halfThickness : -halfThickness);
                Vector3 local = new Vector3(across * width * profile * asymmetry, y, length * t);
                Vector2 tex = covert ? new Vector2(uCenter + across * uHalf, Mathf.Lerp(vRoot, vTip, t))
                    : Vector2.Lerp(shaftRoot, shaftTip, t) + shaftAcross * (across * uHalf * asymmetry);
                g.Vertex(root + rotation * local, tex, weight);
            }
        int bottom = top + FeatherRows * FeatherColumns;
        g.SealGrid(top, bottom, FeatherRows, FeatherColumns, covert ? (underside ? 1 : 0) : 3, covert ? 1 : 4);
        // Quaternion frames mirror feather directions, not triangle indices. Both
        // sides must remain outward-facing even on the opposite wing or underwing.
        int middle = (FeatherRows / 2) * FeatherColumns + FeatherColumns / 2;
        Vector3 topNormal = Vector3.Cross(g.vertices[top + middle + FeatherColumns] - g.vertices[top + middle],
            g.vertices[top + middle + 1] - g.vertices[top + middle]);
        Vector3 bottomNormal = Vector3.Cross(g.vertices[bottom + middle + 1] - g.vertices[bottom + middle],
            g.vertices[bottom + middle + FeatherColumns] - g.vertices[bottom + middle]);
        Vector3 outward = rotation * Vector3.up;
        if (Vector3.Dot(topNormal, outward) <= 0 || Vector3.Dot(bottomNormal, outward) >= 0)
            throw new InvalidOperationException("飞羽上下表面绕序不正确。");
        float shaftLength = Vector3.Distance(g.vertices[top + FeatherColumns / 2],
            g.vertices[top + (FeatherRows - 1) * FeatherColumns + FeatherColumns / 2]);
        if (Mathf.Abs(shaftLength - length) > .00001f) throw new InvalidOperationException("开合改变了羽片长度。");
    }

    static void WingSkin(Geometry g, int side)
    {
        const int rows = 17, columns = 9;
        int top = g.vertices.Count;
        for (int surface = 0; surface < 2; surface++) for (int r = 0; r < rows; r++) for (int c = 0; c < columns; c++)
        {
            float t = r / (rows - 1f), angle = c / (columns - 1f) * Mathf.PI;
            float across = (1-Mathf.Cos(angle))*.5f;
            float x = .058f + .454f * t;
            float root = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.090f, .200f, x));
            // Keep the stationary inner edge inside the torso. The chord widens
            // outside it, preserving a broad feathered junction during a beat.
            float widening = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.060f, .115f, x));
            float width = .055f + .025f * Mathf.Sin(t * Mathf.PI) + .055f * root * widening;
            var openCenter = LeadingEdge(side, x) + Vector3.forward * (.025f * root);
            BoneWeight weight = AttachmentWeight(x);
            float camber = Mathf.Sin(across * Mathf.PI) * (.0010f + .0040f * root);
            float halfThickness = .0008f + .0242f * root;
            // A round cross-section meets at the leading/trailing edges. The
            // old box section exposed a tall rim whose top/bottom UVs coincided,
            // stretching one texel into the striped wall seen at the shoulder.
            float round = Mathf.Sin(angle);
            Vector3 p = openCenter + Vector3.back * (across * width)
                + Vector3.up * (camber + (surface == 0 ? halfThickness : -halfThickness)*round);
            g.Vertex(p, new Vector2(t * 1.6f, across), weight);
        }
        int bottom = top + rows * columns;
        // Left mirroring reverses this skin's row direction, unlike the oriented feather frames.
        int firstTriangle = g.triangles[2].Count;
        g.SealGrid(top, bottom, rows, columns, 2, 2);
        if (side < 0)
            for (int i = firstTriangle; i < g.triangles[2].Count; i += 3)
                (g.triangles[2][i + 1], g.triangles[2][i + 2]) = (g.triangles[2][i + 2], g.triangles[2][i + 1]);
    }

    static Geometry ShoulderPatch(Mesh source, int side)
    {
        var patch = new Geometry();
        Vector3[] positions = source.vertices, normals = source.normals;
        Vector2[] uv = source.uv;
        BoneWeight[] weights = source.boneWeights;
        if (normals.Length != positions.Length || uv.Length != positions.Length || weights.Length != positions.Length)
            throw new InvalidOperationException("原肩覆羽缺少位置、法线、UV或骨权重。");
        var mapped = new Dictionary<int, int>();
        int Vertex(int index)
        {
            if (mapped.TryGetValue(index, out int existing)) return existing;
            Vector3 position = positions[index];
            // Slot 6 binds these unchanged source vertices in the closed pose.
            // The separate wing skin supplies the flexible chest connection.
            int result = patch.Vertex(position, uv[index], Weight(6));
            patch.originalNormals.Add(result, normals[index]);
            mapped.Add(index, result);
            return result;
        }
        int[] triangles = source.triangles;
        for (int i = 0; i < triangles.Length; i += 3)
        {
            int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
            if (!DiandianFlightBody.IsShoulderTriangle(positions[a], positions[b], positions[c])) continue;
            if (positions[a].x * side <= 0 && positions[b].x * side <= 0 && positions[c].x * side <= 0) continue;
            if (positions[a].x * side <= 0 || positions[b].x * side <= 0 || positions[c].x * side <= 0)
                throw new InvalidOperationException("原肩覆羽跨过中线，不能分配到单侧翼根。");
            patch.Triangle(5, Vertex(a), Vertex(b), Vertex(c));
        }
        if (patch.triangles[5].Count == 0) throw new InvalidOperationException("原肩覆羽提取为空。");
        return patch;
    }

    static void AppendPatch(Geometry geometry, Geometry patch)
    {
        int first = geometry.vertices.Count;
        geometry.vertices.AddRange(patch.vertices); geometry.uv.AddRange(patch.uv); geometry.weights.AddRange(patch.weights);
        foreach (int index in patch.triangles[5]) geometry.triangles[5].Add(first + index);
        foreach (var item in patch.originalNormals) geometry.originalNormals.Add(first + item.Key, item.Value);
    }

    static Vector3[] Normals(Geometry geometry)
    {
        var temporary = new Mesh { indexFormat = IndexFormat.UInt32 };
        try
        {
            temporary.SetVertices(geometry.vertices); temporary.subMeshCount = geometry.triangles.Length;
            for (int i = 0; i < geometry.triangles.Length; i++) temporary.SetTriangles(geometry.triangles[i], i);
            temporary.RecalculateNormals();
            Vector3[] normals = temporary.normals;
            foreach (var original in geometry.originalNormals) normals[original.Key] = original.Value;
            return normals;
        }
        finally { UnityEngine.Object.DestroyImmediate(temporary); }
    }

    static void WriteMesh(Mesh mesh, int side, Matrix4x4[] bindposes, Mesh original)
    {
        Geometry shoulderPatch = ShoulderPatch(original, side);
        Geometry open = MakeGeometry(side, 0, shoulderPatch);
        ValidateClosedGeometry(open);
        Vector3[] normals = Normals(open);
        // Mesh setters deliberately replace buffers: CopySerialized can retain stale skinning strides.
        mesh.ClearBlendShapes(); mesh.Clear(false); mesh.indexFormat = IndexFormat.UInt32;
        mesh.SetVertices(open.vertices); mesh.SetNormals(normals); mesh.SetUVs(0, open.uv);
        mesh.boneWeights = open.weights.ToArray(); mesh.bindposes = bindposes;
        mesh.subMeshCount = open.triangles.Length;
        for (int i = 0; i < open.triangles.Length; i++) mesh.SetTriangles(open.triangles[i], i);
        foreach (float fold in FoldFrames)
        {
            Geometry folded = MakeGeometry(side, fold, shoulderPatch);
            if (folded.vertices.Count != open.vertices.Count) throw new InvalidOperationException("开合姿态顶点数不同。");
            if (fold == 1f) ValidateClosedGeometry(folded);
            Vector3[] foldedNormals = Normals(folded);
            var deltas = new Vector3[open.vertices.Count];
            var normalDeltas = new Vector3[deltas.Length];
            for (int i = 0; i < deltas.Length; i++)
            {
                if (!float.IsFinite(folded.vertices[i].sqrMagnitude)) throw new InvalidOperationException("三维羽片存在非法顶点。");
                deltas[i] = folded.vertices[i] - open.vertices[i]; normalDeltas[i] = foldedNormals[i] - normals[i];
            }
            mesh.AddBlendShapeFrame("Fold", fold * 100f, deltas, normalDeltas, null);
        }
        mesh.RecalculateTangents(); mesh.RecalculateBounds();
        EditorUtility.SetDirty(mesh);
        Debug.Log("DIANDIAN_WING_MESH side=" + side + " vertices=" + mesh.vertexCount + " span=" + mesh.bounds.size.x.ToString("F3")
            + " foldFrames=" + FoldFrames.Length + " originalShoulderTriangles=" + shoulderPatch.triangles[5].Count / 3 + " fixedRootFK=true");
    }

    static void ValidateClosedGeometry(Geometry g)
    {
        var edgeUses = new Dictionary<ulong, int>();
        double signedVolume = 0;
        void Edge(int a, int b)
        {
            uint low = (uint)Math.Min(a, b), high = (uint)Math.Max(a, b);
            ulong key = ((ulong)low << 32) | high;
            edgeUses.TryGetValue(key, out int count); edgeUses[key] = count + 1;
        }
        // Submesh 5 is an original open shoulder patch that meets the remaining
        // body along its old boundary; only the generated five submeshes are closed.
        for (int material = 0; material < 5; material++)
        {
            var triangles = g.triangles[material];
            for (int i = 0; i < triangles.Count; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                Edge(a, b); Edge(b, c); Edge(c, a);
                signedVolume += Vector3.Dot(g.vertices[a], Vector3.Cross(g.vertices[b], g.vertices[c])) / 6.0;
            }
        }
        foreach (int count in edgeUses.Values)
            if (count != 2) throw new InvalidOperationException("三维羽片或翼皮存在未封口边缘。");
        if (signedVolume <= 0) throw new InvalidOperationException("三维双翼法线朝向错误。");
        foreach (BoneWeight w in g.weights)
            if (Mathf.Abs(w.weight0 + w.weight1 + w.weight2 + w.weight3 - 1) > .0001f)
                throw new InvalidOperationException("三维双翼权重未归一化。");
    }
}
