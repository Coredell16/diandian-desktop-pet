using System;
using UnityEngine;

// Independent wing bones have one writer here; the existing rig owns the head,
// body, tail and feet. All flight surfaces are volumetric meshes, not sprites.
public sealed class DiandianFlight : MonoBehaviour
{
    [Serializable] public sealed class Wing
    {
        public Transform shoulder,elbow,wrist;
        public SkinnedMeshRenderer renderer;
        public int side;
        public Quaternion closedShoulder=Quaternion.identity,closedElbow=Quaternion.identity,closedWrist=Quaternion.identity;
    }
    public SkinnedMeshRenderer bodyRenderer;
    public Mesh restingMesh,flightMesh;
    public Material[] restingMaterials,flightMaterials;
    public Wing[] wings=Array.Empty<Wing>();
    public Camera viewCamera;
    public Vector3 cameraFocus;
    public Vector3 restingCameraPosition;
    [Range(1,10)] public float beatsPerSecond=7;
    [Range(.15f,.8f)] public float openSeconds=.40f,closeSeconds=.46f;
    [Range(1,1.7f)] public float flightCameraDistance=1.35f;
    [Range(0,60)] public float featherBackPitch=38;
    [Range(0,45)] public float recoveryElbowSweep=18,recoveryWristSweep=28;
    public float Spread { get; private set; }
    public bool IsActive=>Spread>0||flying||minimumFlightRemaining>0;
    bool flying;
    float progress,phase,minimumFlightRemaining;

    // Bones and feather adduction share this timing. Fixed bone lengths keep
    // the elbow and wrist attached throughout opening and its exact reverse.
    public static float SegmentOpen(float spread,int bone)
    {
        float start=bone==0?0:bone==1?.12f:.30f;
        float end=bone==0?.65f:bone==1?.85f:1;
        return Mathf.SmoothStep(0,1,Mathf.InverseLerp(start,end,spread));
    }

    public void SetFlying(bool value)
    {
        if(value&&!flying)
        {
            if(progress<=0)phase=Mathf.PI*.5f;
            // Even a quick mouse flick opens fully and completes one wingbeat.
            minimumFlightRemaining=openSeconds+1/Mathf.Max(1,beatsPerSecond);
        }
        flying=value;
    }

    void LateUpdate()
    {
        float dt=Mathf.Min(Time.unscaledDeltaTime,.05f);
        bool airborne=flying||minimumFlightRemaining>0;
        minimumFlightRemaining=Mathf.Max(0,minimumFlightRemaining-dt);
        progress=Mathf.MoveTowards(progress,airborne?1:0,dt/Mathf.Max(.1f,airborne?openSeconds:closeSeconds));
        // Open towards the raised position before beginning the first downstroke;
        // otherwise an arbitrary beat phase can yank a still-folded wrist around.
        if(progress>=1)phase+=dt*beatsPerSecond*Mathf.PI*2;
        ApplyPose(Mathf.SmoothStep(0,1,progress),phase);
    }

    // Editor checks use the same deterministic posing function as the Player.
    public void ApplyPose(float amount,float phase)
    {
        Spread=Mathf.Clamp01(amount);
        bool active=Spread>0;
        if(bodyRenderer)
        {
            Mesh target=active?flightMesh:restingMesh;
            if(target&&bodyRenderer.sharedMesh!=target)
            {
                bodyRenderer.sharedMesh=target;
                bodyRenderer.sharedMaterials=active?flightMaterials:restingMaterials;
            }
        }
        float rawStroke=15+50*Mathf.Sin(phase);
        float lowerStroke=Mathf.SmoothStep(0,1,Mathf.InverseLerp(-5,-35,rawStroke));
        float pitch=Mathf.Lerp(featherBackPitch,Mathf.Min(featherBackPitch,18),lowerStroke);
        // sin gives height; cos gives direction. The recovery stroke sweeps the
        // forearm and hand back, while the power stroke presents the open wing.
        float recovery=Mathf.SmoothStep(0,1,Mathf.Max(0,Mathf.Cos(phase)));
        float handRecovery=Mathf.SmoothStep(0,1,Mathf.Max(0,Mathf.Cos(phase-.2f)));
        foreach(Wing wing in wings)
        {
            if(wing.renderer)
            {
                wing.renderer.enabled=active;
                wing.renderer.SetBlendShapeWeight(0,(1-Spread)*100);
            }
            // +Z points toward the beak. Opposite signs lift both shoulders.
            float stroke=rawStroke;
            // The perched body is upright: trailing feathers slope down and back,
            // rather than presenting a paper-thin horizontal wing to the viewer.
            // Root pitch and delayed hand rotation give different wing shapes
            // at the same height on the upward and downward parts of the beat.
            Quaternion openShoulder=Quaternion.AngleAxis(wing.side*stroke,Vector3.forward)*Quaternion.AngleAxis(-pitch,Vector3.right)*Quaternion.AngleAxis(-wing.side*7,Vector3.up);
            Quaternion openElbow=Quaternion.AngleAxis(wing.side*recoveryElbowSweep*recovery,Vector3.up)
                *Quaternion.AngleAxis(-wing.side*Mathf.Sin(phase)*8,Vector3.forward);
            Quaternion openWrist=Quaternion.AngleAxis(wing.side*recoveryWristSweep*handRecovery,Vector3.up)
                *Quaternion.AngleAxis(-12*handRecovery,Vector3.right)
                *Quaternion.AngleAxis(wing.side*(Mathf.Sin(phase-.28f)*12-stroke*.12f),Vector3.forward);
            // Raise the folded humerus while the forearm and hand open in order.
            // Interpolating through a horizontal neutral pose caused a flat
            // sideways bar, followed by a separate last-minute upward movement.
            if(wing.shoulder)wing.shoulder.localRotation=Quaternion.Slerp(wing.closedShoulder,openShoulder,SegmentOpen(Spread,0));
            if(wing.elbow)wing.elbow.localRotation=Quaternion.Slerp(wing.closedElbow,openElbow,SegmentOpen(Spread,1));
            if(wing.wrist)wing.wrist.localRotation=Quaternion.Slerp(wing.closedWrist,openWrist,SegmentOpen(Spread,2));
        }
        if(viewCamera)
        {
            Vector3 back=restingCameraPosition-cameraFocus;
            float framing=Mathf.SmoothStep(0,1,Mathf.Clamp01(Spread/.60f));
            viewCamera.transform.position=cameraFocus+Vector3.up*(Spread*.12f)+back*Mathf.Lerp(1,flightCameraDistance,framing);
        }
    }

    void OnDisable()
    {
        flying=false;progress=0;minimumFlightRemaining=0;
        ApplyPose(0,0);
    }
}
