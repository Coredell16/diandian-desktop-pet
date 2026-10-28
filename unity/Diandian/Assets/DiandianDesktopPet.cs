using System;
using UnityEngine;

// The desktop controller reuses the preview rig and recordings; no second pose writer.
public sealed class DiandianDesktopPet : MonoBehaviour
{
    public Camera viewCamera;
    public Transform modelRoot;
    public DiandianRig rig;
    public DiandianFlight flight;
    public AudioSource voice;
    public AudioClip[] calls;
    public string[] callNames;
    public DiandianDesktopWindow window;
    public DiandianUpdater updater;

    const string Prefs="DiandianV2.Pet.";
    readonly float[] samples=new float[256];
    bool dragging,muted,autoActions=true,autoCalls,installing,watchCheck,watchDownload;
    bool topmost=true;
    float volume=.4f,nextAction,actionEnd,bubbleUntil;
    string bubble="",lastDownload="";
    int pendingMenu,lastCall=-1,offeredBuild;
    Font chineseFont;
    GUIStyle bubbleStyle;
    System.Threading.Mutex instanceMutex;
    bool ownsMutex;

    void Awake()
    {
        if(!Application.isEditor&&DiandianUpdateInstaller.TryLaunchNewer(DiandianUpdater.BuildNumber,Application.persistentDataPath))
        {
            if(window)window.enabled=false;
            if(updater)updater.enabled=false;
            enabled=false;Application.Quit();
            return;
        }
        if(!Application.isEditor)
        {
            instanceMutex=new System.Threading.Mutex(false,"Local\\DiandianV2-"+DiandianUpdater.BuildNumber);
            try{ownsMutex=instanceMutex.WaitOne(0);}
            catch(System.Threading.AbandonedMutexException){ownsMutex=true;}
            if(!ownsMutex)
            {
                if(window)window.enabled=false;
                if(updater)updater.enabled=false;
                enabled=false;Application.Quit();
            }
        }
    }

    void Start()
    {
        Application.runInBackground=true;Application.targetFrameRate=30;QualitySettings.vSyncCount=0;
        volume=PlayerPrefs.GetFloat(Prefs+"Volume",.4f);
        muted=PlayerPrefs.GetInt(Prefs+"Muted",0)!=0;
        autoActions=PlayerPrefs.GetInt(Prefs+"AutoActions",1)!=0;
        autoCalls=PlayerPrefs.GetInt(Prefs+"AutoCalls",0)!=0;
        topmost=PlayerPrefs.GetInt("DiandianV2.Window.Topmost",1)!=0;
        if(modelRoot)modelRoot.localRotation=Quaternion.Euler(0,PlayerPrefs.GetFloat(Prefs+"Yaw",0),0);
        if(voice){voice.volume=Mathf.Clamp01(volume);voice.mute=muted;voice.loop=false;}
        if(window)
        {
            window.Clicked+=OnClick;window.DoubleClicked+=RandomCall;
            window.DragChanged+=OnDrag;window.ContextRequested+=OnContext;
            window.MenuSelected+=OnMenuSelected;
        }
        nextAction=Time.unscaledTime+UnityEngine.Random.Range(12f,24f);
        Say("我是点点\n右键打开菜单",6);
    }

    void OnDestroy()
    {
        if(window)
        {
            window.Clicked-=OnClick;window.DoubleClicked-=RandomCall;
            window.DragChanged-=OnDrag;window.ContextRequested-=OnContext;
            window.MenuSelected-=OnMenuSelected;
        }
        if(chineseFont)Destroy(chineseFont);
        if(ownsMutex)instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
    }

    void OnClick(){if(!dragging)Act(DiandianRig.Motion.Nod,2.8f);}
    void OnContext(){pendingMenu=1;}
    void OnMenuSelected(int id){pendingMenu=id;}
    void OnDrag(bool value)
    {
        if(Array.IndexOf(Environment.GetCommandLineArgs(),"-diandian-inspect")>=0)
            Debug.Log("DIANDIAN_FLIGHT drag="+value+" spread="+(flight?flight.Spread:0).ToString("F3"));
        dragging=value;Application.targetFrameRate=value?60:30;
        if(flight)flight.SetFlying(value);
        if(rig&&(value||!flight||!flight.IsActive))rig.SetMotion((int)(value?DiandianRig.Motion.CurlFeet:DiandianRig.Motion.Idle));
        actionEnd=0;nextAction=Time.unscaledTime+15;
    }

