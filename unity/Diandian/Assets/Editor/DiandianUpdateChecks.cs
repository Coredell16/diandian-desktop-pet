using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
#endif

// Local fixtures only: no download, user data, Process.Start or real executable.
// The installer checks also run outside Unity without UNITY_EDITOR defined.
public static class DiandianUpdateChecks
{
    const string TemporaryPrefix="DiandianUpdateChecks-";
    const string FakeExecutable="Fixture only. This is not an executable.";
#if UNITY_EDITOR
    static bool running;

    [MenuItem("点点/开发/验证本地升级（不启动程序）")]
    public static async void Run()
    {
        if(running)return;
        running=true;
        try
        {
            int manifests=CheckManifests();
            int installations=await RunInstallerChecks();
            Debug.Log("DIANDIAN_UPDATE_CHECKS_OK manifests="+manifests+" installations="+installations+" processLaunches=0");
        }
        catch(Exception error){Debug.LogError("DIANDIAN_UPDATE_CHECKS_FAILED");Debug.LogException(error);}
        finally{running=false;}
    }

    static DiandianUpdater.UpdateManifest ValidManifest()=>new DiandianUpdater.UpdateManifest {
        schema=1,platform="windows-x64",version="2.0.0-test",buildNumber=20002,
        url="https://updates.example.test/diandian.zip",size=123,sha256=new string('a',64),notes="测试"
    };

    public static int CheckManifests()
    {
        Assert(DiandianUpdater.ValidateManifest(ValidManifest(),out _),"有效 manifest 被拒绝。");
        Assert(DiandianUpdater.ValidateManifest(JsonUtility.ToJson(ValidManifest()),out var parsed,out _)
            &&parsed.buildNumber==20002,"manifest JSON 往返失败。");
        Assert(!DiandianUpdater.ValidateManifest("{broken",out _,out _),"损坏 JSON 未拒绝。");
        Assert(!DiandianUpdater.ValidateManifest(new string(' ',65537),out _,out _),"过大 JSON 未拒绝。");
        Assert(!DiandianUpdater.ValidateManifest((DiandianUpdater.UpdateManifest)null,out _),"空 manifest 未拒绝。");
        var invalid=new (string name,Action<DiandianUpdater.UpdateManifest> edit)[] {
            ("schema",m=>m.schema=2),("platform",m=>m.platform="linux-x64"),
            ("build",m=>m.buildNumber=0),("version",m=>m.version="bad\nversion"),
            ("HTTP",m=>m.url="http://updates.example.test/update.zip"),
            ("credentials",m=>m.url="https://user:pass@updates.example.test/update.zip"),
            ("fragment",m=>m.url="https://updates.example.test/update.zip#fragment"),
            ("empty size",m=>m.size=0),("oversize",m=>m.size=DiandianUpdater.MaximumDownloadSize+1),
            ("short hash",m=>m.sha256="abcd"),("nonhex hash",m=>m.sha256=new string('g',64)),
            ("notes",m=>m.notes=new string('x',8001))
        };
        foreach(var item in invalid)
        {
            var manifest=ValidManifest();item.edit(manifest);
            Assert(!DiandianUpdater.ValidateManifest(manifest,out _),"无效 manifest 未拒绝："+item.name);
        }
        return 5+invalid.Length;
    }
#endif

