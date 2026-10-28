using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// Native Unity meshes: every feather, toe and wing remains genuine 3D geometry.
public static class DiandianBuilder
{
    const string AssetFolder = "Assets/Model";
    static Material grey, contour, headMat, pearl, flight, white, horn, foot, black, eyeMat, crestMat, tailMat, wood;
    static int meshNumber;
    static UnityEngine.Object assetContainer;
    static readonly List<Transform> toeJoints = new List<Transform>();
    static readonly List<Transform> legRoots = new List<Transform>();
    const float HeadY=1.945f,HeadZ=.260f;
    static float Fade(float a,float b,float v)=>Mathf.SmoothStep(0,1,Mathf.InverseLerp(a,b,v));

    class Geometry
    {
        public List<Vector3> v = new List<Vector3>();
        public List<Vector2> uv = new List<Vector2>();
        public List<Color> colors = new List<Color>();
        public List<int> triangles = new List<int>();
        public void Grid(int start, int rows, int cols)
        {
            for(int r=0;r<rows-1;r++) for(int c=0;c<cols-1;c++)
            {
                int a=start+r*cols+c,b=a+cols;
                triangles.AddRange(new[]{a,b,a+1,a+1,b,b+1});
            }
        }
        public Mesh Finish(string name)
        {
            var m=new Mesh { name=name, indexFormat=IndexFormat.UInt32 };
            m.SetVertices(v);m.SetUVs(0,uv);m.SetColors(colors);m.SetTriangles(triangles,0);
            m.RecalculateNormals();m.RecalculateBounds();
            return m;
        }
    }

