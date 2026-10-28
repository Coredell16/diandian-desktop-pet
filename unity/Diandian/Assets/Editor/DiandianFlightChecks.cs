using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Media;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.Video;

public static class DiandianFlightChecks
{
    const string ScenePath = "Assets/点点桌宠.unity";
    const string OutputPath = "Temp/pipeline-screenshots/flight-3d-check.png";
    const int Width = 800, Height = 600;
    static EditorApplication.CallbackFunction quickDragUpdate;
    static FlightReferenceCapture referenceCapture;

    [MenuItem("点点/开发/提取展翅视频参考")]
    public static void ExtractFlightReference()
    {
        if (referenceCapture != null) throw new InvalidOperationException("展翅视频参考正在提取。");
        if (EditorApplication.isPaused) throw new InvalidOperationException("请先取消编辑器暂停，再提取视频参考。");
        referenceCapture = new FlightReferenceCapture();
        referenceCapture.Start();
    }

    sealed class FlightReferenceCapture
    {
        const string Source = "E:/wechat_files/xwechat_files/wxid_lqiwkhrwg6w522_1145/msg/video/2026-10/642cf844738ed6ad13faa69b00d5ca59.mp4";
        const string Folder = "Temp/pipeline-screenshots";
        Scene captureScene;
        GameObject node;
        VideoPlayer video;
        RenderTexture target, tile;
        Texture2D sheet, detail;
        double started;
        long firstFrame, detailFrame, requestedFrame, lastReadyFrame = -1;
        int captured, tileWidth, tileHeight, frameEvents;
        bool pendingRead, disposed, seekCompleted;

        public void Start()
        {
            started = EditorApplication.timeSinceStartup;
            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += Dispose;
            EditorApplication.quitting += Dispose;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            try
            {
                if (!File.Exists(Source)) throw new FileNotFoundException("找不到原始展翅视频。", Source);
                // VideoPlayer needs a normal scene's update loop. Keep it separate from the pet.
                Scene previousActive = SceneManager.GetActiveScene();
                try
                {
                    captureScene = Application.isPlaying ? SceneManager.CreateScene("点点视频参考临时场景")
                        : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                    SceneManager.SetActiveScene(captureScene);
                    node = new GameObject("点点展翅视频参考") { hideFlags = HideFlags.HideAndDontSave };
                }
                finally
                {
                    if (previousActive.IsValid() && previousActive.isLoaded) SceneManager.SetActiveScene(previousActive);
                }
                video = node.AddComponent<VideoPlayer>();
                video.playOnAwake = false;
                video.source = VideoSource.Url;
                video.url = Source;
                video.audioOutputMode = VideoAudioOutputMode.None;
                video.renderMode = VideoRenderMode.RenderTexture;
                video.waitForFirstFrame = true;
                video.skipOnDrop = false;
                video.isLooping = false;
                video.sendFrameReadyEvents = true;
                video.prepareCompleted += Prepared;
                video.frameReady += FrameReady;
                video.seekCompleted += SeekCompleted;
                video.errorReceived += Error;
                video.Prepare();
            }
            catch (Exception error) { Fail(error.Message); }
        }

        void Prepared(VideoPlayer source)
        {
            try
            {
                double rate = source.frameRate;
                if (rate <= 0 || double.IsNaN(rate) || double.IsInfinity(rate)
                    || source.width == 0 || source.height == 0 || source.width > 8192 || source.height > 8192)
                    throw new InvalidOperationException("视频没有有效的帧率或画面尺寸。");
                firstFrame = (long)Math.Round(4.8 * rate);
                detailFrame = (long)Math.Round(5.55 * rate);
                if (source.frameCount > 0 && (ulong)Math.Max(firstFrame + 7, detailFrame) >= source.frameCount)
                    throw new InvalidOperationException("视频长度不足，无法提取指定帧。");
                target = new RenderTexture((int)source.width, (int)source.height, 0, RenderTextureFormat.ARGB32)
                    { hideFlags = HideFlags.HideAndDontSave };
                target.Create();
                source.targetTexture = target;
                tileWidth = Math.Min(360, (int)source.width);
                tileHeight = Math.Max(1, Mathf.RoundToInt(tileWidth * source.height / (float)source.width));
                tile = new RenderTexture(tileWidth, tileHeight, 0, RenderTextureFormat.ARGB32)
                    { hideFlags = HideFlags.HideAndDontSave };
                tile.Create();
                sheet = new Texture2D(tileWidth * 4, tileHeight * 2, TextureFormat.RGB24, false)
                    { hideFlags = HideFlags.HideAndDontSave };
                detail = new Texture2D((int)source.width, (int)source.height, TextureFormat.RGB24, false)
                    { hideFlags = HideFlags.HideAndDontSave };
                if (source.canSetPlaybackSpeed) source.playbackSpeed = .1f;
                Debug.Log("DIANDIAN_REFERENCE_FLIGHT_PREPARED fps=" + rate + " size=" + source.width + "x" + source.height);
                Seek(firstFrame);
            }
            catch (Exception error) { Fail(error.Message); }
        }

        void Seek(long frame)
        {
            requestedFrame = frame;
            pendingRead = false;
            seekCompleted = false;
            video.Pause();
            video.frame = frame;
            video.Play();
        }

        void FrameReady(VideoPlayer source, long frame)
        {
            lastReadyFrame = frame;
            frameEvents++;
            if (disposed || pendingRead || frame != requestedFrame) return;
            // Read on the next editor update, after the decoded frame reaches the render texture.
            source.Pause();
            pendingRead = true;
        }

        void SeekCompleted(VideoPlayer source) => seekCompleted = true;

        string Diagnostics() => " prepared=" + (video && video.isPrepared)
            + " playing=" + (video && video.isPlaying) + " frame=" + (video ? video.frame : -1)
            + " requested=" + requestedFrame + " time=" + (video ? video.time : -1)
            + " readyFrame=" + lastReadyFrame + " readyEvents=" + frameEvents
            + " seekCompleted=" + seekCompleted + " captured=" + captured
            + " previewScene=" + (captureScene.IsValid() && EditorSceneManager.IsPreviewScene(captureScene))
            + " applicationPlaying=" + Application.isPlaying;

        void Update()
        {
            if (disposed) return;
            if (EditorApplication.timeSinceStartup - started > 30)
            {
                Fail("提取超时（30 秒）。若编辑模式未解码，请在已启动且未暂停的播放模式重试此菜单。" + Diagnostics());
                return;
            }
            EditorApplication.QueuePlayerLoopUpdate();
            if (!pendingRead) return;
            pendingRead = false;
            RenderTexture oldActive = RenderTexture.active;
            try
            {
                if (!video || video.frame != requestedFrame)
                    throw new InvalidOperationException("视频解码帧与请求帧不同，已停止，避免生成错误参考。");
                if (captured < 8)
                {
                    Graphics.Blit(target, tile);
                    RenderTexture.active = tile;
                    sheet.ReadPixels(new Rect(0, 0, tileWidth, tileHeight),
                        captured % 4 * tileWidth, (1 - captured / 4) * tileHeight, false);
                    captured++;
                }
                else
                {
                    RenderTexture.active = target;
                    detail.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
                    sheet.Apply(false);
                    detail.Apply(false);
                    Directory.CreateDirectory(Folder);
                    File.WriteAllBytes(Path.Combine(Folder, "reference-flight.png"), sheet.EncodeToPNG());
                    File.WriteAllBytes(Path.Combine(Folder, "reference-flight-detail.png"), detail.EncodeToPNG());
                    Debug.Log("DIANDIAN_REFERENCE_FLIGHT_OK fps=" + video.frameRate
                        + " sequenceFrames=" + firstFrame + ".." + (firstFrame + 7)
                        + " detailFrame=" + detailFrame + " directory=" + Path.GetFullPath(Folder));
                    RenderTexture.active = oldActive;
                    Dispose();
                    return;
                }
                RenderTexture.active = oldActive;
                Seek(captured < 8 ? firstFrame + captured : detailFrame);
            }
            catch (Exception error)
            {
                RenderTexture.active = oldActive;
                Fail(error.Message);
            }
        }

