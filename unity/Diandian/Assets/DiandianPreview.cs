using UnityEngine;

public class DiandianPreview : MonoBehaviour
{
    public Camera viewCamera;
    public Transform modelRoot, head, crest, leftWingRoot, rightWingRoot, perchRoot;
    public SkinnedMeshRenderer[] wings;
    public Transform[] toeJoints, legRoots;
    [Range(0f, 1f)] public float wingSpread;
    [Range(0f, 1f)] public float toeCurl;
    [Range(0f, 1f)] public float crestRaise = 0.72f;
    [Range(-180f, 180f)] public float yaw;
    public bool animateWings, autoRotate;

    [SerializeField] private bool poseInitialized;
    [SerializeField] private Quaternion headBase, crestBase, modelBase;
    [SerializeField] private bool perchInitialized;
    [SerializeField] private Quaternion perchBase = Quaternion.identity;
    [SerializeField] private Quaternion[] toeBases;
    [SerializeField] private Vector3 modelPosition;
    [SerializeField] private int preset;
    public Vector3 viewFocus = new Vector3(0f, 1.22f, -0.35f);
    public bool importedStaticModel;
    public DiandianRig rig;
    public AudioSource voice;
    public AudioClip[] calls;
    public string[] callNames;
    private int selectedCall;
    private readonly float[] audioSamples=new float[256];
    private readonly Rect panel = new Rect(16f, 16f, 316f, 584f);
    private float distance = 4.7f, pitch = 11f;
    private bool dragging;
    private Vector3 lastMouse;
    private Font chineseFont;
    private GUIStyle titleStyle, noteStyle;

    private void Start()
    {
        if (!viewCamera) viewCamera = Camera.main;
        ResetView();
        ApplyPose(0f);
    }

    private void Update()
    {
        float scale = UIScale();
        Vector3 mouse = Input.mousePosition;
        bool overPanel = panel.Contains(new Vector2(mouse.x, Screen.height - mouse.y) / scale);
        if (Input.GetMouseButtonDown(0))
        {
            dragging = !overPanel;
            lastMouse = mouse;
        }
        if (!Input.GetMouseButton(0)) dragging = false;
        if (dragging)
        {
            Vector3 delta = mouse - lastMouse;
            yaw = Mathf.Repeat(yaw - delta.x * 0.35f + 180f, 360f) - 180f;
            pitch = Mathf.Clamp(pitch - delta.y * 0.2f, -12f, 55f);
        }
        lastMouse = mouse;
        if (!overPanel)
            distance = Mathf.Clamp(distance - Input.mouseScrollDelta.y * 0.3f, 3.0f, 7.5f);
        if (autoRotate) yaw = Mathf.Repeat(yaw + Time.deltaTime * 16f + 180f, 360f) - 180f;
        UpdateCamera();
        ApplyPose(Time.time);
        if(rig&&voice)
        {
            float level=0;
            if(voice.isPlaying){voice.GetOutputData(audioSamples,0);foreach(float sample in audioSamples)level+=sample*sample;}
            float target=Mathf.Clamp01(Mathf.Sqrt(level/audioSamples.Length)*18);
            rig.mouthOpen=Mathf.MoveTowards(rig.mouthOpen,target,Time.deltaTime*10);
        }
    }

    private void CaptureBasePose()
    {
        if (!perchInitialized && perchRoot)
        {
            perchBase = perchRoot.localRotation;
            perchInitialized = true;
        }
        if (poseInitialized) return;
        if (modelRoot)
        {
            modelPosition = modelRoot.localPosition;
            modelBase = modelRoot.localRotation;
        }
        if (head) headBase = head.localRotation;
        if (crest) crestBase = crest.localRotation;
        toeBases = new Quaternion[toeJoints == null ? 0 : toeJoints.Length];
        for (int i = 0; i < toeBases.Length; i++)
            toeBases[i] = toeJoints[i] ? toeJoints[i].localRotation : Quaternion.identity;
        poseInitialized = true;
    }