    static Transform Node(string name,Transform parent,Vector3 position)
    {
        var t=new GameObject(name).transform;t.SetParent(parent,false);t.localPosition=position;return t;
    }
    static Material Mat(string name,Color color,Color mark,float pattern=0,float smooth=.15f,bool feathers=true,bool surface=false)
    {
        var m=new Material(Shader.Find("Diandian/Feather")) {name=name};
        m.SetColor("_Color",color);m.SetColor("_Mark",mark);m.SetFloat("_Pattern",pattern);m.SetFloat("_Smoothness",smooth);
        m.SetFloat("_Feather",feathers?1:0);m.SetFloat("_Surface",surface?1:0);
        if(surface)
        {
            var detail=AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Textures/BodyFeathers.png");
            if(detail){m.SetTexture("_DetailTex",detail);m.SetFloat("_DetailStrength",.78f);m.SetTextureScale("_DetailTex",new Vector2(1.5f,1.4f));}
        }
        AssetDatabase.AddObjectToAsset(m,assetContainer);return m;
    }
    static void MeshObject(string name,Transform parent,Mesh mesh,Material material)
    {
        mesh.name=(++meshNumber)+"_"+name;AssetDatabase.AddObjectToAsset(mesh,assetContainer);
        var g=Node(name,parent,Vector3.zero).gameObject;
        g.AddComponent<MeshFilter>().sharedMesh=mesh;g.AddComponent<MeshRenderer>().sharedMaterial=material;
    }
    static void Feather(Geometry g,Vector3 a,Vector3 b,float width,Vector3 normal,float curve=0.012f,float tone=1)
    {
        Vector3 along=b-a,side=Vector3.Cross(normal,along).normalized;
        const int rows=13,cols=7;int start=g.v.Count;
        for(int r=0;r<rows;r++)
        {
            float t=r/(rows-1f);
            float profile=(.22f+.78f*Mathf.Sin(Mathf.Pow(t,.72f)*Mathf.PI)) * Mathf.Pow(1-t,.16f);
            for(int c=0;c<cols;c++)
            {
                float across=c/(cols-1f)*2-1;
                float edge=1+.012f*Mathf.Sin(t*137+tone*19)*Mathf.Pow(Mathf.Abs(across),6);
                g.v.Add(a+along*t+side*(across*width*profile*edge)+normal*(Mathf.Sin(t*Mathf.PI)*curve+(1-across*across)*width*.035f));
                g.uv.Add(new Vector2(c/(cols-1f),t));g.colors.Add(new Color(tone,tone,tone));
            }
        }
        g.Grid(start,rows,cols);
    }
    static Mesh Ellipsoid(Vector3 center,Vector3 radii,int rings=24,int segments=36,bool faceTint=false)
    {
        var g=new Geometry();
        for(int r=0;r<=rings;r++) for(int s=0;s<=segments;s++)
        {
            float latitude=Mathf.PI*r/rings,longitude=Mathf.PI*2*s/segments;
            Vector3 unit=new Vector3(Mathf.Sin(latitude)*Mathf.Cos(longitude),Mathf.Cos(latitude),Mathf.Sin(latitude)*Mathf.Sin(longitude));
            Vector3 position=center+Vector3.Scale(radii,unit);
            if(faceTint)
            {
                // A cockatiel's nape is flatter than its rounded forehead. Keep the
                // lower face open onto the same neck profile instead of a ball seam.
                position.z*=Mathf.Lerp(.90f,1,Fade(-.8f,.1f,unit.z));
                if(unit.y<-.30f)
                {
                    Vector4 neck=Profile(HeadY+position.y);
                    float join=Fade(-.30f,-.94f,unit.y);
                    position.x=Mathf.Lerp(position.x,(neck.y+.001f)*Mathf.Cos(longitude),join);
                    position.z=Mathf.Lerp(position.z,neck.w-HeadZ+(neck.z+.001f)*Mathf.Sin(longitude),join);
                }
            }
            g.v.Add(position);g.uv.Add(new Vector2(s/(float)segments,r/(float)rings));
            Color tint=Color.white;
            if(faceTint)
            {
                tint=Color.Lerp(new Color(.48f,.49f,.43f),new Color(.87f,.81f,.46f),Mathf.SmoothStep(0,1,(unit.z+.2f)*1.05f+unit.y*.12f));
                Vector3 p=Vector3.Scale(radii,unit);
                float cheek=Mathf.Pow((p.y+.045f)/.052f,2)+Mathf.Pow((p.z-.048f)/.058f,2);
                float patch=(1-Fade(.65f,1.32f,cheek))*Fade(.5f,.84f,Mathf.Abs(unit.x));
                tint=Color.Lerp(tint,new Color(.89f,.39f,.10f),patch);
                tint=Color.Lerp(tint,new Color(.48f,.49f,.46f),Fade(-.42f,-.90f,unit.y));
            }
            g.colors.Add(tint);
        }
        g.Grid(0,rings+1,segments+1);
        for(int i=0;i<g.triangles.Count;i+=3)(g.triangles[i],g.triangles[i+2])=(g.triangles[i+2],g.triangles[i]);
        return g.Finish("surface");
    }
    static Mesh Tube(Vector3[] points,float[] radii,int sides=9)
    {
        var g=new Geometry();
        Vector3 reference=Mathf.Abs((points[points.Length-1]-points[0]).normalized.x)>.85f?Vector3.up:Vector3.right;
        for(int r=0;r<points.Length;r++)
        {
            Vector3 tangent=(points[Math.Min(r+1,points.Length-1)]-points[Math.Max(r-1,0)]).normalized;
            Vector3 u=Vector3.Cross(tangent,reference).normalized;
            Vector3 v=Vector3.Cross(tangent,u).normalized;
            for(int c=0;c<=sides;c++)
            {
                float angle=c*Mathf.PI*2/sides;
                g.v.Add(points[r]+radii[r]*(Mathf.Cos(angle)*u+Mathf.Sin(angle)*v));
                g.uv.Add(new Vector2(c/(float)sides,r/(points.Length-1f)));g.colors.Add(Color.white);
            }
        }
        g.Grid(0,points.Length,sides+1);
        for(int i=0;i<g.triangles.Count;i+=3)(g.triangles[i],g.triangles[i+2])=(g.triangles[i+2],g.triangles[i]);
        return g.Finish("tube");
    }
    static void Coat(Transform root,string name,Vector3 center,Vector3 radii,int rows,int columns,float length,float width,Material material,bool face=false)
    {
        var g=new Geometry();
        for(int row=0;row<rows;row++)for(int col=0;col<columns;col++)
        {
            float phi=.17f+(Mathf.PI-.34f)*row/(rows-1),theta=(col+(row%2)*.5f)*Mathf.PI*2/columns;
            Vector3 n=new Vector3(Mathf.Sin(phi)*Mathf.Cos(theta),Mathf.Cos(phi),Mathf.Sin(phi)*Mathf.Sin(theta));
            Vector3 normal=new Vector3(n.x/radii.x,n.y/radii.y,n.z/radii.z).normalized;
            Vector3 a=center+Vector3.Scale(radii,n)+normal*.0008f;
            Vector3 down=Vector3.ProjectOnPlane(Vector3.down,normal).normalized;
            int start=g.v.Count;
            Feather(g,a,a+down*length,width,normal,.0004f,.98f+.02f*Mathf.Sin(row*7+col*13));
            if(face)
            {
                Color tint=Color.Lerp(new Color(.42f,.44f,.40f),new Color(.83f,.82f,.52f),Mathf.Clamp01((n.z-.1f)*1.8f+n.y*.35f));
                for(int i=start;i<g.colors.Count;i++)g.colors[i]*=tint;
            }
        }
        MeshObject(name,root,g.Finish(name),material);
    }
    // y, half-width, front/back radius, forward offset. One continuous chest/neck,
    // with the forward lean and tapered abdomen of the accepted v1.5 bird.
    static readonly Vector4[] bodyProfile={
        new Vector4(.87f,.035f,.065f,-.20f),new Vector4(1.00f,.175f,.23f,-.10f),
        new Vector4(1.20f,.250f,.29f,-.035f),new Vector4(1.39f,.273f,.285f,.005f),
        new Vector4(1.56f,.250f,.269f,.068f),new Vector4(1.70f,.216f,.224f,.140f),
        new Vector4(1.84f,.174f,.170f,.199f),new Vector4(2.00f,.110f,.112f,.250f)};
    static Vector4 Profile(float y)
    {
        int i=0;while(i<bodyProfile.Length-2 && y>bodyProfile[i+1].x)i++;
        float t=Mathf.InverseLerp(bodyProfile[i].x,bodyProfile[i+1].x,y);
        Vector4 a=bodyProfile[Mathf.Max(0,i-1)],b=bodyProfile[i],c=bodyProfile[i+1],d=bodyProfile[Mathf.Min(bodyProfile.Length-1,i+2)];
        return .5f*((2*b)+(-a+c)*t+(2*a-5*b+4*c-d)*t*t+(-a+3*b-3*c+d)*t*t*t);
    }
    static float BodySide(float y,float z)
    {
        Vector4 p=Profile(y);float cross=(z-p.w)/p.z;
        return p.y*Mathf.Sqrt(Mathf.Max(.035f,1-cross*cross));
    }
    static void Body(Transform root)
    {
        var g=new Geometry();const int rows=72,cols=80;
        for(int r=0;r<=rows;r++)for(int c=0;c<=cols;c++)
        {
            float t=r/(float)rows,y=Mathf.Lerp(bodyProfile[0].x,bodyProfile[bodyProfile.Length-1].x,t),angle=c*Mathf.PI*2/cols;
            Vector4 p=Profile(y);
            g.v.Add(new Vector3(Mathf.Cos(angle)*p.y,y,p.w+Mathf.Sin(angle)*p.z));
            g.uv.Add(new Vector2(c/(float)cols,1-t));
            float belly=Fade(.9f,1.3f,y)*(1-Fade(1.5f,1.9f,y));
            float light=.92f+.08f*belly+.024f*Mathf.Sin(angle*7+y*34);
            g.colors.Add(new Color(light,light,light));
        }
        g.Grid(0,rows+1,cols+1);MeshObject("连续胸腹与前倾颈部",root,g.Finish("body"),grey);
    }
    static Transform Head(Transform root,out Transform crown)
    {
        Transform h=Node("头部",root,new Vector3(0,HeadY,HeadZ));
        MeshObject("融合颊斑的头部羽面",h,Ellipsoid(Vector3.zero,new Vector3(.180f,.193f,.205f),48,72,true),headMat);
        for(int sign=-1;sign<=1;sign+=2)
        {
            Transform eye=Node("眼眶",h,new Vector3(sign*.148f,.050f,.103f));
            eye.localRotation=Quaternion.LookRotation(new Vector3(sign*.85f,.20f,.49f));
            MeshObject("细眼缘",eye,Ellipsoid(Vector3.zero,new Vector3(.0245f,.0255f,.004f)),horn);
            MeshObject("嵌入式黑眼",eye,Ellipsoid(new Vector3(0,0,.0025f),new Vector3(.021f,.022f,.006f)),eyeMat);
            MeshObject("眼睛高光",eye,Ellipsoid(new Vector3(-.006f,.007f,.009f),Vector3.one*.0021f,12,16),white);
        }
        MeshObject("浅灰粉喙基",h,Ellipsoid(new Vector3(0,-.022f,.188f),new Vector3(.048f,.055f,.040f),32,40),horn);
        var beakPoints=new Vector3[24];var beakRadii=new float[24];
        for(int i=0;i<24;i++)
        {
            float t=i/23f,u=1-t;
            beakPoints[i]=u*u*u*new Vector3(0,-.003f,.203f)+3*u*u*t*new Vector3(0,-.005f,.275f)+3*u*t*t*new Vector3(0,-.074f,.268f)+t*t*t*new Vector3(0,-.100f,.217f);
            beakRadii[i]=Mathf.Lerp(.032f,.0005f,Mathf.Pow(t,1.2f));
        }
        Mesh upperBeak=Tube(beakPoints,beakRadii,32);
        Vector3[] beakVertices=upperBeak.vertices;
        for(int i=0;i<beakVertices.Length;i++)beakVertices[i].x*=.86f;
        upperBeak.vertices=beakVertices;upperBeak.RecalculateNormals();upperBeak.RecalculateBounds();
        MeshObject("顺滑弯钩上喙",h,upperBeak,horn);
        MeshObject("下喙",h,Ellipsoid(new Vector3(0,-.082f,.202f),new Vector3(.027f,.016f,.020f)),horn);
        for(int sign=-1;sign<=1;sign+=2)
            MeshObject("鼻孔",h,Ellipsoid(new Vector3(sign*.019f,.011f,.217f),new Vector3(.0048f,.0033f,.0015f),12,12),black);
        crown=Node("冠羽枢轴",h,new Vector3(0,.174f,.036f));
        var feathers=new Geometry();
        float[] lengths={.16f,.245f,.32f,.30f,.235f,.14f};
        float[] widths={.010f,.012f,.0135f,.013f,.011f,.0095f};
        for(int i=0;i<lengths.Length;i++)
        {
            float x=(i-2.5f)*.009f;
            // Slightly different feather planes keep six narrow vanes readable
            // from the side without turning the crown into extra hair or a fan.
            Vector3 normal=new Vector3((i-2.5f)*.16f,.07f,1).normalized;
            Feather(feathers,new Vector3(x,-.014f,.018f),new Vector3(x*1.85f,lengths[i],-.065f-lengths[i]*.21f+(i-2.5f)*.006f),widths[i],normal,.034f,.92f+i*.016f);
        }
        MeshObject("六根疏密不齐的冠羽",crown,feathers.Finish("crest"),crestMat);
        return h;
    }
    static void Tail(Transform root)
    {
        var g=new Geometry();
        for(int i=0;i<12;i++)
        {
            float offset=(i-5.5f),length=1.26f-Mathf.Abs(offset)*.115f;
            Feather(g,new Vector3(offset*.017f,1.105f,-.19f),new Vector3(offset*.024f,1.105f-length*.76f,-.19f-length*1.08f),.052f-Mathf.Abs(offset)*.0027f,new Vector3(.35f,.76f,.55f).normalized,.012f,1-Mathf.Abs(offset)*.018f);
        }
        MeshObject("十二根层叠渐尖长尾羽",root,g.Finish("tail"),tailMat);
    }
    static void Legs(Transform root)
    {
        toeJoints.Clear();legRoots.Clear();
        for(int sign=-1;sign<=1;sign+=2)
        {
            float x=sign*.145f;
            Transform leg=Node(sign<0?"左腿枢轴":"右腿枢轴",root,Vector3.zero);legRoots.Add(leg);
            MeshObject("腹羽衔接腿部",leg,Tube(new[]{new Vector3(x,1.16f,.09f),new Vector3(x,1.03f,.08f),new Vector3(x,.85f,.09f)},new[]{.067f,.052f,.026f},20),grey);
            MeshObject("可见跗跖",leg,Tube(new[]{new Vector3(x,.86f,.09f),new Vector3(x,.77f,.093f),new Vector3(x,.685f,.105f)},new[]{.022f,.019f,.023f},14),foot);
            MeshObject("脚掌",leg,Ellipsoid(new Vector3(x,.68f,.102f),new Vector3(.038f,.024f,.038f)),foot);
            for(int digit=0;digit<4;digit++)
            {
                bool back=digit==0||digit==3;
                float dx=(digit==0||digit==1?-1:1)*.022f;
                Transform parent=Node("趾"+(digit+1)+(back?"_向后":"_向前"),leg,new Vector3(x+dx,.68f,.10f));
                parent.localRotation=Quaternion.Euler(0,(back?180:0)+(digit%2==0?-12:12),0);
                float len=(digit==2?.060f:.052f)*(back?.86f:1);
                for(int joint=0;joint<3;joint++)
                {
                    var t=Node("趾节"+(joint+1),parent,joint==0?Vector3.zero:new Vector3(0,-.008f,len));
                    t.localRotation=Quaternion.Euler(30+joint*5,0,0);
                    toeJoints.Add(t);
                    MeshObject("鳞皮",t,Tube(new[]{Vector3.zero,new Vector3(0,-.008f,len)},new[]{.015f-joint*.0025f,.013f-joint*.0025f},8),foot);
                    // Shallow, fine scales avoid the old bead-like segmented toes.
                    for(int ring=1;ring<=4;ring++)
                        MeshObject("细趾鳞",t,Ellipsoid(new Vector3(0,-.006f*ring/4,len*ring/5),new Vector3(.0148f-joint*.0025f,.013f-joint*.0025f,.0017f),8,10),foot);
                    if(joint==2)
                        MeshObject("深色爪尖",t,Tube(new[]{new Vector3(0,-.008f,len),new Vector3(0,-.025f,len+.027f),new Vector3(0,-.055f,len+.034f)},new[]{.012f,.007f,.0006f},8),black);
                    parent=t;
                }
            }
            Vector3 hip=new Vector3(x,1.10f,.09f);
            foreach(Transform child in leg)child.localPosition-=hip;
            leg.localPosition=hip;
        }
    }

