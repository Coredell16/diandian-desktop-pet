using System;
using System.Collections.Generic;
using UnityEngine;

// Landmarks are in the supplied GLB's native units after glTFast's X reflection.
// Correct the generated three-forward foot before binding; preserve UVs and claws.
public class DiandianFootRig
{
    public Transform[] legs=new Transform[2],feet=new Transform[2];
    public Transform[] toeRoots=new Transform[8],toeTips=new Transform[8];
    public Vector3[] toeAxes=new Vector3[8];
    BoneWeight[] footWeights;
    float[] influence;
    int boneCount;

    static float Fade(float a,float b,float v)=>Mathf.SmoothStep(0,1,Mathf.InverseLerp(a,b,v));
    static float Distance(Vector3 p,Vector3 a,Vector3 b)
    {
        Vector3 ab=b-a;return Vector3.Distance(p,a+ab*Mathf.Clamp01(Vector3.Dot(p-a,ab)/ab.sqrMagnitude));
    }
    static float ToeDistance(Vector3 p,Vector3 root,Vector3 nail,Vector3 tip)
        =>Mathf.Min(Distance(p,root,nail),Distance(p,nail,tip));

    public static DiandianFootRig Build(Transform space,Transform pelvis,Mesh mesh,List<Transform> bones)
    {
        if(mesh.bounds.size.y<.8f||mesh.bounds.size.y>1.1f)
            throw new InvalidOperationException("脚部坐标要求未缩放的原始 Tripo 网格。");
        var rig=new DiandianFootRig();
        Vector3[] hips={new Vector3(-.066f,.445f,.095f),new Vector3(.070f,.445f,.105f)};
        Vector3[] ankles={new Vector3(-.082f,.400f,.084f),new Vector3(.086f,.400f,.105f)};
        // Per foot: I rear, II inner front, III central front, IV outer front to correct.
        Vector3[] roots={
            new Vector3(-.108f,.378f,.096f),new Vector3(-.095f,.378f,.113f),
            new Vector3(-.110f,.382f,.119f),new Vector3(-.119f,.376f,.115f),
            new Vector3(.108f,.384f,.113f),new Vector3(.094f,.383f,.135f),
            new Vector3(.111f,.388f,.142f),new Vector3(.122f,.380f,.135f)};
        Vector3[] nails={
            new Vector3(-.11340f,.36472f,.05150f),new Vector3(-.05660f,.36345f,.14654f),
            new Vector3(-.11183f,.37810f,.15743f),new Vector3(-.15045f,.36306f,.14904f),
            new Vector3(.10897f,.37354f,.07737f),new Vector3(.06230f,.36942f,.16402f),
            new Vector3(.11170f,.38344f,.17997f),new Vector3(.15128f,.37090f,.16258f)};
        Vector3[] tips={
            new Vector3(-.11345f,.33754f,.04780f),new Vector3(-.05517f,.33347f,.15146f),
            new Vector3(-.11648f,.34840f,.16971f),new Vector3(-.15784f,.33053f,.15373f),
            new Vector3(.11649f,.34614f,.06162f),new Vector3(.05629f,.33780f,.17001f),
            new Vector3(.11388f,.34726f,.19759f),new Vector3(.15791f,.33876f,.16788f)};
        Vector3[] originalNails=(Vector3[])nails.Clone(),originalTips=(Vector3[])tips.Clone();
        Quaternion[] correction={Quaternion.AngleAxis(-95,Vector3.up),Quaternion.AngleAxis(95,Vector3.up)};
        for(int side=0;side<2;side++)
        {
            int i=side*4+3;
            nails[i]=roots[i]+correction[side]*(nails[i]-roots[i]);
            tips[i]=roots[i]+correction[side]*(tips[i]-roots[i]);
        }
        int initialBones=bones.Count;int[] legIndex=new int[2],footIndex=new int[2],rootIndex=new int[8],tipIndex=new int[8];
        Transform AddBone(string name,Transform parent,Vector3 p,out int index)
        {
            var t=new GameObject(name).transform;t.SetParent(parent,false);
            t.position=space.TransformPoint(p);t.rotation=space.rotation;
            index=bones.Count;bones.Add(t);return t;
        }
        for(int side=0;side<2;side++)
        {
            string prefix=side==0?"负X足":"正X足";
            rig.legs[side]=AddBone(prefix+"_腿",pelvis,hips[side],out legIndex[side]);
            rig.feet[side]=AddBone(prefix+"_踝",rig.legs[side],ankles[side],out footIndex[side]);
            int front=0,back=0;
            for(int digit=0;digit<4;digit++)
            {
                int i=side*4+digit;Vector3 direction=nails[i]-roots[i];
                if(direction.z>0)front++;else back++;
                rig.toeRoots[i]=AddBone(prefix+"_趾"+(digit+1)+"_根",rig.feet[side],roots[i],out rootIndex[i]);
                rig.toeTips[i]=AddBone(prefix+"_趾"+(digit+1)+"_末节",rig.toeRoots[i],Vector3.Lerp(roots[i],nails[i],.56f),out tipIndex[i]);
                Vector3 axis=Vector3.Cross(Vector3.up,direction).normalized;
                rig.toeAxes[i]=rig.toeRoots[i].InverseTransformDirection(space.TransformDirection(axis)).normalized;
                if(Mathf.Abs(direction.magnitude-(originalNails[i]-roots[i]).magnitude)>1e-6f)
                    throw new InvalidOperationException("脚趾矫正改变了趾长。");
            }
            if(front!=2||back!=2)throw new InvalidOperationException("必须是第二三趾向前、第一四趾向后。");
        }
        if(bones.Count-initialBones!=20)throw new InvalidOperationException("脚部骨架应为20根骨骼。");
        rig.boneCount=bones.Count;
        var vertices=mesh.vertices;var normals=mesh.normals;
        rig.footWeights=new BoneWeight[vertices.Length];rig.influence=new float[vertices.Length];
        int[] movedCount=new int[2];float maxRadiusError=0;
        for(int v=0;v<vertices.Length;v++)
        {
            Vector3 p=vertices[v];
            if(p.y>.465f||p.y<.29f||p.z<.025f||Mathf.Abs(p.x)<.035f)continue;
            int side=p.x<0?0:1,start=side*4;
            float legDistance=Distance(p,hips[side],ankles[side]);
            int nearest=-1;float nearestDistance=float.MaxValue,secondDistance=float.MaxValue;
            for(int i=start;i<start+4;i++)
            {
                float dist=ToeDistance(p,roots[i],originalNails[i],originalTips[i]);
                if(dist<nearestDistance){secondDistance=nearestDistance;nearestDistance=dist;nearest=i;}
                else secondDistance=Mathf.Min(secondDistance,dist);
            }
            float palmDistance=Distance(p,ankles[side],Vector3.Lerp(roots[start],roots[start+2],.5f));
            float regionDistance=Mathf.Min(legDistance,Mathf.Min(palmDistance,nearestDistance));
            float coverage=(1-Fade(.022f,.033f,regionDistance))*(1-Fade(.435f,.465f,p.y));
            if(coverage<=0)continue;
            rig.influence[v]=coverage;
            float footAmount=1-Fade(.393f,.427f,p.y);
            Vector3 along=originalNails[nearest]-roots[nearest];float length=along.magnitude;
            float projection=Vector3.Dot(p-roots[nearest],along/length);
            float toeAmount=Fade(.002f,.018f,projection)*(1-Fade(.014f,.023f,nearestDistance))*footAmount;
            // Near the shared palm, a soft ownership boundary avoids pulling a
            // neighboring digit along with the fourth toe.
            float ownership=Fade(0,.006f,secondDistance-nearestDistance);
            toeAmount*=ownership;
            float distal=Fade(.34f,.76f,projection/length);
            rig.footWeights[v]=new BoneWeight{
                boneIndex0=legIndex[side],weight0=1-footAmount,
                boneIndex1=footIndex[side],weight1=footAmount-toeAmount,
                boneIndex2=rootIndex[nearest],weight2=toeAmount*(1-distal),
                boneIndex3=tipIndex[nearest],weight3=toeAmount*distal};
            if(nearest%4==3)
            {
                // Only the short attachment at the palm transitions. Keeping a
                // long forward-pointing proximal section would make an S-shaped toe.
                float turn=Fade(0,.007f,projection)*(1-Fade(.014f,.023f,nearestDistance))*ownership;
                if(turn>0)
                {
                    Quaternion q=Quaternion.Slerp(Quaternion.identity,correction[side],turn);
                    Vector3 relative=p-roots[nearest];vertices[v]=roots[nearest]+q*relative;
                    if(normals.Length==vertices.Length)normals[v]=(q*normals[v]).normalized;
                    maxRadiusError=Mathf.Max(maxRadiusError,Mathf.Abs(relative.magnitude-(vertices[v]-roots[nearest]).magnitude));
                    if(turn>.99f)movedCount[side]++;
                }
            }
        }
        if(movedCount[0]<20||movedCount[1]<20||maxRadiusError>1e-6f)
            throw new InvalidOperationException("第四趾分区或等长旋转验证失败。");
        mesh.vertices=vertices;if(normals.Length==vertices.Length)mesh.normals=normals;
        // Rotation changes the tangent frame as well. The existing UV seams stay intact.
        mesh.RecalculateTangents();mesh.RecalculateBounds();
        Debug.Log("DIANDIAN_FEET_OK addedBones=20 fourthToeVertices="+movedCount[0]+","+movedCount[1]+" radiusError="+maxRadiusError);
        return rig;
    }