    public static async Task<int> RunInstallerChecks(string temporaryRoot=null)
    {
        string temporary=Path.GetFullPath(temporaryRoot??Path.GetTempPath());
        string fixture=Path.Combine(temporary,TemporaryPrefix+Guid.NewGuid().ToString("N"));
        string root=Path.Combine(fixture,"data");
        string packages=Path.Combine(fixture,"packages");
        int passed=0;
        try
        {
            Directory.CreateDirectory(packages);
            Directory.CreateDirectory(Path.Combine(root,"Versions","7"));
            File.WriteAllText(Path.Combine(root,"settings.json"),"keep settings");
            File.WriteAllText(Path.Combine(root,"Versions","7","keep.txt"),"keep old version");
            File.WriteAllText(Path.Combine(fixture,"escape.txt"),"outside sentinel");
            string good=MakeZip(packages,"good",z=>Add(z,"点点_Data/fixture.txt","data marker"));
            string executable=await DiandianUpdateInstaller.InstallAsync(good,100,root);
            Assert(executable==Path.Combine(root,"Versions","100","点点.exe"),"安装返回路径不正确。");
            Assert(File.ReadAllText(executable)==FakeExecutable,"exe 内容未完整保留。");
            Assert(File.ReadAllText(Path.Combine(root,"Versions","100","点点_Data","fixture.txt"))=="data marker","数据目录未解压。");
            Assert(ReadPointer(root)=="{\"buildNumber\":100}","首次版本指针不正确。");passed++;
            await DiandianUpdateInstaller.InstallAsync(good,101,root);
            Assert(ReadPointer(root)=="{\"buildNumber\":101}"&&File.Exists(executable),"原子替换指针或保留旧版失败。");passed++;
            await Reject(good,100,root,"existing version",true);passed++;
            await Reject(good,0,root,"nonpositive build");passed++;

            int build=200;
            string[] unsafePaths={"../escape.txt","..\\escape.txt","../../../escape.txt","folder/../../escape.txt",
                "/absolute.txt","C:/absolute.txt",Path.Combine(fixture,"escape.txt").Replace('\\','/'),
                "file.txt:stream","NUL.txt","COM1 .txt","trailing.","trailing ","./relative.txt"};
            foreach(string path in unsafePaths)
            {
                string zip=MakeZip(packages,"unsafe-"+build,z=>Add(z,path));
                await Reject(zip,build++,root,path);passed++;
            }
            var badPackages=new (string name,Action<ZipArchive> extra)[] {
                ("duplicate",z=>{Add(z,"payload/a.txt");Add(z,"PAYLOAD/A.TXT");}),
                ("separator-duplicate",z=>{Add(z,"payload/a.txt");Add(z,"payload\\a.txt");}),
                ("directory-duplicate",z=>{Add(z,"folder/");Add(z,"FOLDER/");}),
                ("parent-file",z=>{Add(z,"folder");Add(z,"folder/child.txt");}),
                ("symlink",z=>Add(z,"link","../outside",unchecked((int)0xA1FF0000))),
                ("reparse",z=>Add(z,"link","../outside",(int)FileAttributes.ReparsePoint)),
                ("entry-limit",z=>{for(int i=0;i<10000;i++)Add(z,"f"+i,"");})
            };
            foreach(var item in badPackages)
            {
                string zip=MakeZip(packages,item.name,item.extra);
                await Reject(zip,build++,root,item.name);passed++;
            }
            string missing=MakeZip(packages,"missing-exe",z=>Add(z,"点点_Data/file.txt"),false);
            await Reject(missing,build++,root,"missing exe");passed++;
            string empty=MakeZip(packages,"empty-exe",z=>Add(z,"点点.exe",""),false);
            await Reject(empty,build++,root,"empty exe");passed++;
            string corrupt=Path.Combine(packages,"corrupt.zip");File.WriteAllText(corrupt,"not a ZIP");
            await Reject(corrupt,build++,root,"corrupt ZIP");passed++;
            string truncated=Path.Combine(packages,"truncated.zip");
            byte[] shortened=File.ReadAllBytes(good);Array.Resize(ref shortened,shortened.Length-12);File.WriteAllBytes(truncated,shortened);
            await Reject(truncated,build++,root,"truncated ZIP");passed++;
            string oversized=MakeZip(packages,"expanded-limit");PatchDeclaredLength(oversized,0x80000001u);
            await Reject(oversized,build++,root,"expanded size limit");passed++;
            string understated=MakeZip(packages,"understated-size");PatchDeclaredLength(understated,1);
            await Reject(understated,build++,root,"actual size exceeds declared size");passed++;

            // Simulate a pointer commit failure without changing a real user's pointer.
            string blocked=Path.Combine(fixture,"blocked");
            Directory.CreateDirectory(Path.Combine(blocked,"current-version.json"));
            bool failed=false;
            try{await DiandianUpdateInstaller.InstallAsync(good,300,blocked);}
            catch(IOException){failed=true;}
            Assert(failed&&File.Exists(Path.Combine(blocked,"Versions","300","点点.exe")),"指针失败后未保留已提交版本。");
            Assert(Directory.GetDirectories(Path.Combine(blocked,"Versions"),".stage-*").Length==0,"指针失败残留 stage。");passed++;

            Assert(File.ReadAllText(Path.Combine(root,"settings.json"))=="keep settings","设置被修改。");
            Assert(File.ReadAllText(Path.Combine(root,"Versions","7","keep.txt"))=="keep old version","旧版被修改。");
            Assert(File.ReadAllText(Path.Combine(fixture,"escape.txt"))=="outside sentinel","ZipSlip 越界写入。");
            Assert(ReadPointer(root)=="{\"buildNumber\":101}","失败安装改变了版本指针。");
            return passed;
        }
        finally{RemoveFixture(fixture,temporary);}
    }