    void Update()
    {
        if(installing)return;
        Application.targetFrameRate=dragging||(flight&&flight.IsActive)?60:30;
        if(pendingMenu!=0){int id=pendingMenu;pendingMenu=0;Menu(id);}
        if(!dragging&&actionEnd==0&&flight&&!flight.IsActive&&rig&&rig.motion==DiandianRig.Motion.CurlFeet)
            rig.SetMotion((int)DiandianRig.Motion.Idle);
        if(rig&&voice)
        {
            float energy=0;
            if(voice.isPlaying){voice.GetOutputData(samples,0);foreach(float value in samples)energy+=value*value;}
            rig.mouthOpen=Mathf.MoveTowards(rig.mouthOpen,Mathf.Clamp01(Mathf.Sqrt(energy/samples.Length)*18),Time.unscaledDeltaTime*10);
        }
        if(!dragging&&actionEnd>0&&Time.unscaledTime>=actionEnd&&(!voice||!voice.isPlaying))
        {if(rig)rig.SetMotion((int)DiandianRig.Motion.Idle);if(flight)flight.SetFlying(false);actionEnd=0;}
        if(!dragging&&(autoActions||autoCalls)&&actionEnd==0&&Time.unscaledTime>=nextAction)
        {
            if(autoCalls&&(!autoActions||UnityEngine.Random.value<.15f))RandomCall();
            else Act((DiandianRig.Motion)UnityEngine.Random.Range(1,5),3.8f);
            nextAction=Time.unscaledTime+UnityEngine.Random.Range(18f,35f);
        }
        if(!updater)return;
        if(watchCheck&&!updater.IsBusy){watchCheck=false;Say(updater.UpdateStatus,7);}
        if(watchDownload&&!updater.IsBusy)
        {watchDownload=false;if(string.IsNullOrEmpty(updater.DownloadedZipPath))Say(updater.UpdateStatus,8);}
        if(updater.UpdateAvailable&&updater.AvailableBuildNumber!=offeredBuild&&!dragging)
        {offeredBuild=updater.AvailableBuildNumber;Say("发现新版 "+updater.AvailableVersion+"\n右键 → 更新，自行选择",10);}
        if(!string.IsNullOrEmpty(updater.DownloadedZipPath)&&updater.DownloadedZipPath!=lastDownload&&!dragging)
        {lastDownload=updater.DownloadedZipPath;Say("新版已下载并校验\n右键 → 更新，可选择安装",10);}
    }

    void Act(DiandianRig.Motion motion,float seconds)
    {
        if(dragging)return;
        if(flight)flight.SetFlying(false);
        if(rig)rig.SetMotion((int)motion);
        actionEnd=Time.unscaledTime+seconds;nextAction=actionEnd+UnityEngine.Random.Range(15f,30f);
    }

    public void RandomCall()
    {
        if(calls==null||calls.Length==0)return;
        int index=UnityEngine.Random.Range(0,calls.Length);
        if(calls.Length>1&&index==lastCall)index=(index+UnityEngine.Random.Range(1,calls.Length))%calls.Length;
        PlayCall(index);
    }

    public void PlayCall(int index)
    {
        if(!voice||calls==null||index<0||index>=calls.Length||!calls[index])return;
        if(muted){Say("已静音，可在声音设置中开启");return;}
        lastCall=index;voice.Stop();voice.clip=calls[index];voice.Play();
        Act(DiandianRig.Motion.Call,calls[index].length+.5f);
        Say(callNames!=null&&index<callNames.Length?callNames[index]:"点点叫了一声",3);
    }

    void Say(string text,float seconds=5){bubble=text;bubbleUntil=Time.unscaledTime+seconds;}
    void Show(string[] labels,int[] ids){if(window)window.ShowMenu(labels,ids);}
    string Mark(bool value,string label)=>(value?"✓ ":"")+label;

    void Menu(int id)
    {
        switch(id)
        {
            case 1:
                Show(new[]{"点点 v"+DiandianUpdater.VersionLabel,"","动作…","叫声…","外观与声音…","更新…","使用说明","","退出点点"},
                     new[]{0,0,2,3,4,5,6,0,9});break;
            case 2:
                Show(new[]{"点头","看看周围","轻摆尾巴","轻动冠羽","收起爪子","原地踏步","展翅试飞",Mark(autoActions,"自动小动作"),"回到待机","← 返回"},
                     new[]{102,101,103,104,105,106,107,110,100,1});break;
            case 3:
                Show(new[]{"叽咕叽","peekbo","拍手歌","小鸡宝宝","连续转音","随机叫声（避免连着重复）","停止叫声","← 返回"},
                     new[]{200,201,202,203,204,210,211,1});break;
            case 4:
                Show(new[]{"大小：小","大小：标准","大小：大",Mark(topmost,"始终置顶"),"朝向左侧","朝向右侧","",Mark(muted,"静音"),"音量 20%","音量 40%","音量 70%",Mark(autoCalls,"偶尔自己叫一声"),"← 返回"},
                     new[]{300,301,302,303,304,305,0,310,311,312,313,314,1});break;
            case 5:UpdateMenu();break;
            case 6:
                Show(new[]{"单击点头 · 双击叫声","拖动展翅拍动，松手收翼","右键选择动作、叫声和更新","三维飞羽与翼下结构重建预览","← 返回"},new[]{0,0,0,0,1});break;
            case 9:Application.Quit();break;
            case 100:if(flight)flight.SetFlying(false);if(rig)rig.SetMotion((int)DiandianRig.Motion.Idle);actionEnd=0;nextAction=Time.unscaledTime+20;break;
            case 101:case 102:case 103:case 104:case 105:case 106:Act((DiandianRig.Motion)(id-100),5);break;
            case 107:Act(DiandianRig.Motion.CurlFeet,5);if(flight)flight.SetFlying(true);break;
            case 110:autoActions=!autoActions;Save();break;
            case 200:case 201:case 202:case 203:case 204:PlayCall(id-200);break;
            case 210:RandomCall();break;
            case 211:if(voice)voice.Stop();if(rig)rig.mouthOpen=0;break;
            case 300:window.SetSize(200,240);break;
            case 301:window.SetSize(260,300);break;
            case 302:window.SetSize(320,380);break;
            case 303:topmost=!topmost;window.SetTopmost(topmost);PlayerPrefs.Save();break;
            case 304:case 305:
                if(modelRoot)modelRoot.localRotation=Quaternion.Euler(0,id==304?0:-120,0);
                PlayerPrefs.SetFloat(Prefs+"Yaw",id==304?0:-120);PlayerPrefs.Save();break;
            case 310:muted=!muted;Save();break;
            case 311:case 312:case 313:volume=id==311?.2f:id==312?.4f:.7f;muted=false;Save();break;
            case 314:autoCalls=!autoCalls;Save();break;
            case 400:updater.Check();watchCheck=true;Say(updater.UpdateStatus);break;
            case 401:updater.Download();watchDownload=true;Say(updater.UpdateStatus);break;
            case 402:updater.SkipThisVersion();Say(updater.UpdateStatus);break;
            case 403:updater.AutoCheck=!updater.AutoCheck;break;
            case 404:updater.Cancel();break;
            case 405:updater.OpenDownloadFolder();break;
            case 406:InstallAndRestart();break;
        }
    }