    // Deterministic pose evaluation also works in the Editor without entering Play mode.
    public void ApplyPose(float time)
    {
        CaptureBasePose();
        if(!importedStaticModel)
        {
        float spread = Mathf.Clamp01(wingSpread);
        if (wings != null)
            foreach (SkinnedMeshRenderer wing in wings)
                if (wing && wing.sharedMesh && wing.sharedMesh.blendShapeCount > 0)
                    wing.SetBlendShapeWeight(0, spread * 100f);

        float flap = animateWings ? Mathf.Sin(time * 7f) * 48f * spread : 0f;
        if (leftWingRoot) leftWingRoot.localRotation = Quaternion.Euler(0f, 0f, -flap);
        if (rightWingRoot) rightWingRoot.localRotation = Quaternion.Euler(0f, 0f, flap);
        if (toeJoints != null && toeBases != null)
            for (int i = 0; i < Mathf.Min(toeJoints.Length, toeBases.Length); i++)
                if (toeJoints[i])
                    toeJoints[i].localRotation = toeBases[i] * Quaternion.Euler(22f * Mathf.Clamp01(toeCurl), 0f, 0f);
        if (legRoots != null)
            foreach (Transform leg in legRoots)
                if (leg) leg.localRotation = Quaternion.Euler(preset == 2 ? 60f : 0f, 0f, 0f);

        if (head)
            head.localRotation = headBase * Quaternion.Euler(Mathf.Sin(time * 0.7f) * 2f,
                Mathf.Sin(time * 0.53f) * 7f, Mathf.Sin(time * 0.81f) * 1.5f);
        if (crest) crest.localRotation = crestBase * Quaternion.Euler(-48f * (1f - Mathf.Clamp01(crestRaise)), 0f, 0f);
        }
        if (modelRoot)
        {
            modelRoot.localRotation = modelBase * Quaternion.Euler(0f, yaw, 0f);
            modelRoot.localPosition = modelPosition + Vector3.up * (preset == 2 ? 0.18f + Mathf.Sin(time * 2f) * 0.025f : 0f);
        }
        if (perchRoot)
        {
            perchRoot.localRotation = perchBase * Quaternion.Euler(0f, yaw, 0f);
            perchRoot.gameObject.SetActive(preset != 2);
        }
    }

    public void SetPreset(int index)
    {
        preset = Mathf.Clamp(index, 0, 2);
        wingSpread = preset == 0 ? 0f : 1f;
        toeCurl = preset == 2 ? 1f : 0f;
        crestRaise = preset == 0 ? 0.72f : 0.92f;
        animateWings = preset != 0;
        ApplyPose(0f);
    }

    public void ResetView()
    {
        yaw = 0f;
        distance = 4.7f;
        pitch = 11f;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        if (!viewCamera) return;
        Vector3 direction = Quaternion.Euler(-pitch, 60f, 0f) * Vector3.forward;
        viewCamera.transform.position = viewFocus + direction * distance;
        viewCamera.transform.LookAt(viewFocus);
    }

    private float UIScale()
    {
        return Mathf.Clamp(Screen.height / 760f, 0.65f, 1f);
    }

    private float Slider(string label, float value, float min, float max, bool degrees = false)
    {
        GUILayout.Label(label + "  " + (degrees ? value.ToString("0") + "°" : (value * 100f).ToString("0") + "%"), GUILayout.Height(20f));
        return GUILayout.HorizontalSlider(value, min, max, GUILayout.Height(18f));
    }

