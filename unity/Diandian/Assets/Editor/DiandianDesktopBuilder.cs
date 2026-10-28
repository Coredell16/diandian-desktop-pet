using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public static class DiandianDesktopBuilder
{
    const string PreviewScene = "Assets/点点预览.unity";
    const string DesktopScene = "Assets/点点桌宠.unity";
    const string MaterialPath = "Assets/Tripo/点点桌面材质.mat";
    const string TexturePath = "Assets/Tripo/点点桌面贴图.asset";
    const int Width = 260, Height = 300;

    [MenuItem("点点/构建 Windows 桌宠 v2")]
    public static void BuildWindows()
    {
        PrepareScene();
        ConfigurePlayer();
        string outputs = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../outputs"));
        string version = DiandianUpdater.VersionLabel;
        string playerFolder = Path.Combine(outputs, "Windows-v" + version, "点点");
        Directory.CreateDirectory(playerFolder);
        string executable = Path.Combine(playerFolder, DiandianUpdater.ExecutableName);
        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { DesktopScene },
            target = BuildTarget.StandaloneWindows64,
            locationPathName = executable,
            options = BuildOptions.None
        });
        if (report == null || report.summary.result != BuildResult.Succeeded)
            throw new BuildFailedException("桌宠构建失败：" + (report == null ? "没有构建报告" : report.SummarizeErrors()));

        string zip = Path.Combine(outputs, "点点桌宠-v" + version + "-Windows.zip");
        Package(report, playerFolder, zip);
        string hash;
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(zip))
            hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        var manifest = new DiandianUpdater.UpdateManifest
        {
            schema = 1,
            platform = "windows-x64",
            version = version,
            buildNumber = DiandianUpdater.BuildNumber,
            url = "",
            sha256 = hash,
            size = new FileInfo(zip).Length,
            notes = "点点桌宠 v2 预览版：肩、肘、腕按固定骨长连续展收；原肩部覆羽随翼根活动并保留原纹理；修正翼下补面、半展开时序和取景，保留快速拖动展翅与收爪。"
        };
        string manifestPath = Path.Combine(outputs, "latest.template.json");
        File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest, true) + "\n", new UTF8Encoding(false));
        Debug.Log("DIANDIAN_DESKTOP_BUILD_OK\n" + executable + "\n" + zip + "\n" + manifestPath
            + "\nZIP bytes=" + manifest.size + " SHA256=" + hash);
    }

    // Also callable by the editor bridge for inspection before an executable build.
    [MenuItem("点点/开发/准备桌宠场景")]
    public static void PrepareScene()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("请先停止播放，再准备桌宠场景。");
        Scene scene = EditorSceneManager.OpenScene(PreviewScene, OpenSceneMode.Single);
        if (!EditorSceneManager.SaveScene(scene, DesktopScene))
            throw new IOException("无法另存桌宠场景。");
        var previews = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<DiandianPreview>(true)).ToArray();
        if (previews.Length != 1)
            throw new InvalidOperationException("预览场景必须恰有一个 DiandianPreview。");
        DiandianPreview preview = previews[0];
        if (!preview.viewCamera || !preview.modelRoot || !preview.rig || !preview.voice)
            throw new InvalidOperationException("预览场景缺少相机、模型、骨骼或声音引用。");

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            var instances = root.GetComponentsInChildren<Transform>(true)
                .Select(t => PrefabUtility.GetOutermostPrefabInstanceRoot(t.gameObject))
                .Where(instance => instance != null).Distinct().ToArray();
            foreach (GameObject instance in instances)
                if (PrefabUtility.IsPartOfPrefabInstance(instance))
                    PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        }

        Renderer[] renderers = preview.modelRoot.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
            throw new InvalidOperationException("桌宠模型没有 Renderer。");
        Material[] sourceMaterials = renderers.SelectMany(renderer => renderer.sharedMaterials)
            .Where(material => material != null).Distinct().ToArray();
        if (sourceMaterials.Length != 1)
            throw new InvalidOperationException("当前桌宠应使用一个原始材质；发现 " + sourceMaterials.Length + " 个，停止自动替换。");
        Material desktopMaterial = CopyDesktopMaterial(sourceMaterials[0]);
        foreach (Renderer renderer in renderers)
        {
            var materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
                if (materials[i]) materials[i] = desktopMaterial;
            renderer.sharedMaterials = materials;
        }

        preview.rig.ResetToRestPose();
        preview.rig.SetMotion((int)DiandianRig.Motion.Idle);
        preview.modelRoot.localRotation = Quaternion.identity;
        ConfigureCamera(preview.viewCamera, renderers);
        var window = preview.gameObject.AddComponent<DiandianDesktopWindow>();
        window.windowSize = new Vector2Int(Width, Height);
        window.renderCamera = preview.viewCamera;
        var updater = preview.gameObject.AddComponent<DiandianUpdater>();
        var pet = preview.gameObject.AddComponent<DiandianDesktopPet>();
        pet.viewCamera = preview.viewCamera;
        pet.modelRoot = preview.modelRoot;
        pet.rig = preview.rig;
        var bodySkin = preview.rig.GetComponent<SkinnedMeshRenderer>();
        if (!bodySkin) throw new InvalidOperationException("三维飞行需要原身体蒙皮。");
        var flying = preview.rig.gameObject.AddComponent<DiandianFlight>();
        flying.bodyRenderer = bodySkin;
        flying.restingMesh = bodySkin.sharedMesh;
        flying.restingMaterials = bodySkin.sharedMaterials;
        flying.flightMesh = DiandianFlightBody.Build(bodySkin.sharedMesh);
        flying.flightMaterials = new[] { desktopMaterial, DiandianFlightBody.BodyMaterial() };
        flying.wings = DiandianWingBuilder.Build(preview.rig.transform, preview.rig.body, bodySkin.sharedMesh, desktopMaterial);
        flying.viewCamera = preview.viewCamera;
        flying.cameraFocus = bodySkin.transform.TransformPoint(bodySkin.sharedMesh.bounds.center);
        flying.restingCameraPosition = preview.viewCamera.transform.position;
        flying.ApplyPose(0,0);
        pet.flight = flying;
        pet.voice = preview.voice;
        pet.calls = preview.calls;
        pet.callNames = preview.callNames;
        pet.window = window;
        pet.updater = updater;
        pet.voice.playOnAwake = false;
        preview.gameObject.name = "点点桌宠控制";
        Object.DestroyImmediate(preview);

        string sourcePath = Path.Combine(Application.streamingAssetsPath, "update-source.json");
        Directory.CreateDirectory(Application.streamingAssetsPath);
        if (!File.Exists(sourcePath))
            File.WriteAllText(sourcePath, "{\n  \"manifestUrl\": \"\"\n}\n", new UTF8Encoding(false));
        AssetDatabase.Refresh();
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) throw new IOException("无法保存桌宠场景。");
        ValidateDependencies();
        Selection.activeGameObject = pet.modelRoot.gameObject;
        Debug.Log("DIANDIAN_DESKTOP_SCENE_OK " + DesktopScene + " camera=" + pet.viewCamera.transform.position);
    }

    static Material CopyDesktopMaterial(Material source)
    {
        Texture texture = source.mainTexture;
        if (!texture) throw new InvalidOperationException("原材质没有 mainTexture，不能生成独立桌面贴图。");
        float scale = Mathf.Min(1f, 2048f / Mathf.Max(texture.width, texture.height));
        int width = Mathf.Max(4, Mathf.RoundToInt(texture.width * scale / 4) * 4);
        int height = Mathf.Max(4, Mathf.RoundToInt(texture.height * scale / 4) * 4);
        var copy = new Texture2D(width, height, TextureFormat.RGB24, true, false)
        {
            name = "点点桌面贴图",
            filterMode = texture.filterMode,
            wrapMode = texture.wrapMode,
            anisoLevel = texture.anisoLevel
        };
        RenderTexture previous = RenderTexture.active;
        bool previousSRGB = GL.sRGBWrite;
        RenderTexture temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        try
        {
            GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
            Graphics.Blit(texture, temporary);
            RenderTexture.active = temporary;
            copy.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            copy.Apply(true, false);
            EditorUtility.CompressTexture(copy, TextureFormat.DXT1, TextureCompressionQuality.Best);
            copy.Apply(false, true);
        }
        catch
        {
            Object.DestroyImmediate(copy);
            throw;
        }
        finally
        {
            GL.sRGBWrite = previousSRGB;
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(temporary);
        }
        Texture2D savedTexture = SaveAsset(copy, TexturePath);
        var material = new Material(source) { name = "点点桌面材质" };
        foreach (string property in material.GetTexturePropertyNames())
            if (material.GetTexture(property) == texture) material.SetTexture(property, savedTexture);
        material.mainTexture = savedTexture;
        return SaveAsset(material, MaterialPath);
    }

    static T SaveAsset<T>(T asset, string path) where T : Object
    {
        T existing = AssetDatabase.LoadAssetAtPath<T>(path);
        if (!existing)
        {
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }
        EditorUtility.CopySerialized(asset, existing);
        EditorUtility.SetDirty(existing);
        Object.DestroyImmediate(asset);
        return existing;
    }

    static void ConfigureCamera(Camera camera, Renderer[] renderers)
    {
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.clear;
        camera.allowHDR = false;
        camera.allowMSAA = false;
        camera.targetTexture = null;
        camera.rect = new Rect(0, 0, 1, 1);
        camera.orthographic = false;
        camera.aspect = (float)Width / Height;
        // Skinning bounds include animation padding. Fit the neutral geometry itself,
        // not those padded bounds transformed into a second axis-aligned box.
        var points = new List<Vector3>();
        foreach (Renderer renderer in renderers)
        {
            Mesh mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
            if (!mesh || !mesh.isReadable)
                throw new InvalidOperationException("取景需要可读取的中立姿态网格：" + renderer.name);
            foreach (Vector3 vertex in mesh.vertices)
                points.Add(renderer.transform.TransformPoint(vertex));
        }
        if (points.Count == 0) throw new InvalidOperationException("桌宠取景网格没有顶点。");
        Bounds bounds = new Bounds(points[0], Vector3.zero);
        foreach (Vector3 point in points) bounds.Encapsulate(point);
        Vector3 focus = bounds.center;
        Vector3 back = Quaternion.Euler(-11, -60, 0) * Vector3.forward;
        camera.transform.rotation = Quaternion.LookRotation(-back, Vector3.up);
        float vertical = Mathf.Tan(camera.fieldOfView * .5f * Mathf.Deg2Rad) * .88f;
        float horizontal = vertical * camera.aspect;
        float distance = .1f;
        foreach (Vector3 point in points)
        {
            Vector3 offset = point - focus;
            float depth = Vector3.Dot(offset, back);
            distance = Mathf.Max(distance, Mathf.Abs(Vector3.Dot(offset, camera.transform.right)) / horizontal + depth,
                Mathf.Abs(Vector3.Dot(offset, camera.transform.up)) / vertical + depth);
            distance = Mathf.Max(distance, depth + .06f);
        }
        camera.transform.position = focus + back * distance;
        camera.nearClipPlane = .03f;
        camera.farClipPlane = Mathf.Max(30, distance + bounds.size.magnitude);
        Vector2 minimum = Vector2.one, maximum = Vector2.zero;
        foreach (Vector3 point in points)
        {
            Vector3 viewport = camera.WorldToViewportPoint(point);
            minimum = Vector2.Min(minimum, new Vector2(viewport.x, viewport.y));
            maximum = Vector2.Max(maximum, new Vector2(viewport.x, viewport.y));
        }
        Debug.Log("DIANDIAN_DESKTOP_FRAMING vertices=" + points.Count + " distance=" + distance.ToString("F3")
            + " viewport=" + minimum.ToString("F3") + ".." + maximum.ToString("F3"));
    }

    static void ValidateDependencies()
    {
        string[] dependencies = AssetDatabase.GetDependencies(DesktopScene, true);
        string[] unwanted = dependencies.Where(path => path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("Assets/Model/", StringComparison.OrdinalIgnoreCase)
            || path == "Assets/Tripo/点点.prefab" || path == PreviewScene).ToArray();
        if (unwanted.Length != 0)
            throw new BuildFailedException("桌宠仍引用原 GLB/旧模型，停止打包：\n" + string.Join("\n", unwanted));
        if (!dependencies.Contains(MaterialPath) || !dependencies.Contains(TexturePath))
            throw new BuildFailedException("桌宠场景没有引用独立材质及贴图。");
        Debug.Log("DIANDIAN_DESKTOP_DEPENDENCIES_OK " + dependencies.Length + " assets, no GLB or preview prefab");
    }

    static void ConfigurePlayer()
    {
        PlayerSettings.companyName = "Diandian";
        PlayerSettings.productName = "点点桌宠";
        PlayerSettings.bundleVersion = DiandianUpdater.VersionLabel;
        PlayerSettings.defaultScreenWidth = Width;
        PlayerSettings.defaultScreenHeight = Height;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.runInBackground = true;
        PlayerSettings.forceSingleInstance = false;
        PlayerSettings.allowFullscreenSwitch = false;
        PlayerSettings.resizableWindow = false;
        // Unity 6 permits disabling its splash screen on Personal as well as Pro.
        PlayerSettings.SplashScreen.show = false;
        PlayerSettings.SplashScreen.showUnityLogo = false;
        PlayerSettings.useFlipModelSwapchain = false; // DWM transparency requires D3D11 BitBlt.
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
        PlayerSettings.meshDeformation = MeshDeformation.GPU;
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
        EditorUserBuildSettings.development = false;
        EditorUserBuildSettings.allowDebugging = false;
        EditorUserBuildSettings.connectProfiler = false;
        QualitySettings.shadows = ShadowQuality.Disable;
        QualitySettings.vSyncCount = 0;
        QualitySettings.antiAliasing = 0;
    }

    static void Package(BuildReport report, string folder, string destination)
    {
        string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string temporary = destination + ".tmp";
        var paths = report.GetFiles().Select(file => Path.GetFullPath(file.path))
            .Where(path => path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        bool hasExecutable = false, hasData = false;
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            foreach (string path in paths)
            {
                string relative = path.Substring(root.Length).Replace('\\', '/');
                if (relative.IndexOf("DoNotShip", StringComparison.OrdinalIgnoreCase) >= 0
                    || relative.IndexOf("BackUpThisFolder", StringComparison.OrdinalIgnoreCase) >= 0
                    || Path.GetFileName(relative).StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase)
                    || relative.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                    || relative.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase)) continue;
                hasExecutable |= relative == DiandianUpdater.ExecutableName;
                hasData |= relative.StartsWith("点点_Data/", StringComparison.Ordinal);
                ZipArchiveEntry entry = archive.CreateEntry(relative, System.IO.Compression.CompressionLevel.Optimal);
                using (Stream input = File.OpenRead(path))
                using (Stream output = entry.Open()) input.CopyTo(output);
            }
        }
        if (!hasExecutable || !hasData)
            throw new BuildFailedException("构建报告中的打包文件缺少点点.exe 或点点_Data；未替换已有 ZIP。");
        if (File.Exists(destination)) File.Replace(temporary, destination, null);
        else File.Move(temporary, destination);
    }
}