        void Error(VideoPlayer source, string message) => Fail(message);
        void OnPlayModeChanged(PlayModeStateChange state) => Fail("编辑器播放模式发生切换，已取消视频参考提取。");
        void Fail(string message)
        {
            if (disposed) return;
            Debug.LogError("DIANDIAN_REFERENCE_FLIGHT_FAILED " + message);
            Dispose();
        }

        void Dispose()
        {
            if (disposed) return;
            disposed = true;
            EditorApplication.update -= Update;
            AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
            EditorApplication.quitting -= Dispose;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            if (video)
            {
                video.prepareCompleted -= Prepared;
                video.frameReady -= FrameReady;
                video.seekCompleted -= SeekCompleted;
                video.errorReceived -= Error;
                video.Stop();
                video.targetTexture = null;
            }
            if (node) UnityEngine.Object.DestroyImmediate(node);
            if (captureScene.IsValid() && captureScene.isLoaded)
            {
                if (Application.isPlaying) SceneManager.UnloadSceneAsync(captureScene);
                else EditorSceneManager.CloseScene(captureScene, true);
            }
            if (target) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
            if (tile) { tile.Release(); UnityEngine.Object.DestroyImmediate(tile); }
            if (sheet) UnityEngine.Object.DestroyImmediate(sheet);
            if (detail) UnityEngine.Object.DestroyImmediate(detail);
            referenceCapture = null;
        }
    }

    sealed class Snapshot
    {
        public SkinnedMeshRenderer source;
        public Mesh original, baked;
        public Material[] materials;
        public bool enabled;
        public float[] blends;
        public GameObject node;
        public MeshRenderer renderer;
    }

    [MenuItem("点点/开发/播放展翅")]
    public static void PlayFlight() => SetFlight(true);

    [MenuItem("点点/开发/停止展翅")]
    public static void StopFlight() => SetFlight(false);

    static void SetFlight(bool flying)
    {
        if (!Application.isPlaying) throw new InvalidOperationException("请先进入播放模式。");
        var pet = UnityEngine.Object.FindFirstObjectByType<DiandianDesktopPet>();
        if (!pet || !pet.flight) throw new InvalidOperationException("当前场景没有三维展翅桌宠。");
        // Exercise the same drag callback as the native window. This also keeps
        // automatic idle actions from cancelling a manually inspected flight.
        pet.SendMessage("OnDrag", flying, SendMessageOptions.RequireReceiver);
    }

    [MenuItem("点点/开发/检查快速拖动")]
    public static void CheckQuickDrag()
    {
        if (!Application.isPlaying || EditorApplication.isPaused)
            throw new InvalidOperationException("请在未暂停的播放模式检查快速拖动。");
        if (quickDragUpdate != null) throw new InvalidOperationException("快速拖动检查正在运行。");
        var pet = UnityEngine.Object.FindFirstObjectByType<DiandianDesktopPet>();
        if (!pet || !pet.flight || !pet.rig || !pet.flight.bodyRenderer)
            throw new InvalidOperationException("当前场景缺少桌宠、展翅或骨骼引用。");
        var flight = pet.flight;
        var rig = pet.rig;
        if (flight.IsActive) throw new InvalidOperationException("请先停止展翅并等待收翼后再检查。");
        double start = EditorApplication.timeSinceStartup;
        int startFrame = Time.frameCount, fullSpreadSamples = 0;
        float maxSpread = 0;
        bool curledAtFullSpread = true;
        void Finish(bool passed, string reason)
        {
            EditorApplication.update -= quickDragUpdate;
            quickDragUpdate = null;
            string report = "DIANDIAN_QUICK_DRAG_CHECK passed=" + passed + " reason=" + reason
                + " maxSpread=" + maxSpread.ToString("F4") + " fullSpreadSamples=" + fullSpreadSamples
                + " curledAtFullSpread=" + curledAtFullSpread
                + " elapsed=" + (EditorApplication.timeSinceStartup - start).ToString("F3");
            if (passed) Debug.Log(report); else Debug.LogError(report);
        }
        quickDragUpdate = () =>
        {
            if (!Application.isPlaying || !pet || !flight || !rig)
            { Finish(false, "play-stopped-or-object-destroyed"); return; }
            maxSpread = Mathf.Max(maxSpread, flight.Spread);
            if (flight.Spread >= .99f)
            {
                fullSpreadSamples++;
                curledAtFullSpread &= rig.motion == DiandianRig.Motion.CurlFeet;
            }
            if (EditorApplication.timeSinceStartup - start >= 5)
            { Finish(false, "timeout-5s"); return; }
            if (Time.frameCount <= startFrame || flight.IsActive || rig.motion != DiandianRig.Motion.Idle) return;
            bool restored = flight.bodyRenderer && flight.bodyRenderer.enabled
                && flight.restingMesh && flight.bodyRenderer.sharedMesh == flight.restingMesh;
            bool wingsDisabled = flight.wings != null && flight.wings.Length == 2;
            if (flight.wings != null)
                foreach (var wing in flight.wings) wingsDisabled &= wing != null && wing.renderer && !wing.renderer.enabled;
            bool passed = maxSpread >= .99f && fullSpreadSamples > 0 && curledAtFullSpread && restored && wingsDisabled;
            Finish(passed, "idle;originalMeshRestored=" + restored + ";wingsDisabled=" + wingsDisabled);
        };
        EditorApplication.update += quickDragUpdate;
        try
        {
            // Both callbacks run in this frame: no artificial hold between press and release.
            pet.SendMessage("OnDrag", true, SendMessageOptions.RequireReceiver);
            pet.SendMessage("OnDrag", false, SendMessageOptions.RequireReceiver);
        }
        catch
        {
            Finish(false, "drag-callback-error");
            throw;
        }
    }

    [MenuItem("点点/开发/检查三维展翅")]
    public static void Run() => CaptureFlight(false);

    [MenuItem("点点/开发/导出展翅连续预览")]
    public static void RenderFlightMotion() => CaptureFlight(true);