    static SkinnedMeshRenderer Wing(Transform root,int sign,out Transform pivot)
    {
        pivot=Node(sign<0?"左翅_单一结构":"右翅_单一结构",root,new Vector3(sign*.205f,1.70f,0));
        var open=new Geometry();var closed=new Geometry();
        // The same feather vertices have anatomically arranged resting and extended
        // poses. A per-feather pose avoids crushing the folded wing with one affine map.
        void Pair(Vector3 a,Vector3 b,float width,Vector2 restA,Vector2 restB,float restWidth,float layer,float tone=1)
        {
            Feather(open,a,b,width,Vector3.up,.012f,tone);
            int start=closed.v.Count;
            Feather(closed,new Vector3(0,restA.x,restA.y),new Vector3(0,restB.x,restB.y),restWidth,Vector3.right*sign,.002f,tone);
            for(int j=start;j<closed.v.Count;j++)
            {
                Vector3 p=closed.v[j];
                float x=BodySide(p.y,p.z)+layer;
                if(p.y<1.0f)x*=Mathf.Lerp(.55f,1,Mathf.InverseLerp(.80f,1.0f,p.y));
                closed.v[j]=new Vector3(p.x+sign*(x-.205f),p.y-1.70f,p.z);
            }
        }
        for(int i=0;i<10;i++)
        {
            float t=i/9f;
            Vector3 a=new Vector3(sign*(.47f+t*.18f),-.035f,-.05f-t*.10f);
            Vector3 b=new Vector3(sign*(.76f+t*.64f),-.055f-t*.035f,-.53f+t*.22f);
            Pair(a,b,.055f,new Vector2(1.46f-t*.02f,-.07f-t*.014f),new Vector2(.97f-t*.06f,-.37f-t*.10f),.033f,.012f,.91f+t*.09f);
        }
        int primaryEnd=open.triangles.Count;
        for(int i=0;i<10;i++)
        {
            float t=i/9f;
            Pair(new Vector3(sign*(.025f+t*.48f),0,-.015f),new Vector3(sign*(.08f+t*.56f),-.10f,-.45f-t*.055f),.054f,
                new Vector2(1.68f-t*.25f,.10f-t*.07f),new Vector2(1.24f-t*.19f,-.21f-t*.09f),.040f,.020f,.94f+t*.05f);
        }
        int secondaryEnd=open.triangles.Count;
        // A softly curved mantle carries the irregular pearl plumage. It belongs
        // to this same deforming wing; there is no stationary second folded wing.
        int mantleStart=open.v.Count;const int mantleRows=40,mantleCols=24;
        for(int r=0;r<=mantleRows;r++)for(int c=0;c<=mantleCols;c++)
        {
            float t=r/(float)mantleRows,u=c/(float)mantleCols;
            float side=sign>0?u:1-u;
            float width=.035f+.39f*Mathf.Pow(Mathf.Max(0,Mathf.Sin(Mathf.PI*Mathf.Pow(t,.64f))),.70f);
            float y=1.76f-.73f*t,z=.09f-.36f*t-(side-.5f)*width;
            float x=BodySide(y,z)+.024f+.010f*Mathf.Sin(side*Mathf.PI);
            closed.v.Add(new Vector3(sign*(x-.205f),y-1.70f,z));
            open.v.Add(new Vector3(sign*(.035f+t*.94f),.022f+.030f*Mathf.Sin(side*Mathf.PI)*Mathf.Sin(t*Mathf.PI),.05f-side*(.26f+.12f*Mathf.Sin(t*Mathf.PI))-.065f*t));
            Vector2 uv=new Vector2(side,1-t);open.uv.Add(uv);closed.uv.Add(uv);
            open.colors.Add(Color.white);closed.colors.Add(Color.white);
        }
        open.Grid(mantleStart,mantleRows+1,mantleCols+1);closed.Grid(mantleStart,mantleRows+1,mantleCols+1);
        int covertEnd=open.triangles.Count;
        Vector3[] openVertices=open.v.ToArray();
        Mesh openMesh=open.Finish("open wing normals");
        Vector3[] openNormals=openMesh.normals;
        UnityEngine.Object.DestroyImmediate(openMesh);
        Mesh mesh=closed.Finish("wing");
        mesh.subMeshCount=4;
        mesh.SetTriangles(open.triangles.GetRange(0,primaryEnd),0);
        mesh.SetTriangles(open.triangles.GetRange(primaryEnd,secondaryEnd-primaryEnd),1);
        mesh.SetTriangles(open.triangles.GetRange(secondaryEnd,covertEnd-secondaryEnd),2);
        mesh.SetTriangles(open.triangles.GetRange(covertEnd,open.triangles.Count-covertEnd),3);
        var deltas=new Vector3[openVertices.Length];var normalDeltas=new Vector3[openVertices.Length];
        Vector3[] closedNormals=mesh.normals;
        for(int i=0;i<deltas.Length;i++){deltas[i]=openVertices[i]-closed.v[i];normalDeltas[i]=openNormals[i]-closedNormals[i];}
        mesh.AddBlendShapeFrame("展开",100,deltas,normalDeltas,null);
        mesh.name=sign<0?"LeftWing":"RightWing";AssetDatabase.AddObjectToAsset(mesh,assetContainer);
        var renderer=pivot.gameObject.AddComponent<SkinnedMeshRenderer>();renderer.sharedMesh=mesh;
        renderer.sharedMaterials=new[]{flight,flight,pearl,white};renderer.updateWhenOffscreen=true;
        renderer.localBounds=new Bounds(new Vector3(sign*.5f,-.3f,-.2f),Vector3.one*3);
        return renderer;
    }
    static void Perch(Transform parent)
    {
        var horizontal=Node("天然木横杆",parent,new Vector3(0,.59f,.1f));
        MeshObject("木杆",horizontal,Tube(new[]{new Vector3(-.70f,0,0),new Vector3(.70f,0,0)},new[]{.066f,.066f},24),wood);
        MeshObject("支柱",parent,Tube(new[]{new Vector3(0,-.15f,.1f),new Vector3(0,.59f,.1f)},new[]{.045f,.045f},18),wood);
        MeshObject("底座",parent,Ellipsoid(new Vector3(0,-.17f,.1f),new Vector3(.55f,.03f,.35f)),wood);
    }

