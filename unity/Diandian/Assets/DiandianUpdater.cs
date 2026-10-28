using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

// Optional HTTPS update client. It downloads a verified archive; it never executes
// a downloaded command, extracts files, or replaces the running application.
public class DiandianUpdater : MonoBehaviour
{
    public const string VersionLabel="2.0.0-preview.3";
    public const int BuildNumber=20003;
    public const string ExecutableName="点点.exe";
    public const long MaximumDownloadSize=1024L*1024*1024;
    const int MaximumManifestSize=64*1024;
    const string PrefPrefix="Diandian.Update.";
    public const string UpgradeInstructions="可在更新菜单中选择安装并重启，或稍后再安装。";

    [Serializable] public sealed class UpdateManifest
    {
        public int schema;
        public string platform,version,url,sha256,notes;
        public int buildNumber;
        public long size;
    }
    [Serializable] sealed class UpdateSource { public string manifestUrl=""; }
    sealed class TransferResult
    {
        public byte[] data;
        public long bytes;
        public string sha256,error;
        public bool cancelled;
    }

    public string UpdateStatus { get; private set; }="尚未检查更新";
    public bool UpdateAvailable { get; private set; }
    public bool IsBusy { get; private set; }
    public string AvailableVersion { get; private set; }="";
    public string ReleaseNotes { get; private set; }="";
    public string DownloadedFilePath { get; private set; }="";
    public string DownloadedZipPath=>DownloadedFilePath;
    public int AvailableBuildNumber=>available?.buildNumber??0;
    public float DownloadProgress { get; private set; }
    public bool AutoCheck
    {
        get=>PlayerPrefs.GetInt(PrefPrefix+"AutoCheck",1)!=0;
        set
        {
            PlayerPrefs.SetInt(PrefPrefix+"AutoCheck",value?1:0);PlayerPrefs.Save();
            if(!value&&automaticCheckRunning)Cancel();
        }
    }

    UpdateManifest available;
    UnityWebRequest activeRequest;
    LimitedHandler activeHandler;
    string activePartPath;
    int downloadedBuild;
    string downloadedHash;
    bool cancelRequested,automaticCheckRunning;

    IEnumerator Start()
    {
        // Avoid networking during startup and check at most once per UTC day.
        yield return new WaitForSecondsRealtime(20);
        while(true)
        {
            long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long.TryParse(PlayerPrefs.GetString(PrefPrefix+"LastCheck","0"),NumberStyles.Integer,CultureInfo.InvariantCulture,out long last);
            if(AutoCheck&&!IsBusy&&(now-last>=86400||now<last))BeginCheck(true);
            yield return new WaitForSecondsRealtime(300);
        }
    }

