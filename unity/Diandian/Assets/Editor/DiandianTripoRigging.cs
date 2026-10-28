using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// A conservative first rig for the supplied closed-wing mesh. Full flight needs
// separately authored wing geometry; this rig deliberately keeps both wings folded.
public static class DiandianTripoRigging
{
    const string MeshPath="Assets/Tripo/点点动作网格.asset";
    static float Fade(float a,float b,float value)=>Mathf.SmoothStep(0,1,Mathf.InverseLerp(a,b,value));

    public static DiandianRig Bind(GameObject imported)
    {
        var filters=imported.GetComponentsInChildren<MeshFilter>();
        if(filters.Length!=1)throw new InvalidOperationException("这份绑定针对已核对的单网格 Tripo 模型。");
        var filter=filters[0];var renderer=filter.GetComponent<MeshRenderer>();
        var materials=renderer.sharedMaterials;
        var mesh=DiandianMeshReduction.Reduce(filter.sharedMesh,240000);
        Transform space=filter.transform;
        Transform Bone(string name,Transform parent,Vector3 position)
        {
            var t=new GameObject(name).transform;t.SetParent(parent,false);
            t.position=space.TransformPoint(position);return t;
        }
        var pelvis=Bone("骨架_骨盆",space,new Vector3(0,.435f,-.015f));
        var body=Bone("胸腹",pelvis,new Vector3(0,.555f,.075f));
        var head=Bone("头颈",body,new Vector3(-.005f,.695f,.170f));
        var crest=Bone("冠羽",head,new Vector3(-.005f,.845f,.215f));
        var tail=Bone("尾根",pelvis,new Vector3(0,.420f,-.105f));
        var bones=new List<Transform>{pelvis,body,head,crest,tail};
        var footRig=DiandianFootRig.Build(space,pelvis,mesh,bones);
        var jawRig=DiandianJawRig.Build(space,head,mesh,bones);
        var weights=new BoneWeight[mesh.vertexCount];var positions=mesh.vertices;
        for(int i=0;i<positions.Length;i++)
        {
            Vector3 p=positions[i];
            float headWeight=Fade(.645f,.735f,p.y);
            float crestWeight=headWeight*Fade(.842f,.892f,p.y);
            float tailWeight=(1-Fade(.295f,.425f,p.y))*(1-Fade(-.125f,-.065f,p.z));
            if(headWeight>0)
                weights[i]=new BoneWeight{boneIndex0=1,weight0=1-headWeight,boneIndex1=2,weight1=headWeight-crestWeight,boneIndex2=3,weight2=crestWeight};
            else
            {
                float bodyWeight=(1-tailWeight)*Fade(.420f,.520f,p.y);
                weights[i]=new BoneWeight{boneIndex0=0,weight0=1-tailWeight-bodyWeight,boneIndex1=1,weight1=bodyWeight,boneIndex2=4,weight2=tailWeight};
            }
            var w=weights[i];
            if(w.weight0<w.weight1){(w.weight0,w.weight1)=(w.weight1,w.weight0);(w.boneIndex0,w.boneIndex1)=(w.boneIndex1,w.boneIndex0);}
            if(w.weight1<w.weight2){(w.weight1,w.weight2)=(w.weight2,w.weight1);(w.boneIndex1,w.boneIndex2)=(w.boneIndex2,w.boneIndex1);}
            if(w.weight0<w.weight1){(w.weight0,w.weight1)=(w.weight1,w.weight0);(w.boneIndex0,w.boneIndex1)=(w.boneIndex1,w.boneIndex0);}
            weights[i]=w;
        }
        footRig.ApplyWeights(weights);
        jawRig.ApplyWeights(weights);
        var bindposes=new Matrix4x4[bones.Count];
        for(int i=0;i<bones.Count;i++)bindposes[i]=bones[i].worldToLocalMatrix*space.localToWorldMatrix;
        mesh.boneWeights=weights;mesh.bindposes=bindposes;
        mesh.name="点点动作网格";
        UnityEngine.Object.DestroyImmediate(renderer);UnityEngine.Object.DestroyImmediate(filter);
        var skin=space.gameObject.AddComponent<SkinnedMeshRenderer>();
        skin.sharedMesh=mesh;skin.sharedMaterials=materials;skin.bones=bones.ToArray();skin.rootBone=pelvis;
        skin.quality=SkinQuality.Bone4;
        Bounds bounds=mesh.bounds;bounds.Expand(.22f);skin.localBounds=bounds;
        var rig=space.gameObject.AddComponent<DiandianRig>();
        rig.pelvis=pelvis;rig.body=body;rig.head=head;rig.crest=crest;rig.tail=tail;
        rig.legs=footRig.legs;rig.feet=footRig.feet;rig.toeRoots=footRig.toeRoots;rig.toeTips=footRig.toeTips;rig.toeAxes=footRig.toeAxes;
        rig.jaw=jawRig.jaw;rig.jawAxis=jawRig.axis;
        rig.InitializeRestPose();
        rig.ApplyPose(0);
        Validate(skin,rig,positions,weights);
        var saved=AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
        if(saved)
        {
            // Use Mesh setters to invalidate GPU buffers when the vertex layout changes.
            // CopySerialized alone leaves the old skinning stride cached in Unity 6.
            saved.Clear(false);saved.indexFormat=mesh.indexFormat;saved.name=mesh.name;
            saved.vertices=mesh.vertices;saved.normals=mesh.normals;saved.uv=mesh.uv;saved.tangents=mesh.tangents;
            saved.boneWeights=mesh.boneWeights;saved.bindposes=mesh.bindposes;
            saved.triangles=mesh.triangles;saved.bounds=mesh.bounds;skin.sharedMesh=saved;
            UnityEngine.Object.DestroyImmediate(mesh);EditorUtility.SetDirty(saved);
        }
        else AssetDatabase.CreateAsset(mesh,MeshPath);
        return rig;
    }

