using System;
using System.Collections.Generic;
using UnityEngine;

public class DiandianRig : MonoBehaviour
{
    public enum Motion { Idle, LookAround, Nod, TailWag, CrestSway, CurlFeet, Step, Call }
    public Transform pelvis, body, head, crest, tail, jaw;
    public Transform[] legs=Array.Empty<Transform>(), feet=Array.Empty<Transform>();
    public Transform[] toeRoots=Array.Empty<Transform>(), toeTips=Array.Empty<Transform>();
    public Vector3[] toeAxes=Array.Empty<Vector3>();
    public Vector3 jawAxis=Vector3.right;
    public Motion motion;
    [Range(.25f,1.25f)] public float amplitude=1;
    [Range(.5f,1.5f)] public float speed=1;
    [Range(0,1)] public float mouthOpen;

    [SerializeField,HideInInspector] Transform[] bones;
    [SerializeField,HideInInspector] Quaternion[] restRot;
    [SerializeField,HideInInspector] Vector3[] restPos;
    Quaternion[] fromRot,targetRot;
    Vector3[] fromPos,targetPos;
    float transitionStart,motionStart;
    bool transitioning;

    // Binding calls this after all references are assigned in the neutral pose.
    public void InitializeRestPose()
    {
        var all=new List<Transform>();
        void Add(Transform t){if(t&&!all.Contains(t))all.Add(t);}
        Add(pelvis);Add(body);Add(head);Add(crest);Add(tail);Add(jaw);
        foreach(var group in new[]{legs,feet,toeRoots,toeTips})
            if(group!=null)foreach(var t in group)Add(t);
        bones=all.ToArray();restRot=new Quaternion[bones.Length];restPos=new Vector3[bones.Length];
        for(int i=0;i<bones.Length;i++){restRot[i]=bones[i].localRotation;restPos[i]=bones[i].localPosition;}
        InitializeBuffers();
    }
    void InitializeBuffers()
    {
        fromRot=(Quaternion[])restRot.Clone();targetRot=(Quaternion[])restRot.Clone();
        fromPos=(Vector3[])restPos.Clone();targetPos=(Vector3[])restPos.Clone();
    }
    void EnsureInitialized()
    {
        if(bones==null||bones.Length==0||restRot==null||restRot.Length!=bones.Length)InitializeRestPose();
        else if(fromRot==null||fromRot.Length!=bones.Length)InitializeBuffers();
    }
    public void ResetToRestPose()
    {
        EnsureInitialized();transitioning=false;mouthOpen=0;
        for(int i=0;i<bones.Length;i++)if(bones[i]){bones[i].localRotation=restRot[i];bones[i].localPosition=restPos[i];}
    }
    public void SetMotion(int index)
    {
        EnsureInitialized();
        motion=(Motion)Mathf.Clamp(index,0,(int)Motion.Call);
        for(int i=0;i<bones.Length;i++)if(bones[i]){fromRot[i]=bones[i].localRotation;fromPos[i]=bones[i].localPosition;}
        motionStart=transitionStart=Application.isPlaying?Time.time:0;
        transitioning=Application.isPlaying;
    }
    void Update()=>ApplyPose(Time.time);
    void Rotate(Transform bone,Quaternion offset)
    {
        int i=Array.IndexOf(bones,bone);if(bone&&i>=0)targetRot[i]=restRot[i]*offset;
    }
    void Move(Transform bone,Vector3 offset)
    {
        int i=Array.IndexOf(bones,bone);if(bone&&i>=0)targetPos[i]=restPos[i]+offset;
    }
    public void ApplyPose(float time)
    {
        EnsureInitialized();
        Array.Copy(restRot,targetRot,bones.Length);Array.Copy(restPos,targetPos,bones.Length);
        float t=Mathf.Max(0,time-motionStart)*speed;
        float bodyX=Mathf.Sin(t*1.8f)*.25f,headX=Mathf.Sin(t*.83f)*.55f;
        float headY=Mathf.Sin(t*.63f)*2,tailY=Mathf.Sin(t*.71f)*.65f,crestX=Mathf.Sin(t*1.13f)*.6f;
        float curl=0;
        switch(motion)
        {
            case Motion.LookAround:headY=Mathf.Sin(t*.95f)*10;break;
            case Motion.Nod:headX=Mathf.Sin(t*2.2f)*7;break;
            case Motion.TailWag:tailY=Mathf.Sin(t*2.1f)*7;break;
            case Motion.CrestSway:crestX=Mathf.Sin(t*1.75f)*5;break;
            case Motion.CurlFeet:curl=Mathf.SmoothStep(0,1,t/.8f);break;
            case Motion.Step:
                Move(pelvis,new Vector3(Mathf.Sin(t*3)*.002f,Mathf.Abs(Mathf.Sin(t*6))*.003f,0)*amplitude);
                break;
            case Motion.Call:headX=Mathf.Sin(t*4)*1.5f;crestX=Mathf.Sin(t*2)*1.2f;break;
        }
        Rotate(body,Quaternion.Euler(bodyX*amplitude,0,0));
        Rotate(head,Quaternion.Euler(headX*amplitude,headY*amplitude,0));
        Rotate(tail,Quaternion.Euler(0,tailY*amplitude,0));
        Rotate(crest,Quaternion.Euler(crestX*amplitude,0,0));
        for(int side=0;side<legs.Length;side++)
        {
            float step=motion==Motion.Step?Mathf.Sin(t*6)*(side==0?1:-1):0;
            Rotate(legs[side],Quaternion.Euler((curl*24+step*10)*amplitude,0,0));
            if(side<feet.Length)Rotate(feet[side],Quaternion.Euler((-curl*12-step*7)*amplitude,0,0));
            float toeCurl=(curl+Mathf.Max(0,step)*.18f)*Mathf.Min(1,amplitude);
            for(int digit=0;digit<4;digit++)
            {
                int i=side*4+digit;if(i>=toeRoots.Length||i>=toeAxes.Length)continue;
                Rotate(toeRoots[i],Quaternion.AngleAxis(toeCurl*32,toeAxes[i]));
                if(i<toeTips.Length)Rotate(toeTips[i],Quaternion.AngleAxis(toeCurl*38,toeAxes[i]));
            }
        }
        Rotate(jaw,Quaternion.AngleAxis(Mathf.Clamp01(mouthOpen)*6,jawAxis));
        float blend=transitioning?Mathf.SmoothStep(0,1,(time-transitionStart)/.35f):1;
        if(blend>=1)transitioning=false;
        for(int i=0;i<bones.Length;i++)if(bones[i])
        {
            bones[i].localRotation=Quaternion.Slerp(fromRot[i],targetRot[i],blend);
            bones[i].localPosition=Vector3.Lerp(fromPos[i],targetPos[i],blend);
        }
    }
}