    static void CaptureFlight(bool motion)
    {
        if (Application.isPlaying) throw new InvalidOperationException("请先停止播放，再检查三维展翅。");
        Scene scene = SceneManager.GetActiveScene();
        if (scene.path != ScenePath) throw new InvalidOperationException("请先打开 " + ScenePath + "。");
        DiandianFlight flight = null;
        foreach (var item in UnityEngine.Object.FindObjectsByType<DiandianFlight>())
            if (item.gameObject.scene == scene)
            {
                if (flight) throw new InvalidOperationException("当前桌宠场景有多个展翅控制器，无法确定检查对象。");
                flight = item;
            }
        if (!flight || !flight.bodyRenderer || !flight.restingMesh || !flight.flightMesh || flight.wings == null || flight.wings.Length != 2)
            throw new InvalidOperationException("当前桌宠场景缺少完整的三维展翅网格与双翼引用。");
        Camera sourceCamera = flight.viewCamera;
        if (!sourceCamera) throw new InvalidOperationException("展翅控制器没有预览相机。");
        foreach (var wing in flight.wings)
            if (wing == null || !wing.renderer || !wing.renderer.sharedMesh)
                throw new InvalidOperationException("双翼必须引用真实的 SkinnedMeshRenderer 网格。");

        Transform root = flight.transform.root;
        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        var positions = new Vector3[transforms.Length];
        var rotations = new Quaternion[transforms.Length];
        for (int i = 0; i < transforms.Length; i++)
        { positions[i] = transforms[i].localPosition; rotations[i] = transforms[i].localRotation; }
        var rig = flight.GetComponent<DiandianRig>();
        float oldMouth = rig ? rig.mouthOpen : 0, oldSpread = flight.Spread;
        Vector3 cameraPosition = sourceCamera.transform.position;
        Quaternion cameraRotation = sourceCamera.transform.rotation;
        var snapshots = new List<Snapshot>();
        GameObject cameraNode = null, labelNode = null;
        RenderTexture target = null;
        Texture2D sheet = null;
        RenderTexture oldActive = RenderTexture.active;
        try
        {
            foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!skin.sharedMesh) continue;
                var snapshot = new Snapshot { source = skin, original = skin.sharedMesh,
                    materials = skin.sharedMaterials, enabled = skin.enabled,
                    blends = new float[skin.sharedMesh.blendShapeCount] };
                for (int b = 0; b < snapshot.blends.Length; b++) snapshot.blends[b] = skin.GetBlendShapeWeight(b);
                snapshots.Add(snapshot);
                snapshot.node = new GameObject("展翅检查快照") { hideFlags = HideFlags.HideAndDontSave, layer = skin.gameObject.layer };
                snapshot.node.transform.SetParent(skin.transform, false);
                snapshot.baked = new Mesh { name = "展翅检查烘焙", hideFlags = HideFlags.HideAndDontSave };
                snapshot.node.AddComponent<MeshFilter>().sharedMesh = snapshot.baked;
                snapshot.renderer = snapshot.node.AddComponent<MeshRenderer>();
                snapshot.renderer.shadowCastingMode = skin.shadowCastingMode;
                snapshot.renderer.receiveShadows = skin.receiveShadows;
            }
            if (rig) rig.ResetToRestPose();
            ValidateMeshes(flight);
            CheckRestoration(flight, snapshots);