    static void Validate(SkinnedMeshRenderer skin,DiandianRig rig,Vector3[] rest,BoneWeight[] weights)
    {
        foreach(var w in weights)
            if(Mathf.Abs(w.weight0+w.weight1+w.weight2+w.weight3-1)>0.0001f||w.weight0<w.weight1||w.weight1<w.weight2)
                throw new InvalidOperationException("骨骼权重未归一化。");
        var baked=new Mesh();
        try
        {
            skin.BakeMesh(baked,false);var neutral=baked.vertices;float maxRestError=0;
            for(int i=0;i<rest.Length;i++)maxRestError=Mathf.Max(maxRestError,Vector3.Distance(rest[i],neutral[i]));
            if(maxRestError>.0001f)throw new InvalidOperationException("绑定改变了静止外观："+maxRestError);
            rig.SetMotion((int)DiandianRig.Motion.Nod);rig.ApplyPose(.65f);skin.BakeMesh(baked,false);
            var moved=baked.vertices;float headMovement=0,footMovement=0;
            for(int i=0;i<moved.Length;i++)
            {
                if(!float.IsFinite(moved[i].x)||!float.IsFinite(moved[i].y)||!float.IsFinite(moved[i].z))
                    throw new InvalidOperationException("动作产生非法顶点。");
                float delta=Vector3.Distance(rest[i],moved[i]);
                if(rest[i].y>.75f)headMovement=Mathf.Max(headMovement,delta);
                if(rest[i].y<.40f&&rest[i].z>.02f)footMovement=Mathf.Max(footMovement,delta);
            }
            if(headMovement<.005f||footMovement>.001f)throw new InvalidOperationException("点头权重范围异常。");
            rig.SetMotion((int)DiandianRig.Motion.CurlFeet);rig.ApplyPose(1);skin.BakeMesh(baked,false);
            var curled=baked.vertices;float curlMovement=0;
            for(int i=0;i<curled.Length;i++)
            {
                if(!float.IsFinite(curled[i].sqrMagnitude))throw new InvalidOperationException("收爪顶点无效。");
                if(rest[i].y<.40f&&rest[i].z>.02f)curlMovement=Mathf.Max(curlMovement,Vector3.Distance(rest[i],curled[i]));
            }
            if(rig.legs.Length>0&&curlMovement<.005f)throw new InvalidOperationException("收爪没有带动脚部。");
            rig.ResetToRestPose();skin.BakeMesh(baked,false);
            var reset=baked.vertices;float resetError=0;
            for(int i=0;i<rest.Length;i++)resetError=Mathf.Max(resetError,Vector3.Distance(rest[i],reset[i]));
            if(resetError>.0001f)throw new InvalidOperationException("动作回零残留。");
            Debug.Log("DIANDIAN_RIG_OK bones="+skin.bones.Length+" vertices="+rest.Length+" neutralError="+maxRestError+" headMotion="+headMovement+" feetMotion="+footMovement+" curlMotion="+curlMovement+" resetError="+resetError);
        }
        finally
        {
            rig.SetMotion((int)DiandianRig.Motion.Idle);rig.ApplyPose(0);
            UnityEngine.Object.DestroyImmediate(baked);
        }
    }

