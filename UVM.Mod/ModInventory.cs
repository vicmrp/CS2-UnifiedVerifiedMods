using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Game.SceneFlow;
using Colossal.Mono.Cecil;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Uvm
{
    // Unity and ModManager are read only on the main thread. Disk work runs on a worker.
    internal sealed class InventoryPump : MonoBehaviour
    {
        internal ModInventory Inventory;
        float next;
        void Update(){if(Inventory!=null&&Time.realtimeSinceStartup>=next){next=Time.realtimeSinceStartup+5;Inventory.Refresh();}}
    }
    internal sealed class ModInventory : IDisposable
    {
        static readonly JObject EmptyRow=new JObject();
        volatile JObject snapshot;
        readonly string session=Guid.NewGuid().ToString("N");
        readonly int pid;
        readonly string started,image;
        readonly Func<JArray> results;
        readonly Func<string> scannedAt;
        int running,version;
        volatile bool stopped;
        DateTime nextDisk;
        JArray diskRows=new JArray();
        readonly Dictionary<string,string> icons=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,JArray> calls=new Dictionary<string,JArray>(StringComparer.OrdinalIgnoreCase);
        public int Version=>Volatile.Read(ref version);
        public JObject Snapshot=>(JObject)snapshot.DeepClone();
        public int Count=>((JArray)snapshot["mods"]).Count;
        public JObject Row(int index){var rows=(JArray)snapshot["mods"];return index>=0&&index<rows.Count?(JObject)rows[index]:EmptyRow;}
        public JObject[] Rows=>(snapshot["mods"] as JArray).OfType<JObject>().ToArray();
        public ModInventory(Func<JArray> results,Func<string> scannedAt)
        {
            this.results=results;this.scannedAt=scannedAt;
            using(var process=Process.GetCurrentProcess()){pid=process.Id;started=process.StartTime.ToUniversalTime().ToString("O");image=process.MainModule.FileName;}
            snapshot=Envelope(new JArray(),"Inventory is starting.");
        }
        JObject Envelope(JArray rows,string warning="")=>new JObject{{"schema",1},{"session",session},{"generated_at",DateTimeOffset.UtcNow.ToString("O")},{"uvm_version","0.5.0"},{"pid",pid},{"process_start",started},{"image",image},{"scan_at",scannedAt()},{"warning",warning},{"mods",rows},{"receipts",ObserveActivity.Snapshot()}};
        public void Invalidate(){nextDisk=DateTime.MinValue;}
        public void Refresh()
        {
            if(stopped||Interlocked.CompareExchange(ref running,1,0)!=0)return;
            var loaded=new JArray();
            try
            {
                var manager=GameManager.instance?.modManager;
                if(manager!=null)foreach(var mod in manager.Take(512))
                {
                    if(!mod.asset.isMod)continue;
                    var path=mod.asset.path;
                    if(string.IsNullOrWhiteSpace(path))path=mod.asset.assembly?.Location;
                    loaded.Add(new JObject{{"name",mod.asset.name},{"path",path??""},{"assembly_name",mod.asset.assembly?.FullName??""},{"assembly_mvid",mod.asset.assembly?.ManifestModule.ModuleVersionId.ToString("D")??""},{"state",mod.state.ToString()},{"loaded",mod.instances.Count>0},{"load_error",mod.loadError??""}});
                }
            }
            catch(Exception e){Mod.Log.Warn("UVM inventory: "+e.Message);}
            Task.Run(()=>
            {
                try
                {
                    if(DateTime.UtcNow>=nextDisk){diskRows=ReadPackages();nextDisk=DateTime.UtcNow.AddSeconds(30);}
                    var rows=(JArray)diskRows.DeepClone();var scan=results();
                    foreach(JObject row in rows)
                    {
                        var path=(string)row["path"];
                        var modules=loaded.OfType<JObject>().Where(m=>Inside((string)m["path"],path)).ToArray();
                        row["modules"]=new JArray(modules.Select(m=>m.DeepClone()));row["loaded"]=modules.Any(m=>(bool)m["loaded"]);
                        if(modules.Length>0)row["is_code"]=true;
                        row["availability"]=(bool)row["loaded"]?"Loaded in this game process":"Cached / local; load not observed";
                        var found=scan.OfType<JObject>().FirstOrDefault(r=>(string)r["folder"]==(string)row["folder"] && (string)row["mod_id"]!=null);
                        row["build"]=found?.DeepClone()??new JObject{{"status","NOT_SCANNED"},{"reason","No package scan evidence. Use Scan downloaded mods."}};
                    }
                    foreach(JObject mod in loaded)
                    {
                        if(rows.OfType<JObject>().Any(r=>Inside((string)mod["path"],(string)r["path"])))continue;
                        var folder=Path.GetDirectoryName((string)mod["path"]);
                        if(string.IsNullOrEmpty(folder))continue;
                        rows.Add(new JObject{{"key","loaded:"+(string)mod["path"]},{"name",mod["name"]},{"path",folder},{"folder",Path.GetFileName(folder)},{"is_code",true},{"loaded",mod["loaded"]},{"availability","Game mod manager"},{"modules",new JArray(mod.DeepClone())},{"icon",""},{"capabilities",Inspect((string)mod["path"])},{"build",new JObject{{"status","NOT_SCANNED"},{"reason","Local/other mod; no matching downloaded-package scan."}}}});
                    }
                    if(!stopped){snapshot=Envelope(rows);Interlocked.Increment(ref version);}
                }
                catch(Exception e){if(!stopped)snapshot=Envelope((JArray)snapshot["mods"].DeepClone(),"Inventory refresh incomplete: "+e.Message);}
                finally{Interlocked.Exchange(ref running,0);}
            });
        }
        JArray ReadPackages()
        {
            var rows=new JArray();var metadata=Scanner.ReadMetadata(Scanner.DefaultRoot);
            var local=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"AppData","LocalLow","Colossal Order","Cities Skylines II","Mods");
            foreach(var root in new[]{Scanner.DefaultRoot,local})
            {
                if(!Directory.Exists(root))continue;
                foreach(var folder in Directory.EnumerateDirectories(root).OrderBy(p=>p).Take(256))
                {
                    if(Path.GetFileName(folder).StartsWith(".")||(File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)continue;
                    var name=Path.GetFileName(folder);var row=new JObject{{"key",folder},{"path",folder},{"folder",name},{"name",name},{"icon",Icon(folder)}};
                    row["is_code"]=ModCodeClassifier.HasCode(folder);
                    if(root==Scanner.DefaultRoot)
                    {
                        Scanner.AddMetadata(row,name,metadata);if(row["paradox_revision"]!=null)row["mod_id"]=name.Split('_')[0];
                        var meta=metadata.OfType<JObject>().FirstOrDefault(m=>(string)m["id"]==(string)row["mod_id"] && (string)m["operatingSystem"]=="Windows");
                        row["description"]=Limit((string)meta?["shortDescription"],4000);
                        row["external_links"]=meta?["externalLinks"]?.DeepClone()??new JArray();
                    }
                    var capabilities=new JArray();
                    foreach(var dll in Directory.EnumerateFiles(folder,"*.dll").Take(32))foreach(var entry in Inspect(dll))capabilities.Add(entry.DeepClone());
                    row["capabilities"]=capabilities;rows.Add(row);
                }
            }
            return rows;
        }
        string Icon(string folder)
        {
            var path=Path.Combine(folder,".metadata","thumbnail.png");
            if(!File.Exists(path))path=Path.Combine(folder,"Thumbnail.png");
            if(!File.Exists(path))return "";
            var key=path+File.GetLastWriteTimeUtc(path).Ticks;
            if(icons.TryGetValue(key,out var cached))return cached;
            try
            {
                if(new FileInfo(path).Length>2*1024*1024||(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)return "";
                using(var source=System.Drawing.Image.FromFile(path))
                {
                    if(source.Width>4096||source.Height>4096)return "";
                    using(var thumb=new Bitmap(source,64,64))using(var bytes=new MemoryStream()){thumb.Save(bytes,ImageFormat.Png);return icons[key]="data:image/png;base64,"+Convert.ToBase64String(bytes.ToArray());}
                }
            }
            catch{return "";}
        }
        JArray Inspect(string path)
        {
            if(!File.Exists(path))return new JArray();var key=path+File.GetLastWriteTimeUtc(path).Ticks;
            if(calls.TryGetValue(key,out var cached))return cached;
            var found=new JArray();
            try
            {
                if(new FileInfo(path).Length>32*1024*1024||(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)return found;
                using(var module=ModuleDefinition.ReadModule(path,new ReaderParameters{ReadSymbols=false,ReadingMode=ReadingMode.Deferred}))
                {
                    var seen=new HashSet<string>();int budget=1000000;
                    foreach(var type in module.GetTypes())foreach(var method in type.Methods)
                    {
                        if(!method.HasBody)continue;
                        foreach(var instruction in method.Body.Instructions)
                        {
                            if(--budget<0)return found;
                            if(!(instruction.Operand is MethodReference reference))continue;
                            string api=reference.DeclaringType.FullName+"."+reference.Name,category=null;
                            if(api.StartsWith("System.Net.")||api.StartsWith("UnityEngine.Networking.UnityWebRequest"))category="network";
                            else if(api.StartsWith("System.IO.File.")||api.StartsWith("System.IO.Directory.")||api.StartsWith("System.IO.FileStream."))category="file";
                            else if(api.StartsWith("System.Diagnostics.Process.Start"))category="process";
                            if(category!=null&&seen.Add(api)&&found.Count<60)found.Add(new JObject{{"category",category},{"api",api},{"caller",Limit(method.FullName,256)},{"assembly",Path.GetFileName(path)},{"provenance","compiled call reference; execution not established"}});
                        }
                    }
                }
            }
            catch(Exception e){found.Add(new JObject{{"category","coverage"},{"assembly",Path.GetFileName(path)},{"api",(found.Count>0?"Inspection partial: ":"Inspection unavailable: ")+e.GetType().Name}});}
            return calls[key]=found;
        }
        internal static bool Inside(string file,string folder)=>!string.IsNullOrWhiteSpace(file)&&!string.IsNullOrWhiteSpace(folder)&&Path.GetFullPath(file).StartsWith(Path.GetFullPath(folder).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase);
        internal static string Limit(string value,int max)=>(value??"").Length>max?value.Substring(0,max):value??"";
        public void Dispose(){stopped=true;}
    }
}