            cameraNode = new GameObject("展翅检查相机") { hideFlags = HideFlags.HideAndDontSave };
            var camera = cameraNode.AddComponent<Camera>();
            camera.CopyFrom(sourceCamera); camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.32f, .32f, .32f, 1);
            camera.rect = new Rect(0, 0, 1, 1); camera.orthographic = true;
            camera.aspect = (float)Width / Height; camera.fieldOfView = 37;
            camera.nearClipPlane = .03f; camera.farClipPlane = 100;
            camera.allowHDR = false; camera.allowMSAA = true;
            target = new RenderTexture(Width, Height, 24) { antiAliasing = 4 };
            camera.targetTexture = target;
            if (!motion) sheet = new Texture2D(Width * 4, Height * 2, TextureFormat.RGB24, false);

            float[] amounts = { 1, 1, 0, .001f, .25f, .60f, 1, 1 };
            float[] phases = { Mathf.PI / 2, Mathf.PI * 1.5f, 0, Mathf.PI / 2, Mathf.PI / 2, Mathf.PI / 2, Mathf.PI / 2, Mathf.PI * 1.5f };
            string[] labels = { "SHOULDER / UPSTROKE", "SHOULDER / DOWNSTROKE", "ORIGINAL / REST", "REBUILT / FOLD 0.001",
                "UNFOLD / 0.25", "UNFOLD / 0.60", "FULL / UPSTROKE", "FULL / DOWNSTROKE" };
            BakePose(flight, snapshots, 0, 0, true);
            Snapshot bodySnapshot = snapshots.Find(item => item.source == flight.bodyRenderer);
            CheckRestSnapshot(bodySnapshot);
            CheckDesktopFraming(flight, snapshots);
            Vector3 focus = UsedWorldBounds(bodySnapshot).center;
            Vector3 front = flight.bodyRenderer.transform.TransformDirection(new Vector3(1, .18f, 1)).normalized;
            float distance = 4.5f;
            camera.orthographicSize = 1.8f;
            // Only referenced vertices affect framing; retained but unused cut-wing
            // vertices and empty corners of a rotated AABB cannot shrink the bird.
            for (int i = 0; i < amounts.Length; i++)
            {
                BakePose(flight, snapshots, amounts[i], phases[i], true);
                FitOrthographic(snapshots, focus, front, camera, ref distance);
            }

            labelNode = new GameObject("展翅检查标签") { hideFlags = HideFlags.HideAndDontSave };
            var label = labelNode.AddComponent<TextMesh>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            // TextMesh maps font pixels to characterSize / 10 world units.
            // This keeps captions near 20 screen pixels, independent of framing.
            label.fontSize = 24; label.characterSize = camera.orthographicSize * 2 / Height * 10;
            label.anchor = TextAnchor.UpperLeft; label.color = Color.white;
            label.GetComponent<MeshRenderer>().sharedMaterial = label.font.material;
            label.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
            label.GetComponent<MeshRenderer>().receiveShadows = false;
            float overviewSize = camera.orthographicSize;
            if (motion)
            {
                RenderMotion(flight, snapshots, camera, label, target, focus, front, distance);
                return;
            }
            for (int i = 0; i < amounts.Length; i++)
            {
                BakePose(flight, snapshots, amounts[i], phases[i], true);
                // ApplyPose moves the scene camera; set this capture camera only after posing.
                Vector3 captureFocus = i < 2 ? flight.bodyRenderer.transform.TransformPoint(new Vector3(.078f, .650f, .135f)) : focus;
                camera.orthographicSize = i < 2 ? .55f : overviewSize;
                camera.transform.position = captureFocus + front * distance;
                camera.transform.LookAt(captureFocus);
                label.text = labels[i];
                label.characterSize = camera.orthographicSize * 2 / Height * 10;
                labelNode.transform.SetPositionAndRotation(camera.ViewportToWorldPoint(new Vector3(.035f, .96f, 1)), camera.transform.rotation);
                camera.Render(); RenderTexture.active = target;
                sheet.ReadPixels(new Rect(0, 0, Width, Height), (i % 4) * Width, (1 - i / 4) * Height, false);
            }
            sheet.Apply();
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
            File.WriteAllBytes(OutputPath, sheet.EncodeToPNG());
            Debug.Log("DIANDIAN_FLIGHT_CHECK image=" + Path.GetFullPath(OutputPath)
                + " panels=8 bakedSkins=" + snapshots.Count + " sharedCameraDistance=" + distance.ToString("F3")
                + " orthographicSize=" + camera.orthographicSize.ToString("F3") + " usedBodyFocus=" + focus.ToString("F3")
                + " geometryChecksOnly=true visualReviewRequired=true");
        }
        finally
        {
            flight.ApplyPose(oldSpread, 0);
            foreach (var snapshot in snapshots)
            {
                snapshot.source.sharedMesh = snapshot.original;
                snapshot.source.sharedMaterials = snapshot.materials;
                snapshot.source.enabled = snapshot.enabled;
                for (int b = 0; b < snapshot.blends.Length; b++) snapshot.source.SetBlendShapeWeight(b, snapshot.blends[b]);
                if (snapshot.node) UnityEngine.Object.DestroyImmediate(snapshot.node);
                if (snapshot.baked) UnityEngine.Object.DestroyImmediate(snapshot.baked);
            }
            for (int i = 0; i < transforms.Length; i++)
            { transforms[i].localPosition = positions[i]; transforms[i].localRotation = rotations[i]; }
            if (rig) rig.mouthOpen = oldMouth;
            sourceCamera.transform.SetPositionAndRotation(cameraPosition, cameraRotation);
            RenderTexture.active = oldActive;
            if (labelNode) UnityEngine.Object.DestroyImmediate(labelNode);
            if (cameraNode) UnityEngine.Object.DestroyImmediate(cameraNode);
            if (sheet) UnityEngine.Object.DestroyImmediate(sheet);
            if (target) UnityEngine.Object.DestroyImmediate(target);
        }
    }

    static void RenderMotion(DiandianFlight flight, List<Snapshot> snapshots, Camera camera, TextMesh label,
        RenderTexture target, Vector3 focus, Vector3 front, float distance)
    {
        const int frames = 90, fps = 30;
        const string path = "Temp/pipeline-screenshots/flight-motion.mp4";
        (float amount, float phase) Pose(int frame)
        {
            float time = frame / (float)(frames - 1) * 3;
            if (time <= .9f) return (Mathf.SmoothStep(0, 1, time / .9f), Mathf.PI / 2);
            if (time >= 2.1f) return (Mathf.SmoothStep(1, 0, (time - 2.1f) / .9f), Mathf.PI / 2);
            // Four complete beats return to the starting phase before the closing pass.
            return (1, Mathf.PI / 2 + (time - .9f) / 1.2f * Mathf.PI * 8);
        }
        // Fit the whole actual sequence once, so camera zoom does not mask unfolding errors.
        for (int i = 0; i < frames; i++)
        {
            var pose = Pose(i);
            BakePose(flight, snapshots, pose.amount, pose.phase, true);
            FitOrthographic(snapshots, focus, front, camera, ref distance);
        }
        camera.transform.position = focus + front * distance;
        camera.transform.LookAt(focus);
        label.characterSize = camera.orthographicSize * 2 / Height * 10;
        label.transform.SetPositionAndRotation(camera.ViewportToWorldPoint(new Vector3(.035f, .96f, 1)), camera.transform.rotation);
        var pixels = new Texture2D(Width, Height, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        RenderTexture previous = RenderTexture.active;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var attributes = new VideoTrackAttributes { frameRate = new MediaRational(fps), width = Width, height = Height, includeAlpha = false };
            using (var encoder = new MediaEncoder(path, attributes))
                for (int i = 0; i < frames; i++)
                {
                    var pose = Pose(i);
                    BakePose(flight, snapshots, pose.amount, pose.phase, true);
                    label.text = "SLOW INSPECTION / " + (i < 27 ? "OPEN" : i < 63 ? "FLAP" : "CLOSE")
                        + " / SPREAD " + pose.amount.ToString("F2");
                    camera.Render(); RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
                    pixels.Apply(false);
                    if (!encoder.AddFrame(pixels)) throw new IOException("展翅预览第 " + i + " 帧编码失败。");
                }
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new IOException("展翅视频编码器未生成有效文件。");
            Debug.Log("DIANDIAN_FLIGHT_MOTION video=" + Path.GetFullPath(path) + " frames=" + frames + " fps=" + fps
                + " inspectionSlowMotion=true geometryChecksOnly=true visualReviewRequired=true");
        }
        finally
        {
            RenderTexture.active = previous;
            UnityEngine.Object.DestroyImmediate(pixels);
        }
    }

    static void BakePose(DiandianFlight flight, List<Snapshot> snapshots, float amount, float phase, bool showWings)
    {
        foreach (var snapshot in snapshots) snapshot.source.enabled = snapshot.enabled;
        flight.ApplyPose(amount, phase);
        if (!showWings) foreach (var wing in flight.wings) wing.renderer.enabled = false;
        foreach (var snapshot in snapshots)
        {
            snapshot.renderer.enabled = snapshot.source.enabled && snapshot.source.gameObject.activeInHierarchy;
            snapshot.renderer.sharedMaterials = snapshot.source.sharedMaterials;
            // Match RenderMotionCheck: useScale=true preserves renderer-local
            // coordinates in this rig; false already includes its presentation scale.
            // Static copies avoid Camera.Render reusing one frame's GPU skinning cache.
            snapshot.source.BakeMesh(snapshot.baked, true);
            snapshot.baked.RecalculateBounds();
            snapshot.source.enabled = false;
        }
    }

    static Bounds UsedWorldBounds(Snapshot snapshot)
    {
        Vector3[] vertices = snapshot.baked.vertices;
        int[] indices = snapshot.baked.triangles;
        if (indices.Length == 0) throw new InvalidOperationException("烘焙网格没有实际三角形。");
        var bounds = new Bounds(snapshot.node.transform.TransformPoint(vertices[indices[0]]), Vector3.zero);
        foreach (int index in new HashSet<int>(indices)) bounds.Encapsulate(snapshot.node.transform.TransformPoint(vertices[index]));
        return bounds;
    }

    static void CheckRestSnapshot(Snapshot snapshot)
    {
        Vector3[] original = snapshot.source.sharedMesh.vertices, baked = snapshot.baked.vertices;
        int[] indices = snapshot.source.sharedMesh.triangles;
        if (original.Length != baked.Length || indices.Length == 0)
            throw new InvalidOperationException("静止快照顶点数与原网格不一致。");
        var expected = new Bounds(snapshot.source.transform.TransformPoint(original[indices[0]]), Vector3.zero);
        float maxError = 0;
        foreach (int index in new HashSet<int>(indices))
        {
            Vector3 before = snapshot.source.transform.TransformPoint(original[index]);
            Vector3 after = snapshot.node.transform.TransformPoint(baked[index]);
            expected.Encapsulate(before);
            maxError = Mathf.Max(maxError, Vector3.Distance(before, after));
        }
        float actualHeight = UsedWorldBounds(snapshot).size.y;
        Debug.Log("DIANDIAN_FLIGHT_CHECK restSnapshot expectedWorldHeight=" + expected.size.y.ToString("F4")
            + " actualWorldHeight=" + actualHeight.ToString("F4") + " maxWorldVertexError=" + maxError.ToString("F6"));
        if (!float.IsFinite(maxError) || maxError > .002f)
            throw new InvalidOperationException("静止快照与原模型世界坐标不一致，检查 BakeMesh 缩放。");
    }

    static void CheckDesktopFraming(DiandianFlight flight, List<Snapshot> snapshots)
    {
        Transform modelRoot = null;
        foreach (var pet in UnityEngine.Object.FindObjectsByType<DiandianDesktopPet>())
            if (pet.flight == flight) { modelRoot = pet.modelRoot; break; }
        if (!modelRoot)
        {
            Debug.LogWarning("DIANDIAN_FLIGHT_VIEWPORT skipped: desktop modelRoot is missing");
            return;
        }
        Camera camera = flight.viewCamera;
        Quaternion originalFacing = modelRoot.localRotation, cameraRotation = camera.transform.rotation;
        Vector3 cameraPosition = camera.transform.position;
        float level = Mathf.Asin(-15f / 50f);
        const int cyclePhases = 24;
        var samples = new List<(float amount, float phase)> { (0, 0) };
        for (int i = 0; i < cyclePhases; i++) samples.Add((1, i * Mathf.PI * 2 / cyclePhases));
        // At transition amounts test both travel directions, extremes and equal-height levels.
        foreach (float amount in new[] { .001f, .25f, .5f, .75f })
            foreach (float phase in new[] { 0, Mathf.PI / 2, Mathf.PI, Mathf.PI * 1.5f, level, Mathf.PI - level })
                samples.Add((amount, phase));
        var usedIndices = new Dictionary<Mesh, int[]>();
        try
        {
            Debug.Log("DIANDIAN_FLIGHT_VIEWPORT camera=" + camera.name + " aspect=" + camera.aspect.ToString("F3")
                + " fov=" + camera.fieldOfView.ToString("F2") + " orthographic=" + camera.orthographic
                + " flightCameraDistance=" + flight.flightCameraDistance.ToString("F3"));
            for (int direction = 0; direction < 2; direction++)
            {
                // This is the same modelRoot rotation used by the desktop menu.
                modelRoot.localRotation = direction == 0 ? originalFacing : Quaternion.Euler(0, -120, 0);
                Vector2 worstMin = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
                Vector2 worstMax = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
                float nearest = float.PositiveInfinity, farthest = float.NegativeInfinity;
                string left = "", right = "", bottom = "", top = "";
                int outsideXYTotal = 0, outsideDepthTotal = 0, invalidTotal = 0;
                var clipped = new List<string>();
                foreach (var sample in samples)
                {
                    // ApplyPose updates the real desktop camera; retain its FOV,
                    // aspect, projection and rotation exactly as configured.
                    BakePose(flight, snapshots, sample.amount, sample.phase, true);
                    Vector2 minimum = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
                    Vector2 maximum = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
                    float near = float.PositiveInfinity, far = float.NegativeInfinity;
                    int outsideXY = 0, outsideDepth = 0, invalid = 0;
                    foreach (var snapshot in snapshots)
                    {
                        if (!snapshot.renderer.enabled) continue;
                        Vector3[] points = snapshot.baked.vertices;
                        Mesh mesh = snapshot.source.sharedMesh;
                        if (!usedIndices.TryGetValue(mesh, out int[] indices))
                        {
                            var unique = new HashSet<int>(snapshot.baked.triangles);
                            indices = new int[unique.Count]; unique.CopyTo(indices);
                            usedIndices.Add(mesh, indices);
                        }
                        foreach (int index in indices)
                        {
                            Vector3 viewport = camera.WorldToViewportPoint(snapshot.node.transform.TransformPoint(points[index]));
                            if (!float.IsFinite(viewport.x) || !float.IsFinite(viewport.y) || !float.IsFinite(viewport.z))
                            { invalid++; continue; }
                            Vector2 xy = new Vector2(viewport.x, viewport.y);
                            minimum = Vector2.Min(minimum, xy); maximum = Vector2.Max(maximum, xy);
                            near = Mathf.Min(near, viewport.z); far = Mathf.Max(far, viewport.z);
                            if (viewport.x < 0 || viewport.x > 1 || viewport.y < 0 || viewport.y > 1) outsideXY++;
                            if (viewport.z < camera.nearClipPlane || viewport.z > camera.farClipPlane) outsideDepth++;
                        }
                    }
                    string tag = "a" + sample.amount.ToString("F3") + "@" + Mathf.Repeat(sample.phase * Mathf.Rad2Deg, 360).ToString("F1") + "deg";
                    if (minimum.x < worstMin.x) left = tag;
                    if (maximum.x > worstMax.x) right = tag;
                    if (minimum.y < worstMin.y) bottom = tag;
                    if (maximum.y > worstMax.y) top = tag;
                    worstMin = Vector2.Min(worstMin, minimum); worstMax = Vector2.Max(worstMax, maximum);
                    nearest = Mathf.Min(nearest, near); farthest = Mathf.Max(farthest, far);
                    outsideXYTotal += outsideXY; outsideDepthTotal += outsideDepth; invalidTotal += invalid;
                    if (outsideXY > 0 || outsideDepth > 0 || invalid > 0) clipped.Add(tag);
                }
                string facing = direction == 0 ? "original" : "menu-minus120";
                string report = "DIANDIAN_FLIGHT_VIEWPORT facing=" + facing + " yaw=" + modelRoot.localEulerAngles.y.ToString("F1")
                    + " cyclePhases=" + cyclePhases + " samples=" + samples.Count
                    + " worstViewport=" + worstMin.ToString("F4") + ".." + worstMax.ToString("F4")
                    + " edgeSamples=L:" + left + ",R:" + right + ",B:" + bottom + ",T:" + top
                    + " depth=" + nearest.ToString("F3") + ".." + farthest.ToString("F3")
                    + " clippedSamples=" + clipped.Count + " outsideXY=" + outsideXYTotal
                    + " outsideDepth=" + outsideDepthTotal + " invalid=" + invalidTotal;
                if (clipped.Count == 0) Debug.Log(report);
                else Debug.LogWarning(report + " clippedAmountPhase=[" + string.Join(",", clipped) + "]");
            }
        }
        finally
        {
            modelRoot.localRotation = originalFacing;
            BakePose(flight, snapshots, 0, 0, true);
            camera.transform.SetPositionAndRotation(cameraPosition, cameraRotation);
        }
    }

    static void FitOrthographic(List<Snapshot> snapshots, Vector3 focus, Vector3 back, Camera camera, ref float distance)
    {
        back.Normalize();
        Quaternion view = Quaternion.LookRotation(-back, Vector3.up);
        Vector3 right = view * Vector3.right, up = view * Vector3.up;
        foreach (var snapshot in snapshots)
        {
            if (!snapshot.renderer.enabled) continue;
            Vector3[] vertices = snapshot.baked.vertices;
            int[] indices = snapshot.baked.triangles;
            foreach (int index in new HashSet<int>(indices))
            {
                Vector3 world = snapshot.node.transform.TransformPoint(vertices[index]);
                Vector3 offset = world - focus;
                camera.orthographicSize = Mathf.Max(camera.orthographicSize,
                    Mathf.Abs(Vector3.Dot(offset, up)) / .84f,
                    Mathf.Abs(Vector3.Dot(offset, right)) / (camera.aspect * .90f));
                distance = Mathf.Max(distance, Vector3.Dot(offset, back) + 1);
            }
        }
    }

    static void ValidateMeshes(DiandianFlight flight)
    {
        MeshStats("originalBody", flight.restingMesh);
        MeshStats("flightBody", flight.flightMesh);
        foreach (var wing in flight.wings)
        {
            Mesh mesh = wing.renderer.sharedMesh;
            MeshStats("wing" + wing.side, mesh);
            int fold = mesh.GetBlendShapeIndex("Fold");
            Debug.Log("DIANDIAN_FLIGHT_CHECK wing=" + wing.side + " foldPresent=" + (fold >= 0)
                + " foldFrames=" + (fold < 0 ? 0 : mesh.GetBlendShapeFrameCount(fold)));
            if (fold != 0) throw new InvalidOperationException("展翅控制要求真实双翼的第一个 BlendShape 为 Fold。");
        }
        ReportPreservation(flight.restingMesh, flight.flightMesh);
        CheckTransferredShoulderPlumage(flight);
        CheckWingBoneLengths(flight);
    }

    static float BoneInfluence(BoneWeight weight, int bone)
    {
        float result = 0;
        if (weight.boneIndex0 == bone) result += weight.weight0;
        if (weight.boneIndex1 == bone) result += weight.weight1;
        if (weight.boneIndex2 == bone) result += weight.weight2;
        if (weight.boneIndex3 == bone) result += weight.weight3;
        return result;
    }

    static void CheckTransferredShoulderPlumage(DiandianFlight flight)
    {
        Mesh original = flight.restingMesh;
        Vector3[] sourceVertices = original.vertices;
        Vector2[] sourceUV = original.uv;
        int[] sourceTriangles = original.triangles;
        Transform[] originalBones = flight.bodyRenderer.bones;
        var transferred = new HashSet<int>();
        for (int t = 0; t < sourceTriangles.Length; t += 3)
            if (DiandianFlightBody.IsShoulderTriangle(sourceVertices[sourceTriangles[t]],
                sourceVertices[sourceTriangles[t + 1]], sourceVertices[sourceTriangles[t + 2]])) transferred.Add(t);
        if (transferred.Count == 0) throw new InvalidOperationException("没有可核对的原始肩羽三角形。");
        var retained = new HashSet<(int, int, int)>();
        int[] bodyTriangles = flight.flightMesh.GetTriangles(0);
        for (int t = 0; t < bodyTriangles.Length; t += 3)
            retained.Add((bodyTriangles[t], bodyTriangles[t + 1], bodyTriangles[t + 2]));
        foreach (int t in transferred)
            if (retained.Contains((sourceTriangles[t], sourceTriangles[t + 1], sourceTriangles[t + 2])))
                throw new InvalidOperationException("转移肩羽仍重复留在飞行身体网格中。");
        var checkedTriangles = new HashSet<int>();
        flight.ApplyPose(0, 0);
        foreach (var wing in flight.wings)
        {
            var skin = wing.renderer;
            Mesh mesh = skin.sharedMesh;
            if (mesh.subMeshCount != 6 || skin.sharedMaterials.Length != 6
                || skin.sharedMaterials[5] != flight.restingMaterials[0])
                throw new InvalidOperationException("原肩羽必须位于双翼第六个子网格并使用原模型材质。");
            var expected = new List<int>();
            for (int t = 0; t < sourceTriangles.Length; t += 3)
            {
                if (!transferred.Contains(t)) continue;
                int a = sourceTriangles[t], b = sourceTriangles[t + 1], c = sourceTriangles[t + 2];
                if (sourceVertices[a].x * wing.side <= 0 || sourceVertices[b].x * wing.side <= 0
                    || sourceVertices[c].x * wing.side <= 0) continue;
                if (!checkedTriangles.Add(t)) throw new InvalidOperationException("同一原肩羽三角形被重复分配到双翼。");
                expected.Add(a); expected.Add(b); expected.Add(c);
            }
            int[] actual = mesh.GetTriangles(5);
            if (expected.Count == 0 || expected.Count != actual.Length)
                throw new InvalidOperationException("肩羽三角形数量与原模型的同侧转移区不一致。");
            var sourceToPatch = new Dictionary<int, int>();
            var patchToSource = new Dictionary<int, int>();
            for (int i = 0; i < actual.Length; i++)
            {
                int sourceIndex = expected[i], patchIndex = actual[i];
                if ((sourceToPatch.TryGetValue(sourceIndex, out int previousPatch) && previousPatch != patchIndex)
                    || (patchToSource.TryGetValue(patchIndex, out int previousSource) && previousSource != sourceIndex))
                    throw new InvalidOperationException("肩羽拓扑与原三角顺序不一致，或同一源顶点被重复复制。");
                sourceToPatch[sourceIndex] = patchIndex;
                patchToSource[patchIndex] = sourceIndex;
            }
            Vector2[] uv = mesh.uv;
            BoneWeight[] weights = mesh.boneWeights;
            Transform[] bones = skin.bones;
            if (bones.Length != 7 || mesh.bindposes.Length != 7 || bones[0] != wing.shoulder
                || bones[1] != wing.elbow || bones[2] != wing.wrist || bones[3] != originalBones[1]
                || bones[4] != originalBones[2] || bones[5] != originalBones[0] || bones[6] != wing.shoulder)
                throw new InvalidOperationException("肩羽需要第七骨槽引用同一肩骨并使用独立闭合绑定姿态。");
            float maxPositionError = 0, maxUVError = 0, maxClosedMatrixError = 0, maxPatchSlotWeightError = 0;
            float maxPoseEdgeRelativeError = 0, maxPoseEdgeAbsoluteError = 0;
            int rigidPatchVertices = 0, poseEdgeSamples = 0, distortedPoseEdges = 0;
            var baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                skin.BakeMesh(baked, true);
                Vector3[] positions = baked.vertices;
                Matrix4x4 toSource = flight.bodyRenderer.transform.worldToLocalMatrix * skin.transform.localToWorldMatrix;
                foreach (var pair in sourceToPatch)
                {
                    int sourceIndex = pair.Key, patchIndex = pair.Value;
                    maxPositionError = Mathf.Max(maxPositionError,
                        Vector3.Distance(toSource.MultiplyPoint3x4(positions[patchIndex]), sourceVertices[sourceIndex]));
                    maxUVError = Mathf.Max(maxUVError, Vector2.Distance(uv[patchIndex], sourceUV[sourceIndex]));
                    float slotError = Mathf.Abs(BoneInfluence(weights[patchIndex], 6) - 1);
                    for (int b = 0; b < 6; b++) slotError = Mathf.Max(slotError, BoneInfluence(weights[patchIndex], b));
                    maxPatchSlotWeightError = Mathf.Max(maxPatchSlotWeightError, slotError);
                    if (slotError <= .00001f) rigidPatchVertices++;
                }
                for (int b = 0; b < bones.Length; b++)
                {
                    Matrix4x4 actualMatrix = skin.transform.worldToLocalMatrix * bones[b].localToWorldMatrix * mesh.bindposes[b];
                    // Generated feathers bind open; original patch slot 6 and source-body slots bind closed.
                    Matrix4x4 expectedMatrix = b < 3 ? DiandianWingBuilder.ClosedSkinMatrix(wing.side, b) : Matrix4x4.identity;
                    for (int e = 0; e < 16; e++)
                        maxClosedMatrixError = Mathf.Max(maxClosedMatrixError, Mathf.Abs(actualMatrix[e] - expectedMatrix[e]));
                }
                foreach (float phase in new[] { Mathf.PI / 2, Mathf.PI * 1.5f })
                {
                    flight.ApplyPose(1, phase);
                    skin.BakeMesh(baked, true);
                    positions = baked.vertices;
                    for (int i = 0; i < actual.Length; i += 3)
                        for (int edge = 0; edge < 3; edge++)
                        {
                            int a = i + edge, b = i + (edge + 1) % 3;
                            float originalLength = Vector3.Distance(sourceVertices[expected[a]], sourceVertices[expected[b]]);
                            if (originalLength < .000001f) continue;
                            float posedLength = Vector3.Distance(toSource.MultiplyPoint3x4(positions[actual[a]]),
                                toSource.MultiplyPoint3x4(positions[actual[b]]));
                            poseEdgeSamples++;
                            if (!float.IsFinite(posedLength)) { distortedPoseEdges++; continue; }
                            float error = Mathf.Abs(posedLength - originalLength);
                            maxPoseEdgeRelativeError = Mathf.Max(maxPoseEdgeRelativeError, error / originalLength);
                            maxPoseEdgeAbsoluteError = Mathf.Max(maxPoseEdgeAbsoluteError, error);
                            // A small absolute floor avoids float quantization dominating tiny source edges.
                            if (error > Mathf.Max(.000002f, originalLength * .001f)) distortedPoseEdges++;
                        }
                }
            }
            finally { flight.ApplyPose(0, 0); UnityEngine.Object.DestroyImmediate(baked); }
            Debug.Log("DIANDIAN_SHOULDER_TRANSFER side=" + wing.side + " originalTriangles=" + actual.Length / 3
                + " uniqueSourceVertices=" + sourceToPatch.Count + " rigidPatchVertices=" + rigidPatchVertices
                + " maxClosedPositionError=" + maxPositionError.ToString("E3") + " maxUVError=" + maxUVError.ToString("E3")
                + " maxPatchSlotWeightError=" + maxPatchSlotWeightError.ToString("E3")
                + " maxClosedSkinMatrixError=" + maxClosedMatrixError.ToString("E3")
                + " poseEdgeSamples=" + poseEdgeSamples + " maxPoseEdgeRelativeError=" + maxPoseEdgeRelativeError.ToString("E3")
                + " maxPoseEdgeAbsoluteError=" + maxPoseEdgeAbsoluteError.ToString("E3") + " distortedPoseEdges=" + distortedPoseEdges);
            if (!float.IsFinite(maxPositionError) || maxPositionError > .0001f || maxUVError > .0000001f
                || rigidPatchVertices != sourceToPatch.Count || maxPatchSlotWeightError > .00001f || maxClosedMatrixError > .0001f
                || poseEdgeSamples == 0 || distortedPoseEdges > 0)
                throw new InvalidOperationException("转移肩羽的闭合位置、原UV、刚性绑定或动态三角边长未通过核对。");
        }
        if (!checkedTriangles.SetEquals(transferred))
            throw new InvalidOperationException("仍有转移肩羽三角形没有且仅有一份实际双翼对应面。");
    }

    static void CheckWingBoneLengths(DiandianFlight flight)
    {
        float maxError = 0;
        int samples = 0;
        try
        {
            foreach (float amount in new[] { 0, .001f, .10f, .25f, .50f, .60f, .75f, 1 })
                foreach (float phase in new[] { 0, Mathf.PI / 2, Mathf.PI, Mathf.PI * 1.5f })
                {
                    flight.ApplyPose(amount, phase);
                    samples++;
                    foreach (var wing in flight.wings)
                    {
                        if (wing.elbow.parent != wing.shoulder || wing.wrist.parent != wing.elbow)
                            throw new InvalidOperationException("肩、肘、腕没有形成连续父子骨链。");
                        Transform space = flight.bodyRenderer.transform;
                        Vector3 shoulder = space.InverseTransformPoint(wing.shoulder.position);
                        Vector3 elbow = space.InverseTransformPoint(wing.elbow.position);
                        Vector3 wrist = space.InverseTransformPoint(wing.wrist.position);
                        float upperLength = Vector3.Distance(DiandianWingBuilder.Shoulder(wing.side), DiandianWingBuilder.Elbow(wing.side));
                        float lowerLength = Vector3.Distance(DiandianWingBuilder.Elbow(wing.side), DiandianWingBuilder.Wrist(wing.side));
                        maxError = Mathf.Max(maxError, Mathf.Abs(Vector3.Distance(shoulder, elbow) - upperLength),
                            Mathf.Abs(Vector3.Distance(elbow, wrist) - lowerLength));
                    }
                }
        }
        finally { flight.ApplyPose(0, 0); }
        Debug.Log("DIANDIAN_WING_BONE_LENGTH samples=" + samples + " maxLengthError=" + maxError.ToString("E3")
            + " geometryChecksOnly=true visualReviewRequired=true");
        if (!float.IsFinite(maxError) || maxError > .0001f)
            throw new InvalidOperationException("展开或拍翼过程中肩肘腕骨长发生变化。");
    }

    static void MeshStats(string name, Mesh mesh)
    {
        int[] indices = mesh.triangles;
        Vector3[] normals = mesh.normals, vertices = mesh.vertices;
        bool validIndices = indices.Length > 0 && indices.Length % 3 == 0;
        foreach (int index in indices) validIndices &= index >= 0 && index < vertices.Length;
        bool finiteVertices = vertices.Length > 0, finiteNormals = normals.Length == vertices.Length;
        foreach (var p in vertices) finiteVertices &= float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);
        foreach (var n in normals) finiteNormals &= float.IsFinite(n.x) && float.IsFinite(n.y) && float.IsFinite(n.z);
        Debug.Log("DIANDIAN_FLIGHT_CHECK mesh=" + name + " vertices=" + vertices.Length + " triangles=" + indices.Length / 3
            + " indicesValid=" + validIndices + " verticesFinite=" + finiteVertices + " normalsFinite=" + finiteNormals);
        if (!validIndices || !finiteVertices || !finiteNormals) throw new InvalidOperationException(name + " 网格数据不完整或包含非法值。");
    }

    static void CheckRestoration(DiandianFlight flight, List<Snapshot> snapshots)
    {
        Vector3[] vertices = flight.restingMesh.vertices;
        int[] indices = flight.restingMesh.triangles;
        flight.ApplyPose(1, Mathf.PI / 2); flight.ApplyPose(0, 0);
        bool originalReference = flight.bodyRenderer.sharedMesh == flight.restingMesh;
        Material[] materials = flight.bodyRenderer.sharedMaterials;
        bool originalMaterials = materials.Length == flight.restingMaterials.Length;
        if (originalMaterials)
            for (int i = 0; i < materials.Length; i++) originalMaterials &= materials[i] == flight.restingMaterials[i];
        int enabledWings = 0, enabledSkins = 0;
        foreach (var wing in flight.wings) if (wing.renderer.enabled) enabledWings++;
        foreach (var snapshot in snapshots) if (snapshot.source.enabled && snapshot.source.gameObject.activeInHierarchy) enabledSkins++;
        Vector3[] restored = flight.restingMesh.vertices;
        int[] restoredIndices = flight.restingMesh.triangles;
        bool unchanged = vertices.Length == restored.Length && indices.Length == restoredIndices.Length;
        if (unchanged)
        {
            for (int i = 0; i < vertices.Length; i++) unchanged &= vertices[i].Equals(restored[i]);
            for (int i = 0; i < indices.Length; i++) unchanged &= indices[i] == restoredIndices[i];
        }
        bool singleOriginal = originalReference && originalMaterials && unchanged && enabledWings == 0 && enabledSkins == 1 && flight.bodyRenderer.enabled;
        Debug.Log("DIANDIAN_FLIGHT_CHECK amount0 originalMeshReference=" + originalReference + " originalGeometryUnchanged=" + unchanged
            + " originalMaterials=" + originalMaterials + " enabledWings=" + enabledWings + " enabledSkins=" + enabledSkins
            + " singleOriginalMeshRestored=" + singleOriginal);
        if (!singleOriginal) throw new InvalidOperationException("收翼后未完整恢复原始单网格模型。");
    }

    static void ReportPreservation(Mesh original, Mesh flight)
    {
        Vector3[] before = original.vertices, after = flight.vertices;
        Vector3[] normals = original.normals;
        Vector2[] beforeUV = original.uv, afterUV = flight.uv;
        BoneWeight[] weights = original.boneWeights;
        if (beforeUV.Length != before.Length || afterUV.Length != after.Length
            || normals.Length != before.Length || weights.Length != before.Length)
            throw new InvalidOperationException("原羽保护检查需要完整的原始法线、骨权重与前后 UV。");
        int[] oldTriangles = original.triangles, retainedTriangles = flight.GetTriangles(0);
        var referencedBefore = new HashSet<int>(oldTriangles);
        var referencedAfter = new HashSet<int>(retainedTriangles);
        var transferredVertices = new HashSet<int>();
        var transferredTriangles = new HashSet<int>();
        for (int i = 0; i < oldTriangles.Length; i += 3)
        {
            int a = oldTriangles[i], b = oldTriangles[i + 1], c = oldTriangles[i + 2];
            if (!DiandianFlightBody.IsShoulderTriangle(before[a], before[b], before[c])) continue;
            transferredTriangles.Add(i);
            transferredVertices.Add(a); transferredVertices.Add(b); transferredVertices.Add(c);
        }
        Debug.Log("DIANDIAN_FLIGHT_CHECK transferredShoulder sourceVertices=" + transferredVertices.Count
            + " sourceTriangles=" + transferredTriangles.Count + " checkedSeparately=true");
        var exactTriangles = new HashSet<(int, int, int)>();
        for (int i = 0; i < retainedTriangles.Length; i += 3)
            exactTriangles.Add((retainedTriangles[i], retainedTriangles[i + 1], retainedTriangles[i + 2]));
        float HeadWeight(int index)
        {
            BoneWeight w = weights[index]; float total = 0;
            void Add(int bone, float amount) { if (bone == 2 || bone == 3 || bone == 25) total += amount; }
            Add(w.boneIndex0, w.weight0); Add(w.boneIndex1, w.weight1);
            Add(w.boneIndex2, w.weight2); Add(w.boneIndex3, w.weight3);
            return total;
        }
        // Match FlightBody.PreserveOriginalPlumage's protected surfaces. Breast
        // and back are bounded below the neck so head vertices cannot mask loss.
        string[] names = { "head(y>.72)", "feet(y<.41,z>0)", "tail(y<.34,abs(x)<.04)",
            "original-breast", "original-back", "original-neck-shoulder" };
        Func<int, bool>[] regions = {
            i => before[i].y > .72f,
            i => before[i].y < .41f && before[i].z > 0,
            i => before[i].y < .34f && Mathf.Abs(before[i].x) < .04f,
            i => before[i].y < .655f && ((before[i].y >= .42f && before[i].z >= .60f * before[i].y - .145f)
                || (DiandianFlightBody.IsProtectedNormalPlumage(before[i], normals[i]) && normals[i].z > 0)),
            i => before[i].y < .655f && ((before[i].y >= .50f && before[i].z <= before[i].y - .59f && Mathf.Abs(before[i].x) <= .07f)
                || (DiandianFlightBody.IsProtectedNormalPlumage(before[i], normals[i]) && normals[i].z < 0)),
            i => before[i].y >= .655f || HeadWeight(i) >= .035f || (before[i].y >= .635f && before[i].z >= .065f)
        };
        var failures = new List<string>();
        for (int r = 0; r < regions.Length; r++)
        {
            int totalVertices = 0, keptReferences = 0, keptVertices = 0, keptUV = 0, totalTriangles = 0, keptTriangles = 0;
            foreach (int index in referencedBefore)
                if (regions[r](index) && !transferredVertices.Contains(index))
                {
                    totalVertices++;
                    if (index < after.Length && referencedAfter.Contains(index))
                    {
                        keptReferences++;
                        if (before[index].Equals(after[index])) keptVertices++;
                        if (beforeUV[index].Equals(afterUV[index])) keptUV++;
                    }
                }
            for (int i = 0; i < oldTriangles.Length; i += 3)
            {
                int a = oldTriangles[i], b = oldTriangles[i + 1], c = oldTriangles[i + 2];
                if (transferredTriangles.Contains(i)) continue;
                if (!regions[r](a) || !regions[r](b) || !regions[r](c)) continue;
                totalTriangles++;
                if (exactTriangles.Contains((a, b, c))) keptTriangles++;
            }
            Debug.Log("DIANDIAN_FLIGHT_CHECK preservedRegion=" + names[r] + " referencedVertices=" + keptReferences + "/" + totalVertices
                + " referencedUnchangedVertices=" + keptVertices + "/" + totalVertices + " referencedOriginalUV=" + keptUV + "/" + totalVertices
                + " exactOriginalTriangles=" + keptTriangles + "/" + totalTriangles);
            if (r >= 3 && keptReferences != totalVertices)
                foreach (int index in referencedBefore)
                    if (regions[r](index) && !transferredVertices.Contains(index) && !referencedAfter.Contains(index))
                    {
                        float maxArea = 0;
                        for (int t = 0; t < oldTriangles.Length; t += 3)
                        {
                            int a = oldTriangles[t], b = oldTriangles[t + 1], c = oldTriangles[t + 2];
                            if (a == index || b == index || c == index)
                                maxArea = Mathf.Max(maxArea, Vector3.Cross(before[b] - before[a], before[c] - before[a]).sqrMagnitude);
                        }
                        Debug.Log("DIANDIAN_PLUMAGE_MISSING region=" + names[r] + " vertex=" + index
                            + " position=" + before[index].ToString("F6") + " maxAreaSquared=" + maxArea.ToString("E6"));
                    }
            if (r >= 3 && (totalVertices == 0 || totalTriangles == 0 || keptReferences != totalVertices
                || keptVertices != totalVertices || keptUV != totalVertices || keptTriangles != totalTriangles))
                failures.Add(names[r]);
        }
        if (failures.Count > 0)
            throw new InvalidOperationException(string.Join(", ", failures) + " 原羽的实际三角引用、位置或 UV 未完整保留。");
    }
}