    void UpdateMenu()
    {
        if(!updater)return;
        if(updater.IsBusy)
            Show(new[]{updater.UpdateStatus,"下载进度 "+Mathf.RoundToInt(updater.DownloadProgress*100)+"%","取消本次操作","← 返回"},new[]{0,0,404,1});
        else if(!string.IsNullOrEmpty(updater.DownloadedZipPath))
            Show(new[]{"新版 "+updater.AvailableVersion+" 已下载并校验","安装并重启点点","打开更新包文件夹","稍后再说","← 返回"},new[]{0,406,405,0,1});
        else if(updater.UpdateAvailable)
        {
            string notes=updater.ReleaseNotes.Replace('\r',' ').Replace('\n',' ');
            if(notes.Length>140)notes=notes.Substring(0,140)+"…";
            Show(new[]{"发现新版 "+updater.AvailableVersion,notes,"下载新版","略过此版本","稍后再说","← 返回"},new[]{0,0,401,402,0,1});
        }
        else Show(new[]{"当前 v"+DiandianUpdater.VersionLabel,updater.UpdateStatus,"检查更新",Mark(updater.AutoCheck,"每天自动检查"),"← 返回"},new[]{0,0,400,403,1});
    }

    async void InstallAndRestart()
    {
        if(installing||!updater||string.IsNullOrEmpty(updater.DownloadedZipPath))return;
        installing=true;Say("正在安装新版…",300);
        try
        {
            if(!await updater.VerifyDownloadedPackage())throw new System.IO.InvalidDataException(updater.UpdateStatus);
            string exe=await DiandianUpdateInstaller.InstallAsync(updater.DownloadedZipPath,updater.AvailableBuildNumber,Application.persistentDataPath);
            PlayerPrefs.Save();DiandianUpdateInstaller.LaunchInstalled(exe);Application.Quit();
        }
        catch(Exception e){Say("安装失败，点点继续陪伴\n"+e.Message,12);Debug.LogWarning("DIANDIAN_UPDATE: "+e.Message);}
        finally{installing=false;}
    }

    void Save()
    {
        PlayerPrefs.SetFloat(Prefs+"Volume",volume);PlayerPrefs.SetInt(Prefs+"Muted",muted?1:0);
        PlayerPrefs.SetInt(Prefs+"AutoActions",autoActions?1:0);PlayerPrefs.SetInt(Prefs+"AutoCalls",autoCalls?1:0);PlayerPrefs.Save();
        if(voice){voice.volume=volume;voice.mute=muted;}
    }

    void OnGUI()
    {
        if(Time.unscaledTime>bubbleUntil||string.IsNullOrEmpty(bubble))return;
        if(bubbleStyle==null)
        {
            chineseFont=Font.CreateDynamicFontFromOSFont(new[]{"Microsoft YaHei","SimHei","Arial"},14);
            bubbleStyle=new GUIStyle(GUI.skin.box){font=chineseFont,fontSize=14,wordWrap=true,alignment=TextAnchor.MiddleCenter};
            bubbleStyle.normal.textColor=Color.white;
        }
        float width=Mathf.Min(Screen.width-12,280);
        float height=Mathf.Clamp(bubbleStyle.CalcHeight(new GUIContent(bubble),width)+8,35,110);
        GUI.Box(new Rect((Screen.width-width)*.5f,6,width,height),bubble,bubbleStyle);
    }
}