    public void Check()=>BeginCheck(false);
    void BeginCheck(bool automatic)
    {
        if(IsBusy)return;
        if(Application.platform!=RuntimePlatform.WindowsPlayer&&Application.platform!=RuntimePlatform.WindowsEditor)
        {UpdateStatus="更新包仅支持 Windows x64。";return;}
        if(!TryReadSource(out string url,out string error)){UpdateStatus=error;return;}
        IsBusy=true;cancelRequested=false;automaticCheckRunning=automatic;
        PlayerPrefs.SetString(PrefPrefix+"LastCheck",DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));PlayerPrefs.Save();
        StartCoroutine(CheckRoutine(url,automatic));
    }

    IEnumerator CheckRoutine(string url,bool automatic)
    {
        try
        {
            UpdateStatus="正在检查更新…";
            var result=new TransferResult();
            yield return Transfer(url,null,MaximumManifestSize,result);
            if(result.cancelled){UpdateStatus="已取消检查。";yield break;}
            if(result.error!=null){UpdateStatus="检查失败："+result.error;yield break;}
            string json=Encoding.UTF8.GetString(result.data??Array.Empty<byte>()).TrimStart('\uFEFF');
            if(!ValidateManifest(json,out var manifest,out string error))
            {UpdateStatus="更新信息无效："+error;yield break;}
            available=manifest;AvailableVersion=manifest.version;ReleaseNotes=manifest.notes??"";
            if(downloadedBuild!=manifest.buildNumber||!string.Equals(downloadedHash,manifest.sha256,StringComparison.OrdinalIgnoreCase))
                DownloadedFilePath="";
            if(manifest.buildNumber<=BuildNumber)
            {UpdateAvailable=false;UpdateStatus="当前已是最新版本（"+VersionLabel+"）。";yield break;}
            bool skipped=PlayerPrefs.GetInt(PrefPrefix+"SkippedBuild",0)==manifest.buildNumber;
            UpdateAvailable=!(automatic&&skipped);
            UpdateStatus=UpdateAvailable?"发现新版本 "+manifest.version+"，由你决定是否下载。":"已略过版本 "+manifest.version+"。";
        }
        finally{IsBusy=false;automaticCheckRunning=false;}
    }

    public void Download()
    {
        if(IsBusy)return;
        if(!UpdateAvailable||available==null){UpdateStatus="请先检查并选择可用的新版本。";return;}
        if(!ValidateManifest(available,out string error)){UpdateStatus="更新信息无效："+error;return;}
        string directory=Path.Combine(Application.persistentDataPath,"Updates");
        string file=Path.Combine(directory,"Diandian-"+available.buildNumber+"-"+available.sha256.Substring(0,12).ToLowerInvariant()+".zip");
        try{Directory.CreateDirectory(directory);}
        catch(Exception e){UpdateStatus="无法创建下载目录："+e.Message;return;}
        IsBusy=true;cancelRequested=false;automaticCheckRunning=false;DownloadProgress=0;
        activePartPath=file+".part";
        StartCoroutine(DownloadRoutine(available,file));
    }

    IEnumerator DownloadRoutine(UpdateManifest manifest,string destination)
    {
        string part=destination+".part";bool committed=false;
        try
        {
            UpdateStatus="正在下载 "+manifest.version+"…";
            var result=new TransferResult();
            yield return Transfer(manifest.url,part,manifest.size,result);
            if(result.cancelled){UpdateStatus="已取消下载，点点继续陪伴。";yield break;}
            if(result.error!=null){UpdateStatus="下载失败："+result.error;yield break;}
            if(result.bytes!=manifest.size||!string.Equals(result.sha256,manifest.sha256,StringComparison.OrdinalIgnoreCase))
            {UpdateStatus="更新包大小或 SHA256 校验不匹配，已丢弃下载。";yield break;}
            // Only a fully received and verified .part file becomes a .zip.
            string error=null;
            try
            {
                if(new FileInfo(part).Length!=manifest.size)throw new IOException("文件大小发生变化。");
                if(File.Exists(destination))File.Replace(part,destination,null);
                else File.Move(part,destination);
                committed=true;
            }
            catch(Exception e){error=e.Message;}
            if(error!=null){UpdateStatus="无法保存更新包："+error;yield break;}
            DownloadedFilePath=destination;DownloadProgress=1;
            downloadedBuild=manifest.buildNumber;downloadedHash=manifest.sha256;
            UpdateStatus="更新包已下载并校验完成。"+UpgradeInstructions;
        }
        finally
        {
            if(!committed)TryDelete(part);
            activePartPath=null;IsBusy=false;
        }
    }

    public void SkipThisVersion()
    {
        if(IsBusy||available==null||available.buildNumber<=BuildNumber)return;
        PlayerPrefs.SetInt(PrefPrefix+"SkippedBuild",available.buildNumber);
        PlayerPrefs.SetString(PrefPrefix+"SkippedVersion",available.version);PlayerPrefs.Save();
        UpdateAvailable=false;UpdateStatus="已略过版本 "+available.version+"；手动检查时仍可查看。";
    }

    public void Cancel()
    {
        if(!IsBusy)return;
        cancelRequested=true;activeRequest?.Abort();UpdateStatus="正在取消…";
    }

    public void OpenDownloadFolder()
    {
        if(string.IsNullOrEmpty(DownloadedFilePath)||!File.Exists(DownloadedFilePath))
        {UpdateStatus="还没有已校验的更新包。";return;}
        try
        {
            // The path is locally constructed; no network value is used as a command.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo{
                FileName=Path.GetDirectoryName(DownloadedFilePath),UseShellExecute=true});
        }
        catch(Exception e){UpdateStatus="无法打开下载文件夹："+e.Message;}
    }

    public async System.Threading.Tasks.Task<bool> VerifyDownloadedPackage()
    {
        string path=DownloadedFilePath,expected=downloadedHash;
        long size=available?.size??0;
        if(string.IsNullOrEmpty(path)||string.IsNullOrEmpty(expected))return false;
        bool valid=await System.Threading.Tasks.Task.Run(()=>
        {
            try
            {
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                using(var hash=SHA256.Create())
                    return stream.Length==size&&string.Equals(BitConverter.ToString(hash.ComputeHash(stream)).Replace("-",""),expected,StringComparison.OrdinalIgnoreCase);
            }
            catch(Exception){return false;}
        });
        if(!valid){DownloadedFilePath="";UpdateStatus="更新包已变化或损坏，请重新下载。";}
        return valid;
    }

    bool TryReadSource(out string url,out string error)
    {
        url=null;error=null;
        try
        {
            string path=Path.Combine(Application.streamingAssetsPath,"update-source.json");
            if(!File.Exists(path)){error="尚未配置更新发布地址。";return false;}
            if(new FileInfo(path).Length>16*1024){error="更新地址配置文件过大。";return false;}
            var source=JsonUtility.FromJson<UpdateSource>(File.ReadAllText(path,Encoding.UTF8));
            url=source?.manifestUrl?.Trim();
            if(string.IsNullOrEmpty(url)){error="尚未配置更新发布地址。";return false;}
            if(!IsHttps(url)){error="更新发布地址必须是有效的 HTTPS 地址。";return false;}
            return true;
        }
        catch(Exception e){error="无法读取更新地址："+e.Message;return false;}
    }

    public static bool ValidateManifest(string json,out UpdateManifest manifest,out string error)
    {
        manifest=null;
        if(string.IsNullOrWhiteSpace(json)||json.Length>MaximumManifestSize)
        {error="信息为空或过大。";return false;}
        try{manifest=JsonUtility.FromJson<UpdateManifest>(json);}
        catch(Exception){error="JSON 格式错误。";return false;}
        return ValidateManifest(manifest,out error);
    }

    public static bool ValidateManifest(UpdateManifest manifest,out string error)
    {
        error=null;
        if(manifest==null||manifest.schema!=1){error="不支持的信息格式。";return false;}
        if(manifest.platform!="windows-x64"){error="更新包平台不匹配。";return false;}
        if(string.IsNullOrWhiteSpace(manifest.version)||manifest.version.Length>64||manifest.buildNumber<=0)
        {error="版本信息无效。";return false;}
        foreach(char c in manifest.version)if(char.IsControl(c)){error="版本名称含非法字符。";return false;}
        if(!IsHttps(manifest.url)){error="下载地址必须使用 HTTPS。";return false;}
        if(manifest.size<=0||manifest.size>MaximumDownloadSize){error="更新包大小必须在 1 字节至 1 GiB 之间。";return false;}
        if(manifest.sha256==null||manifest.sha256.Length!=64){error="缺少有效 SHA256。";return false;}
        foreach(char c in manifest.sha256)
            if(!((c>='0'&&c<='9')||(c>='a'&&c<='f')||(c>='A'&&c<='F')))
            {error="SHA256 必须是 64 位十六进制字符。";return false;}
        if(manifest.notes!=null&&manifest.notes.Length>8000){error="更新说明过长。";return false;}
        return true;
    }

    static bool IsHttps(string value)
        =>!string.IsNullOrEmpty(value)&&value.Length<=4096&&Uri.TryCreate(value,UriKind.Absolute,out Uri uri)
          &&uri.Scheme==Uri.UriSchemeHttps&&!string.IsNullOrEmpty(uri.Host)
          &&string.IsNullOrEmpty(uri.UserInfo)&&string.IsNullOrEmpty(uri.Fragment);

    IEnumerator Transfer(string url,string part,long limit,TransferResult result)
    {
        for(int redirect=0;redirect<=4;redirect++)
        {
            LimitedHandler handler=null;UnityWebRequest request=null;Stream stream=null;string creationError=null;
            try
            {
                stream=part==null?(Stream)new MemoryStream():new FileStream(part,FileMode.Create,FileAccess.Write,FileShare.None,64*1024);
                handler=new LimitedHandler(stream,limit,part!=null);
                request=new UnityWebRequest(url,UnityWebRequest.kHttpVerbGET){downloadHandler=handler,redirectLimit=0,timeout=part==null?30:600};
            }
            catch(Exception e){creationError=e.Message;}
            if(creationError!=null)
            {request?.Dispose();handler?.Close();try{stream?.Dispose();}catch(Exception){}result.error=creationError;yield break;}
            activeRequest=request;activeHandler=handler;
            try
            {
                UnityWebRequestAsyncOperation operation=null;string sendError=null;
                try{operation=request.SendWebRequest();}catch(Exception e){sendError=e.Message;}
                if(sendError!=null){result.error=sendError;yield break;}
                while(!operation.isDone)
                {
                    if(cancelRequested){request.Abort();result.cancelled=true;yield break;}
                    if(part!=null)
                    {
                        DownloadProgress=Mathf.Clamp01((float)((double)handler.Bytes/limit));
                        UpdateStatus="正在下载更新… "+Mathf.RoundToInt(DownloadProgress*100)+"%";
                    }
                    yield return null;
                }
                if(cancelRequested){result.cancelled=true;yield break;}
                long code=request.responseCode;
                if(code==301||code==302||code==303||code==307||code==308)
                {
                    string location=request.GetResponseHeader("Location");
                    if(redirect==4||!Uri.TryCreate(new Uri(url),location,out Uri next)||!IsHttps(next.AbsoluteUri))
                    {result.error="重定向过多或目标不是 HTTPS。";yield break;}
                    url=next.AbsoluteUri;continue;
                }
                if(request.result!=UnityWebRequest.Result.Success||handler.Error!=null)
                {result.error=handler.Error??("网络请求失败"+(code>0?"（HTTP "+code+"）":"："+request.error));yield break;}
                result.bytes=handler.Bytes;result.sha256=handler.Digest;
                if(part==null)result.data=handler.ReadMemory();
                yield break;
            }
            finally
            {
                request.Dispose();handler.Close();activeRequest=null;activeHandler=null;
            }
        }
    }

    // Unity invokes these callbacks on its main thread. Each chunk is bounded,
    // written and hashed immediately, so the entire ZIP is never held in RAM.
    sealed class LimitedHandler : DownloadHandlerScript
    {
        Stream stream;
        readonly long limit;
        SHA256 hash;
        public long Bytes { get; private set; }
        public string Digest { get; private set; }
        public string Error { get; private set; }
        public LimitedHandler(Stream destination,long maximum,bool hashing):base(new byte[64*1024])
        {stream=destination;limit=maximum;if(hashing)hash=SHA256.Create();}
        protected override bool ReceiveData(byte[] data,int dataLength)
        {
            if(dataLength==0)return true;
            if(data==null||stream==null||dataLength<0||dataLength>data.Length||Bytes>limit-dataLength)
            {Error="下载内容超过声明大小或接收数据异常。";return false;}
            try
            {
                stream.Write(data,0,dataLength);hash?.TransformBlock(data,0,dataLength,data,0);Bytes+=dataLength;return true;
            }
            catch(Exception e){Error="无法保存下载："+e.Message;return false;}
        }
        protected override void CompleteContent()
        {
            try
            {
                stream?.Flush();
                if(hash!=null){hash.TransformFinalBlock(Array.Empty<byte>(),0,0);Digest=BitConverter.ToString(hash.Hash).Replace("-","").ToLowerInvariant();}
            }
            catch(Exception e){Error="下载校验失败："+e.Message;}
        }
        public byte[] ReadMemory()=>((MemoryStream)stream).ToArray();
        public void Close()
        {
            try{stream?.Dispose();}catch(Exception){}
            stream=null;hash?.Dispose();hash=null;
        }
    }

    static void TryDelete(string path)
    {try{if(!string.IsNullOrEmpty(path)&&File.Exists(path))File.Delete(path);}catch(Exception){}}
    void OnDestroy()
    {
        cancelRequested=true;
        activeRequest?.Abort();activeRequest?.Dispose();activeRequest=null;
        activeHandler?.Close();activeHandler=null;
        TryDelete(activePartPath);
    }
}