    [MenuItem("点点/旧原型/重建程序模型")]
    public static void Build()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        Directory.CreateDirectory(AssetFolder);AssetDatabase.Refresh();
        AssetDatabase.DeleteAsset(AssetFolder+"/DiandianMeshes.asset");
        assetContainer=new Mesh {name="点点原生三维资产"};AssetDatabase.CreateAsset(assetContainer,AssetFolder+"/DiandianMeshes.asset");
        meshNumber=0;
        var textureImporter=AssetImporter.GetAtPath("Assets/Textures/BodyFeathers.png") as TextureImporter;
        if(textureImporter){textureImporter.wrapMode=TextureWrapMode.Repeat;textureImporter.mipmapEnabled=true;textureImporter.sRGBTexture=false;textureImporter.maxTextureSize=2048;textureImporter.anisoLevel=4;textureImporter.SaveAndReimport();}
        var wingImporter=AssetImporter.GetAtPath("Assets/Textures/WingPearl.png") as TextureImporter;
        if(wingImporter){wingImporter.wrapMode=TextureWrapMode.Clamp;wingImporter.mipmapEnabled=true;wingImporter.sRGBTexture=true;wingImporter.maxTextureSize=2048;wingImporter.anisoLevel=4;wingImporter.SaveAndReimport();}
        grey=Mat("柔软灰色体羽",new Color(.48f,.49f,.46f),Color.white,0,.10f,true,true);
        headMat=Mat("细羽黄灰面部",Color.white,Color.white,0,.10f,true,true);
        contour=Mat("细软轮廓羽",new Color(.46f,.47f,.44f),Color.white,0,.1f);
        pearl=Mat("真实珍珠翼面",Color.white,Color.white,0,.10f,true,true);
        var wingTexture=AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Textures/WingPearl.png");
        if(wingTexture){pearl.SetTexture("_AlbedoTex",wingTexture);pearl.SetFloat("_AlbedoStrength",1);pearl.SetFloat("_DetailStrength",0);}
        flight=Mat("深灰飞羽",new Color(.28f,.29f,.27f),new Color(.50f,.52f,.46f));
        white=Mat("白色羽区",new Color(.86f,.86f,.79f),Color.white);
        horn=Mat("浅灰粉角质",new Color(.61f,.51f,.45f),Color.white,0,.20f,false);
        foot=Mat("粉灰鳞皮",new Color(.53f,.37f,.34f),Color.white,0,.13f,false);
        black=Mat("深色爪与鼻孔",new Color(.055f,.050f,.043f),Color.white,0,.25f,false);
        eyeMat=Mat("湿润黑眼",new Color(.008f,.007f,.006f),Color.white,0,.78f,false);
        crestMat=Mat("灰尖黄根冠羽",new Color(.34f,.35f,.29f),new Color(.78f,.76f,.43f),3);
        tailMat=Mat("浅黄灰横斑尾羽",new Color(.39f,.40f,.31f),new Color(.77f,.77f,.55f),2);
        wood=Mat("原木",new Color(.64f,.47f,.29f),Color.white,0,.15f,false);
        Transform model=Node("点点_三维原型",null,Vector3.zero);
        Body(model);Transform crown;Transform head=Head(model,out crown);Tail(model);Legs(model);
        Transform left,right;
        var leftRenderer=Wing(model,-1,out left);var rightRenderer=Wing(model,1,out right);
        var perch=Node("栖木",null,Vector3.zero);Perch(perch);
        var camera=Node("预览相机",null,new Vector3(3.85f,2.05f,1.90f)).gameObject.AddComponent<Camera>();
        camera.transform.LookAt(new Vector3(0,1.2f,-.35f));camera.fieldOfView=37;camera.nearClipPlane=.05f;camera.farClipPlane=30;
        camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.13f,.15f,.17f);camera.tag="MainCamera";
        void LightAt(string name,Vector3 angles,float intensity,Color color)
        {
            var light=Node(name,null,Vector3.zero).gameObject.AddComponent<Light>();light.type=LightType.Directional;
            light.transform.eulerAngles=angles;light.intensity=intensity;light.color=color;light.shadows=LightShadows.Soft;
        }
        LightAt("主光",new Vector3(38,-125,0),.95f,new Color(1,.97f,.91f));
        LightAt("柔光",new Vector3(20,60,0),.35f,new Color(.83f,.88f,1));
        RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(.34f,.36f,.38f);
        var preview=Node("预览控制",null,Vector3.zero).gameObject.AddComponent<DiandianPreview>();
        preview.viewCamera=camera;preview.modelRoot=model;preview.head=head;preview.crest=crown;
        preview.leftWingRoot=left;preview.rightWingRoot=right;preview.wings=new[]{leftRenderer,rightRenderer};
        preview.toeJoints=toeJoints.ToArray();preview.legRoots=legRoots.ToArray();preview.perchRoot=perch;preview.crestRaise=.72f;
        preview.ApplyPose(0);
        AssetDatabase.SaveAssets();
        PrefabUtility.SaveAsPrefabAsset(model.gameObject,AssetFolder+"/点点.prefab");
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(),"Assets/点点预览.unity");
        EditorBuildSettings.scenes=new[]{new EditorBuildSettingsScene("Assets/点点预览.unity",true)};
        Validate();
        if(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Null)RenderPreview(preview,camera);
        Debug.Log("DIANDIAN_BUILD_OK meshes="+meshNumber+" toeJoints="+toeJoints.Count);
    }
    static void Validate()
    {
        var preview=UnityEngine.Object.FindAnyObjectByType<DiandianPreview>();
        if(preview.wings.Length!=2||preview.toeJoints.Length!=24)throw new Exception("翅膀或对趾结构不完整");
        int vertices=0;
        foreach(var mesh in AssetDatabase.LoadAllAssetsAtPath(AssetFolder+"/DiandianMeshes.asset"))
        {
            if(!(mesh is Mesh m))continue;vertices+=m.vertexCount;
            foreach(var v in m.vertices)if(!float.IsFinite(v.x)||!float.IsFinite(v.y)||!float.IsFinite(v.z))throw new Exception("Invalid mesh vertex");
            foreach(int i in m.triangles)if(i<0||i>=m.vertexCount)throw new Exception("Invalid triangle");
        }
        foreach(var w in preview.wings)if(w.sharedMesh.blendShapeCount!=1)throw new Exception("翅膀必须是同一网格的折叠与展开");
        Debug.Log("DIANDIAN_VALIDATE_OK vertices="+vertices);
    }
    static void RenderPreview(DiandianPreview preview,Camera camera)
    {
        var rt=new RenderTexture(1600,1100,24);camera.targetTexture=rt;
        var image=new Texture2D(1600,1100,TextureFormat.RGB24,false);
        camera.Render();RenderTexture.active=rt;image.ReadPixels(new Rect(0,0,1600,1100),0,0);image.Apply();
        File.WriteAllBytes("点点三维预览.png",image.EncodeToPNG());
        camera.targetTexture=null;RenderTexture.active=null;UnityEngine.Object.DestroyImmediate(image);UnityEngine.Object.DestroyImmediate(rt);
    }

    [MenuItem("点点/从 Tripo 模型重建预览")]
    public static void BuildTripoPreview()
    {
        if(Application.isPlaying)throw new InvalidOperationException("请先停止播放，再重建点点模型。");
        const string source="Assets/Tripo/Diandian.glb";
        AssetDatabase.ImportAsset(source,ImportAssetOptions.ForceSynchronousImport);
        var sourcePrefab=AssetDatabase.LoadAssetAtPath<GameObject>(source);
        if(!sourcePrefab)throw new InvalidOperationException("Tripo GLB 尚未导入，请先安装 Unity glTFast。");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        Transform model=Node("点点_Tripo模型",null,Vector3.zero);
        var bird=(GameObject)PrefabUtility.InstantiatePrefab(sourcePrefab);
        PrefabUtility.UnpackPrefabInstance(bird,PrefabUnpackMode.Completely,InteractionMode.AutomatedAction);
        bird.transform.SetParent(model,false);
        var renderers=bird.GetComponentsInChildren<Renderer>();
        if(renderers.Length==0)throw new InvalidOperationException("GLB 没有可显示的网格。");
        Bounds bounds=renderers[0].bounds;
        foreach(var r in renderers)bounds.Encapsulate(r.bounds);
        float scale=2.6f/Mathf.Max(.001f,bounds.size.y);
        // Bind and verify in the GLB's native coordinates before presentation scaling.
        var rig=DiandianTripoRigging.Bind(bird);
        bird.transform.localScale=Vector3.one*scale;
        bird.transform.localPosition=-new Vector3(bounds.center.x,bounds.min.y,bounds.center.z)*scale;
        var camera=Node("预览相机",null,Vector3.zero).gameObject.AddComponent<Camera>();
        camera.tag="MainCamera";camera.fieldOfView=37;camera.nearClipPlane=.03f;camera.farClipPlane=30;
        camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.19f,.20f,.22f);
        void LightAt(string name,Vector3 angles,float intensity,Color color)
        {
            var l=Node(name,null,Vector3.zero).gameObject.AddComponent<Light>();
            l.type=LightType.Directional;l.transform.eulerAngles=angles;l.intensity=intensity;l.color=color;
        }
        LightAt("柔和主光",new Vector3(35,-130,0),.85f,new Color(1,.98f,.94f));
        LightAt("轮廓补光",new Vector3(20,50,0),.35f,new Color(.88f,.93f,1));
        RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(.47f,.47f,.47f);
        var preview=Node("预览控制",null,Vector3.zero).gameObject.AddComponent<DiandianPreview>();
        preview.modelRoot=model;preview.viewCamera=camera;preview.viewFocus=new Vector3(0,1.3f,0);preview.importedStaticModel=true;
        preview.rig=rig;
        camera.gameObject.AddComponent<AudioListener>();
        preview.voice=preview.gameObject.AddComponent<AudioSource>();
        preview.voice.playOnAwake=false;preview.voice.spatialBlend=0;preview.voice.volume=.4f;
        string[] callFiles={"brief","series","rising","double_turn","trill"};
        preview.callNames=new[]{"叽咕叽","peekbo","拍手歌","小鸡宝宝","连续转音"};
        preview.calls=new AudioClip[callFiles.Length];Directory.CreateDirectory("Assets/Tripo/Calls");
        for(int i=0;i<callFiles.Length;i++)
        {
            string original=Path.GetFullPath("../../app/calls/"+callFiles[i]+".wav");
            string destination="Assets/Tripo/Calls/"+callFiles[i]+".wav";
            if(!File.Exists(destination)||File.GetLastWriteTimeUtc(original)>File.GetLastWriteTimeUtc(destination))File.Copy(original,destination,true);
            AssetDatabase.ImportAsset(destination,ImportAssetOptions.ForceSynchronousImport);
            preview.calls[i]=AssetDatabase.LoadAssetAtPath<AudioClip>(destination);
            if(!preview.calls[i])throw new InvalidOperationException("叫声未导入："+callFiles[i]);
        }
        preview.ResetView();preview.ApplyPose(0);
        PrefabUtility.SaveAsPrefabAsset(model.gameObject,"Assets/Tripo/点点.prefab");
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(),"Assets/点点预览.unity");
        EditorBuildSettings.scenes=new[]{new EditorBuildSettingsScene("Assets/点点预览.unity",true)};
        AssetDatabase.SaveAssets();RenderPreview(preview,camera);
        Selection.activeGameObject=model.gameObject;
        Debug.Log("DIANDIAN_TRIPO_IMPORT_OK renderers="+renderers.Length+" scale="+scale);
    }
}