    private void OnGUI()
    {
        if (!chineseFont)
            chineseFont = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "Microsoft YaHei UI", "SimHei" }, 16);
        Font previousFont = GUI.skin.font;
        Matrix4x4 previousMatrix = GUI.matrix;
        GUI.skin.font = chineseFont;
        GUI.matrix = Matrix4x4.Scale(Vector3.one * UIScale());
        if (titleStyle == null)
        {
            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 23, fontStyle = FontStyle.Bold };
            noteStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
        }
        GUI.Box(panel, GUIContent.none);
        GUILayout.BeginArea(new Rect(panel.x + 14f, panel.y + 12f, panel.width - 28f, panel.height - 24f));
        GUILayout.Label("点点 · 3D 建模预览", titleStyle, GUILayout.Height(32f));
        GUILayout.Label(importedStaticModel ? (rig ? "Tripo 形象 · 骨骼动作预览\n二前二后脚趾 · 展翅尚未完成" : "Tripo 原始模型与贴图。\n当前为静态模型，尚未绑定动作骨骼。") : "玄凤鹦鹉形态与动作预览。\n比例、羽毛层次和细节仍可继续调整。", noteStyle, GUILayout.Height(42f));
        if(!importedStaticModel)
        {
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("站立", GUILayout.Height(28f))) SetPreset(0);
        if (GUILayout.Button("抓杆扑翅", GUILayout.Height(28f))) SetPreset(1);
        if (GUILayout.Button("腾空收爪", GUILayout.Height(28f))) SetPreset(2);
        GUILayout.EndHorizontal();
        GUILayout.Space(8f);
        yaw = Slider("旋转", yaw, -180f, 180f, true);
        wingSpread = Slider("展翅", wingSpread, 0f, 1f);
        toeCurl = Slider("脚趾收拢", toeCurl, 0f, 1f);
        crestRaise = Slider("冠羽竖起", crestRaise, 0f, 1f);
        animateWings = GUILayout.Toggle(animateWings, "连续扑翅", GUILayout.Height(25f));
        }
        else
        {
            GUILayout.Space(8f);
            if(rig)
            {
                GUILayout.BeginHorizontal();
                if(GUILayout.Button("待机",GUILayout.Height(30)))rig.SetMotion(0);
                if(GUILayout.Button("左右看",GUILayout.Height(30)))rig.SetMotion(1);
                if(GUILayout.Button("点点头",GUILayout.Height(30)))rig.SetMotion(2);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if(GUILayout.Button("摆尾",GUILayout.Height(30)))rig.SetMotion(3);
                if(GUILayout.Button("冠羽轻摆",GUILayout.Height(30)))rig.SetMotion(4);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if(rig.legs.Length>0&&GUILayout.Button("收爪",GUILayout.Height(30)))rig.SetMotion(5);
                if(rig.legs.Length>0&&GUILayout.Button("原地迈步",GUILayout.Height(30)))rig.SetMotion(6);
                GUILayout.EndHorizontal();
                rig.amplitude=Slider("动作幅度",rig.amplitude,.25f,1.25f);
                rig.speed=Slider("动作速度",rig.speed,.5f,1.5f);
                if(calls!=null&&calls.Length>0&&voice)
                {
                    selectedCall=GUILayout.SelectionGrid(selectedCall,callNames,3);
                    GUILayout.BeginHorizontal();
                    if(GUILayout.Button("叫一声",GUILayout.Height(28)))PlaySelectedCall();
                    if(GUILayout.Button("停止叫声",GUILayout.Height(28))){voice.Stop();rig.mouthOpen=0;}
                    GUILayout.EndHorizontal();
                }
                GUILayout.Space(8f);
            }
            yaw = Slider("旋转", yaw, -180f, 180f, true);
        }
        autoRotate = GUILayout.Toggle(autoRotate, "自动环视", GUILayout.Height(25f));
        if (GUILayout.Button("重置视角", GUILayout.Height(28f))) ResetView();
        GUILayout.Space(7f);
        GUILayout.Label("在面板外按住左键拖动：旋转 / 俯仰\n鼠标滚轮：缩放", noteStyle, GUILayout.Height(40f));
        GUILayout.EndArea();
        GUI.skin.font = previousFont;
        GUI.matrix = previousMatrix;
    }

    private void OnDestroy()
    {
        if (chineseFont) Destroy(chineseFont);
    }

    public void PlaySelectedCall()
    {
        if(!voice||calls==null||calls.Length==0)return;
        selectedCall=Mathf.Clamp(selectedCall,0,calls.Length-1);
        voice.clip=calls[selectedCall];voice.Play();if(rig)rig.SetMotion(7);
    }
}