    [MenuItem("点点/检查基础动作外观")]
    public static void RenderMotionCheck()=>RenderMotionCheck(false);

    [MenuItem("点点/检查新增动作外观")]
    public static void RenderNewMotionCheck()=>RenderMotionCheck(true);

    [MenuItem("点点/试听当前叫声")]
    public static void PreviewCall()
    {
        if(!Application.isPlaying)throw new InvalidOperationException("请在播放模式试听。");
        var preview=UnityEngine.Object.FindAnyObjectByType<DiandianPreview>();
        if(preview)preview.PlaySelectedCall();
    }

    static void RenderMotionCheck(bool details)
    {
        if(Application.isPlaying)throw new InvalidOperationException("请停止播放后检查动作外观。");
        var rig=UnityEngine.Object.FindAnyObjectByType<DiandianRig>();var camera=Camera.main;
        if(!rig||!camera)throw new InvalidOperationException("请先打开点点 Tripo 预览场景。");
        const int width=480,height=600;
        var rt=new RenderTexture(width,height,24);
        var sheet=new Texture2D(width*3,height*2,TextureFormat.RGB24,false);
        var oldTarget=camera.targetTexture;var oldActive=RenderTexture.active;var oldMotion=rig.motion;
        var cameraPosition=camera.transform.position;var cameraRotation=camera.transform.rotation;
        var skin=rig.GetComponent<SkinnedMeshRenderer>();var oldEnabled=skin.enabled;
        var snapshot=new GameObject("动作检查临时网格");
        var baked=new Mesh();snapshot.AddComponent<MeshFilter>().sharedMesh=baked;
        snapshot.AddComponent<MeshRenderer>().sharedMaterials=skin.sharedMaterials;
        snapshot.transform.SetParent(skin.transform,false);
        var poses=details?new[]{0,5,6,6,7,0}:new[]{0,2,2,1,3,4};
        var times=details?new[]{0f,1f,.26f,.78f,.4f,.628f}:new[]{0f,.714f,2.142f,1.65f,.748f,.898f};
        try
        {
            camera.targetTexture=rt;
            for(int i=0;i<poses.Length;i++)
            {
                rig.SetMotion(poses[i]);rig.ApplyPose(times[i]);
                if(details)
                {
                    if(i<4)
                    {
                        Vector3 focus=rig.transform.TransformPoint(new Vector3(0,.385f,.105f));
                        camera.transform.position=focus+new Vector3(0,.8f,1).normalized*1.55f;camera.transform.LookAt(focus);
                    }
                    else if(i==4)
                    {
                        Vector3 focus=rig.transform.TransformPoint(new Vector3(-.02f,.755f,.28f));
                        camera.transform.position=focus+new Vector3(1,.1f,1).normalized*.7f;camera.transform.LookAt(focus);
                        rig.mouthOpen=1;rig.ApplyPose(times[i]);
                    }
                    else{camera.transform.SetPositionAndRotation(cameraPosition,cameraRotation);rig.mouthOpen=0;}
                }
                // Camera.Render reuses a frame's skinning cache; bake each pose explicitly.
                skin.BakeMesh(baked,true);skin.enabled=false;
                camera.Render();RenderTexture.active=rt;
                sheet.ReadPixels(new Rect(0,0,width,height),(i%3)*width,(1-i/3)*height,false);
            }
            sheet.Apply();System.IO.Directory.CreateDirectory("Temp/pipeline-screenshots");
            System.IO.File.WriteAllBytes("Temp/pipeline-screenshots/"+(details?"rig-new-poses.png":"rig-poses.png"),sheet.EncodeToPNG());
            Debug.Log(details?"DIANDIAN_NEW_POSES_OK rest-feet,curl,step-left,step-right,mouth,overview":"DIANDIAN_POSES_OK idle,nod-down,nod-up,look,tail,crest");
        }
        finally
        {
            rig.ResetToRestPose();rig.SetMotion((int)oldMotion);rig.ApplyPose(0);
            camera.transform.SetPositionAndRotation(cameraPosition,cameraRotation);
            skin.enabled=oldEnabled;
            UnityEngine.Object.DestroyImmediate(snapshot);UnityEngine.Object.DestroyImmediate(baked);
            camera.targetTexture=oldTarget;RenderTexture.active=oldActive;
            UnityEngine.Object.DestroyImmediate(sheet);UnityEngine.Object.DestroyImmediate(rt);
        }
    }
}