    static async Task Reject(string zip,int build,string root,string name,bool existing=false)
    {
        string before=ReadPointer(root);bool rejected=false;
        try{await DiandianUpdateInstaller.InstallAsync(zip,build,root);}
        catch(Exception error) when(error is IOException||error is InvalidDataException||error is ArgumentException)
        {rejected=true;}
        Assert(rejected,"危险测试包未被拒绝："+name);
        Assert(ReadPointer(root)==before,"失败改变指针："+name);
        string versions=Path.Combine(root,"Versions");
        Assert(Directory.GetDirectories(versions,".stage-*").Length==0,"失败留下 stage："+name);
        Assert(existing||!Directory.Exists(Path.Combine(versions,build.ToString(System.Globalization.CultureInfo.InvariantCulture))),"失败提交了版本："+name);
    }

    static string ReadPointer(string root)=>File.ReadAllText(Path.Combine(root,"current-version.json")).Trim();

    static string MakeZip(string packages,string name,Action<ZipArchive> extra=null,bool executable=true)
    {
        string path=Path.Combine(packages,name+".zip");
        using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        using(var archive=new ZipArchive(file,ZipArchiveMode.Create))
        {if(executable)Add(archive,"点点.exe",FakeExecutable);extra?.Invoke(archive);}
        return path;
    }

    static void Add(ZipArchive archive,string name,string text="fixture",int attributes=0)
    {
        var entry=archive.CreateEntry(name,System.IO.Compression.CompressionLevel.NoCompression);
        if(attributes!=0)entry.ExternalAttributes=attributes;
        if(name.EndsWith("/",StringComparison.Ordinal))return;
        using(var writer=new StreamWriter(entry.Open(),new UTF8Encoding(false)))writer.Write(text);
    }

    // Patch only the central directory size of a tiny fixture; never allocate GiB.
    static void PatchDeclaredLength(string path,uint length)
    {
        byte[] bytes=File.ReadAllBytes(path);
        for(int i=0;i+28<=bytes.Length;i++)
            if(bytes[i]==0x50&&bytes[i+1]==0x4b&&bytes[i+2]==0x01&&bytes[i+3]==0x02)
            {
                for(int b=0;b<4;b++)bytes[i+24+b]=(byte)(length>>(b*8));
                File.WriteAllBytes(path,bytes);return;
            }
        throw new InvalidDataException("测试 ZIP 缺少 central directory。");
    }

    static void RemoveFixture(string fixture,string temporaryRoot)
    {
        string full=Path.GetFullPath(fixture);
        string temporary=Path.GetFullPath(temporaryRoot).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        string name=Path.GetFileName(full);
        if(!string.Equals(Path.GetDirectoryName(full),temporary,StringComparison.OrdinalIgnoreCase)
            ||!name.StartsWith(TemporaryPrefix,StringComparison.Ordinal)||!Guid.TryParseExact(name.Substring(TemporaryPrefix.Length),"N",out _))
            throw new IOException("拒绝清理非测试临时目录。");
        if(!Directory.Exists(full))return;
        var pending=new System.Collections.Generic.Stack<string>();pending.Push(full);
        while(pending.Count>0)
        {
            string directory=pending.Pop();
            if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new IOException("测试目录出现重解析点，保留供检查。");
            foreach(string child in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes=File.GetAttributes(child);
                if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("测试目录出现重解析点，保留供检查。");
                if((attributes&FileAttributes.Directory)!=0)pending.Push(child);
            }
        }
        Directory.Delete(full,true);
    }

    static void Assert(bool condition,string message)
    {if(!condition)throw new InvalidOperationException(message);}
}