    public void ApplyWeights(BoneWeight[] weights)
    {
        if(weights.Length<footWeights.Length)throw new ArgumentException("足部权重的原始顶点已被删除或重排。");
        var combined=new float[boneCount];
        void Add(BoneWeight w,float amount)
        {
            combined[w.boneIndex0]+=w.weight0*amount;combined[w.boneIndex1]+=w.weight1*amount;
            combined[w.boneIndex2]+=w.weight2*amount;combined[w.boneIndex3]+=w.weight3*amount;
        }
        // A later jaw split may append vertices; their weights belong to that rig.
        for(int i=0;i<footWeights.Length;i++)
        {
            float amount=influence[i];if(amount<=0)continue;
            Array.Clear(combined,0,combined.Length);Add(weights[i],1-amount);Add(footWeights[i],amount);
            var w=new BoneWeight();float sum=0;
            for(int slot=0;slot<4;slot++)
            {
                int index=0;for(int b=1;b<combined.Length;b++)if(combined[b]>combined[index])index=b;
                float value=combined[index];combined[index]=0;sum+=value;
                if(slot==0){w.boneIndex0=index;w.weight0=value;}
                else if(slot==1){w.boneIndex1=index;w.weight1=value;}
                else if(slot==2){w.boneIndex2=index;w.weight2=value;}
                else {w.boneIndex3=index;w.weight3=value;}
            }
            if(sum<=0||!float.IsFinite(sum))throw new InvalidOperationException("足部权重无效。");
            w.weight0/=sum;w.weight1/=sum;w.weight2/=sum;w.weight3/=sum;
            if(w.weight0<w.weight1||w.weight1<w.weight2||w.weight2<w.weight3||Mathf.Abs(w.weight0+w.weight1+w.weight2+w.weight3-1)>1e-5f)
                throw new InvalidOperationException("足部权重必须按降序归一化。");
            weights[i]=w;
        }
    }
}
