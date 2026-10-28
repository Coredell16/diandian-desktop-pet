using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

// InstallAsync and LaunchInstalled are only called after the user clicks
// "安装并重启". Downloading or checking an update must never call them.
public static class DiandianUpdateInstaller
{
    const string ExecutableName="点点.exe";
    const string PointerName="current-version.json";
    const int MaximumEntries=10000;
    const long MaximumExpandedSize=2L*1024*1024*1024;
    static readonly object LaunchLock=new object();
    static readonly HashSet<string> InstalledHere=new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static async Task<string> InstallAsync(string zipPath,int buildNumber,string persistentRoot)
    {
        if(buildNumber<=0)throw new ArgumentOutOfRangeException(nameof(buildNumber));
        string root=RootPath(persistentRoot);
        string archive=Path.GetFullPath(zipPath);
        // No Unity API, including JsonUtility, runs on the extraction thread.
        string executable=await Task.Run(()=>Install(archive,buildNumber,root));
        lock(LaunchLock)InstalledHere.Add(executable);
        return executable;
    }

    static string Install(string zipPath,int buildNumber,string root)
    {
        string versions=Path.Combine(root,"Versions");
        string destination=VersionDirectory(root,buildNumber);
        RequirePlainPath(versions);
        if(Directory.Exists(destination)||File.Exists(destination))
            throw new IOException("此版本目录已存在，已保留原目录。"+destination);
        Directory.CreateDirectory(versions);
        string stage=Path.Combine(versions,".stage-"+Guid.NewGuid().ToString("N"));
        bool ownsStage=false;
        try
        {
            if(Directory.Exists(stage)||File.Exists(stage))throw new IOException("临时目录已存在。");
            Directory.CreateDirectory(stage);ownsStage=true;
            using(var source=new FileStream(zipPath,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var archive=new ZipArchive(source,ZipArchiveMode.Read))
            {
                if(archive.Entries.Count==0||archive.Entries.Count>MaximumEntries)
                    throw new InvalidDataException("更新包为空或条目数超过 10000。");
                long declared=0;
                var paths=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
                foreach(var entry in archive.Entries)
                {
                    string target=EntryPath(entry,stage,out bool directory);
                    if(paths.ContainsKey(target))throw new InvalidDataException("更新包含重复路径。");
                    paths.Add(target,directory);
                    if(entry.Length<0||entry.Length>MaximumExpandedSize-declared)
                        throw new InvalidDataException("更新包解压大小超过 2 GiB。");
                    declared+=entry.Length;
                    if(directory&&entry.Length!=0)throw new InvalidDataException("目录条目含文件数据。");
                }
                foreach(var pair in paths)
                    for(string parent=Path.GetDirectoryName(pair.Key);!string.Equals(parent,stage,StringComparison.OrdinalIgnoreCase);parent=Path.GetDirectoryName(parent))
                        if(paths.TryGetValue(parent,out bool directory)&&!directory)
                            throw new InvalidDataException("更新包的文件与目录路径冲突。");
                long written=0;
                var buffer=new byte[64*1024];
                foreach(var entry in archive.Entries)
                {
                    string target=EntryPath(entry,stage,out bool directory);
                    RequirePlainPath(target);
                    if(directory){Directory.CreateDirectory(target);continue;}
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    long entryWritten=0;
                    using(var input=entry.Open())
                    using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                    {
                        int count;
                        while((count=input.Read(buffer,0,buffer.Length))>0)
                        {
                            if(count>MaximumExpandedSize-written||count>entry.Length-entryWritten)
                                throw new InvalidDataException("解压数据超过声明大小。");
                            output.Write(buffer,0,count);written+=count;entryWritten+=count;
                        }
                    }
                    if(entryWritten!=entry.Length)throw new InvalidDataException("更新包文件不完整。");
                }
            }
            RequireExecutable(Path.Combine(stage,ExecutableName));
            RequirePlainPath(destination);
            Directory.Move(stage,destination);ownsStage=false;
            string executable=Path.Combine(destination,ExecutableName);
            RequireExecutable(executable);
            WritePointer(root,buildNumber);
            return executable;
        }
        finally
        {
            // Only this invocation's unique staging directory can be removed.
            // A committed version is retained even if writing the pointer fails.
            if(ownsStage)
            {
                try
                {
                    if(!stage.StartsWith(versions+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                        throw new IOException("临时目录超出版本目录。");
                    RequirePlainPath(stage);
                    if(Directory.Exists(stage))Directory.Delete(stage,true);
                }
                catch(Exception) { }
            }
        }
    }

    static string EntryPath(ZipArchiveEntry entry,string stage,out bool directory)
    {
        int attributes=entry.ExternalAttributes;
        int unixType=(attributes>>16)&0xF000;
        if(unixType==0xA000||(attributes&(int)FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("更新包不能包含符号链接或重解析点。");
        if(unixType!=0&&unixType!=0x8000&&unixType!=0x4000)
            throw new InvalidDataException("更新包含不支持的文件类型。");
        string name=entry.FullName.Replace('\\','/');
        directory=name.EndsWith("/",StringComparison.Ordinal)||(attributes&(int)FileAttributes.Directory)!=0||unixType==0x4000;
        if(string.IsNullOrEmpty(name)||name.StartsWith("/",StringComparison.Ordinal)||Path.IsPathRooted(name))
            throw new InvalidDataException("更新包含绝对路径或空路径。");
        if(name.EndsWith("/",StringComparison.Ordinal))name=name.Substring(0,name.Length-1);
        foreach(string part in name.Split('/'))
        {
            if(part.Length==0||part=="."||part==".."||part.EndsWith(".",StringComparison.Ordinal)||part.EndsWith(" ",StringComparison.Ordinal))
                throw new InvalidDataException("更新包含不安全的路径片段。");
            foreach(char c in part)
                if(c<32||"<>:\"|?*".IndexOf(c)>=0)
                    throw new InvalidDataException("更新包路径含 Windows 非法字符或 ADS。");
            string stem=part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if(stem=="CON"||stem=="PRN"||stem=="AUX"||stem=="NUL"||stem=="CONIN$"||stem=="CONOUT$"||stem=="CLOCK$"
                ||(stem.Length==4&&(stem.StartsWith("COM",StringComparison.Ordinal)||stem.StartsWith("LPT",StringComparison.Ordinal))&&"123456789¹²³".IndexOf(stem[3])>=0))
                throw new InvalidDataException("更新包含 Windows 设备名称。");
        }
        string target=Path.GetFullPath(Path.Combine(stage,name.Replace('/',Path.DirectorySeparatorChar)));
        if(!target.StartsWith(stage+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包路径超出安装目录。");
        return target;
    }

    public static bool TryLaunchNewer(int currentBuild,string persistentRoot)
    {
        if(currentBuild<0)return false;
        try
        {
            string root=RootPath(persistentRoot);
            string pointer=Path.Combine(root,PointerName);
            RequirePlainPath(pointer);
            if(!File.Exists(pointer))return false;
            string json;
            using(var reader=new StreamReader(pointer,Encoding.UTF8,true))
            {
                var buffer=new char[1025];int count=reader.ReadBlock(buffer,0,buffer.Length);
                if(count>1024)return false;
                json=new string(buffer,0,count);
            }
            // Deliberately accept only this tiny JSON schema: no executable path,
            // additional fields, duplicate keys, exponent or quoted integer.
            var match=Regex.Match(json,"\\A[ \\t\\r\\n]*\\{[ \\t\\r\\n]*\"buildNumber\"[ \\t\\r\\n]*:[ \\t\\r\\n]*([1-9][0-9]*)[ \\t\\r\\n]*\\}[ \\t\\r\\n]*\\z",RegexOptions.CultureInvariant);
            if(!match.Success||!int.TryParse(match.Groups[1].Value,NumberStyles.None,CultureInfo.InvariantCulture,out int build)||build<=currentBuild)
                return false;
            string executable=Path.Combine(VersionDirectory(root,build),ExecutableName);
            // A mistakenly republished older binary can carry a lower build
            // constant than the pointer. Never restart the same executable.
            using(var current=Process.GetCurrentProcess())
                if(string.Equals(Path.GetFullPath(current.MainModule.FileName),executable,StringComparison.OrdinalIgnoreCase))
                    return false;
            StartExecutable(executable);
            return true;
        }
        catch(Exception){return false;} // A broken pointer must leave the old app usable.
    }

    // The caller quits the old app only after this explicit user action succeeds.
    public static void LaunchInstalled(string exe)
    {
        string executable=Path.GetFullPath(exe);
        lock(LaunchLock)
            if(!InstalledHere.Contains(executable))throw new InvalidOperationException("只能启动本次已验证安装的点点版本。");
        StartExecutable(executable);
    }

    static void StartExecutable(string executable)
    {
        RequireExecutable(executable);
        using(var process=Process.Start(new ProcessStartInfo {
            FileName=executable,WorkingDirectory=Path.GetDirectoryName(executable),UseShellExecute=false
        }))
            if(process==null)throw new IOException("无法启动新版点点。");
    }

    static void RequireExecutable(string executable)
    {
        RequirePlainPath(executable);
        if(!File.Exists(executable)||new FileInfo(executable).Length==0)
            throw new InvalidDataException("更新包根目录缺少有效的点点.exe。");
    }

    static string RootPath(string persistentRoot)
    {
        if(string.IsNullOrWhiteSpace(persistentRoot))throw new ArgumentException("用户数据目录为空。",nameof(persistentRoot));
        string root=Path.GetFullPath(persistentRoot).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        if(string.IsNullOrEmpty(root)||string.Equals(root,Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar),StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("不能把磁盘根目录用作用户数据目录。",nameof(persistentRoot));
        RequirePlainPath(root);return root;
    }

    static string VersionDirectory(string root,int build)
        =>Path.Combine(root,"Versions",build.ToString(CultureInfo.InvariantCulture));

    static void RequirePlainPath(string path)
    {
        for(string current=path;!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
        {
            try
            {
                if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                    throw new IOException("安装路径不能经过符号链接或重解析点。");
            }
            catch(FileNotFoundException) { }
            catch(DirectoryNotFoundException) { }
        }
    }

    static void WritePointer(string root,int build)
    {
        string pointer=Path.Combine(root,PointerName);
        string temporary=Path.Combine(root,PointerName+"."+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            RequirePlainPath(pointer);
            byte[] json=new UTF8Encoding(false).GetBytes("{\"buildNumber\":"+build.ToString(CultureInfo.InvariantCulture)+"}\n");
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {output.Write(json,0,json.Length);output.Flush(true);}
            if(File.Exists(pointer))File.Replace(temporary,pointer,null);
            else File.Move(temporary,pointer);
        }
        finally
        {
            try{if(File.Exists(temporary))File.Delete(temporary);}catch(Exception) { }
        }
    }
}
