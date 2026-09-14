#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Uvm
{
    public static class Scanner
    {
        public static readonly HashSet<string> CodeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".dll", ".exe", ".so", ".bundle", ".js", ".mjs", ".cjs", ".wasm" };
        public const string Origin = "https://vezit.net";
        static readonly HttpClient Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };

        public static string Sha(byte[] bytes) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        public static string FileHash(string path) { using (var input = File.OpenRead(path)) using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant(); }
        public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Colossal Order", "Cities Skylines II", ".cache", "Mods", "pdx_mods");

        public static string SafePath(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || relative.Length > 240 || !Regex.IsMatch(relative, @"^[A-Za-z0-9_. /+@()-]+$") || relative.StartsWith("/") || relative.Split('/').Any(x=>x=="" || x=="." || x==".." || x.EndsWith(" ") || x.EndsWith("."))) throw new InvalidDataException("Unsafe package path: " + relative);
            string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path leaves the package");
            return full;
        }

        public static SortedDictionary<string,string> HashFolder(string root, CancellationToken cancellation = default(CancellationToken))
        {
            root = Path.GetFullPath(root);
            var files = new SortedDictionary<string,string>(StringComparer.Ordinal);
            var pending = new SortedDictionary<string,string>(StringComparer.Ordinal);
            var directories = new Stack<string>(); directories.Push(root);
            while (directories.Count>0)
            {
                cancellation.ThrowIfCancellationRequested(); var dir = directories.Pop();
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Linked folders are not supported.");
                foreach (var child in Directory.GetDirectories(dir))
                    if (!new[]{".metadata", ".cpatch", ".git"}.Contains(Path.GetFileName(child))) directories.Push(child);
                foreach (var file in Directory.GetFiles(dir))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if ((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Linked files are not supported.");
                    if (Path.GetExtension(file).Equals(".pdb",StringComparison.OrdinalIgnoreCase) || Path.GetExtension(file).Equals(".cid",StringComparison.OrdinalIgnoreCase)) continue;
                    var relative=file.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length+1).Replace('\\','/').ToLowerInvariant();
                    SafePath(root,relative);
                    if (pending.Count>=256) throw new InvalidDataException("Package exceeds 256 files.");
                    if (pending.ContainsKey(relative)) throw new InvalidDataException("Duplicate package path.");
                    pending.Add(relative,file);
                }
            }
            if (!pending.Keys.Any(p=>CodeExtensions.Contains(Path.GetExtension(p)))) throw new InvalidDataException("No supported code files in this package.");
            foreach(var item in pending){cancellation.ThrowIfCancellationRequested();files.Add(item.Key,FileHash(item.Value));}
            return files;
        }

        public static async Task<JObject> Api(string origin, string path, object body = null, CancellationToken cancellation = default(CancellationToken))
        {
            var uri=new Uri(origin.TrimEnd('/')+path);
            if (uri.Scheme!="https" && !(uri.IsLoopback && uri.Scheme=="http")) throw new InvalidOperationException("Registry requires HTTPS.");
            using(var request=new HttpRequestMessage(body==null?HttpMethod.Get:HttpMethod.Post,uri))
            {
                request.Headers.UserAgent.ParseAdd("UVM/0.3.0");
                if(body!=null) request.Content=new StringContent(JsonConvert.SerializeObject(body),Encoding.UTF8,"application/json");
                using(var response=await Http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellation).ConfigureAwait(false))
                using(var stream=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using(var buffer=new MemoryStream())
                {
                    var chunk=new byte[8192];int read;
                    while((read=await stream.ReadAsync(chunk,0,chunk.Length,cancellation).ConfigureAwait(false))>0)
                    {if(buffer.Length+read>4*1024*1024)throw new InvalidDataException("Registry response too large.");buffer.Write(chunk,0,read);}
                    var text=Encoding.UTF8.GetString(buffer.ToArray());
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Registry returned HTTP "+(int)response.StatusCode+": "+text.Substring(0,Math.Min(500,text.Length)));
                    return JObject.Parse(text);
                }
            }
        }

        public static bool VerifyEvidence(JObject evidence, string origin)
        {
            try
            {
                var key=(JObject)evidence["key"]; var raw=Convert.FromBase64String((string)key["public_key"]);
                if(raw.Length!=65 || raw[0]!=4 || Sha(raw)!=(string)key["id"] || key["revoked_at"].Type!=JTokenType.Null)return false;
                var payload=Convert.FromBase64String((string)evidence["payload"]);var signature=Convert.FromBase64String((string)evidence["signature"]);
                var body=JObject.Parse(Encoding.UTF8.GetString(payload),new JsonLoadSettings{DuplicatePropertyNameHandling=DuplicatePropertyNameHandling.Error});
                if((int?)body["schema"]!=1 || (string)body["registry"]!=origin || (string)body["purpose"]!="attestation" || (string)body["key_id"]!=(string)key["id"] || (string)body["result"]!=(string)evidence["result"])return false;
#if NET48
                var blob=new byte[72];Array.Copy(BitConverter.GetBytes(0x31534345),0,blob,0,4);Array.Copy(BitConverter.GetBytes(32),0,blob,4,4);Array.Copy(raw,1,blob,8,64);
                // Call the Windows crypto provider directly: Unity's Mono may not implement ECDsaCng.
                IntPtr algorithm=IntPtr.Zero, imported=IntPtr.Zero;
                try
                {
                    if(BCryptOpenAlgorithmProvider(out algorithm,"ECDSA_P256",null,0)!=0)return false;
                    if(BCryptImportKeyPair(algorithm,IntPtr.Zero,"ECCPUBLICBLOB",out imported,blob,blob.Length,0)!=0)return false;
                    using(var hash=SHA256.Create()){var digest=hash.ComputeHash(payload);return BCryptVerifySignature(imported,IntPtr.Zero,digest,digest.Length,signature,signature.Length,0)==0;}
                }
                finally{if(imported!=IntPtr.Zero)BCryptDestroyKey(imported);if(algorithm!=IntPtr.Zero)BCryptCloseAlgorithmProvider(algorithm,0);}
#else
                using(var ecdsa=ECDsa.Create(new ECParameters{Curve=ECCurve.NamedCurves.nistP256,Q=new ECPoint{X=raw.Skip(1).Take(32).ToArray(),Y=raw.Skip(33).Take(32).ToArray()}}))return ecdsa.VerifyData(payload,signature,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
#endif
            }
            catch { return false; }
        }

#if NET48
        [System.Runtime.InteropServices.DllImport("bcrypt.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int BCryptOpenAlgorithmProvider(out IntPtr handle,string algorithm,string implementation,int flags);
        [System.Runtime.InteropServices.DllImport("bcrypt.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int BCryptImportKeyPair(IntPtr algorithm,IntPtr importKey,string type,out IntPtr key,byte[] input,int length,int flags);
        [System.Runtime.InteropServices.DllImport("bcrypt.dll")]
        static extern int BCryptVerifySignature(IntPtr key,IntPtr padding,byte[] hash,int hashLength,byte[] signature,int signatureLength,int flags);
        [System.Runtime.InteropServices.DllImport("bcrypt.dll")] static extern int BCryptDestroyKey(IntPtr key);
        [System.Runtime.InteropServices.DllImport("bcrypt.dll")] static extern int BCryptCloseAlgorithmProvider(IntPtr algorithm,int flags);
#endif

        public static async Task<JObject> Lookup(string origin,string modId,SortedDictionary<string,string> files,CancellationToken cancellation=default(CancellationToken))
        {
            var result=await Api(origin,"/api/v1/lookup",new{mod_id=modId,files},cancellation).ConfigureAwait(false);
            // A green result must contain a valid, unrevoked signed matching build for the returned manifest.
            if ((string)result["status"]=="REPRODUCED")
            {
                var verifiers=new SortedDictionary<string,string>(StringComparer.Ordinal);
                foreach(JObject release in (JArray)result["results"])
                {
                    if((string)release["status"]!="REPRODUCED")continue;
                    var response=await Api(origin,"/api/v1/releases/"+Uri.EscapeDataString((string)release["id"])+"/attestations",null,cancellation).ConfigureAwait(false);
                    foreach(JObject evidence in (JArray)response["results"])
                    {
                        if(!MatchesEvidence(evidence,origin,modId,release["manifest"],files))continue;
                        var github=evidence["key"]?["github"];
                        string account=(string)github?["id"],login=(string)github?["login"];
                        if(!string.IsNullOrEmpty(account) && !string.IsNullOrEmpty(login))verifiers[account]=login;
                    }
                }
                result["verified_by"]=new JArray(verifiers.Values.Distinct(StringComparer.OrdinalIgnoreCase));
                result["matching_accounts"]=verifiers.Count;
                if(verifiers.Count==0){result["status"]="UNVERIFIED";result["reason"]="Registry claim has no valid matching signature and GitHub identity.";}
            }
            return result;
        }

        public static bool MatchesEvidence(JObject evidence,string origin,string modId,JToken releaseManifest,SortedDictionary<string,string> files)
        {
            try
            {
                if((string)evidence["result"]!="reproduced" || !VerifyEvidence(evidence,origin))return false;
                var payload=JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String((string)evidence["payload"])));
                var manifest=(JObject)payload["manifest"];var expected=manifest["files"].ToObject<SortedDictionary<string,string>>();
                return (string)manifest["platform"]=="paradox" && (string)manifest["mod_id"]==modId &&
                    JToken.DeepEquals(manifest,releaseManifest) && JToken.DeepEquals(manifest["files"],payload["built_files"]) &&
                    expected.Count>0 && expected.Keys.Any(p=>CodeExtensions.Contains(Path.GetExtension(p))) &&
                    expected.All(p=>files.TryGetValue(p.Key,out var sha)&&sha==p.Value) &&
                    files.Keys.Where(p=>CodeExtensions.Contains(Path.GetExtension(p))).All(expected.ContainsKey);
            }
            catch{return false;}
        }

        public static async Task<JArray> Scan(string root, string origin, Action<string> progress=null,CancellationToken cancellation=default(CancellationToken))
        {
            var results=new JArray();
            var metadata=ReadMetadata(root);
            foreach(var folder in Directory.GetDirectories(root).OrderBy(p=>p,StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested();var name=Path.GetFileName(folder);var match=Regex.Match(name,@"^([1-9][0-9]{0,18})_[0-9]+$");
                if(!match.Success)continue;var id=match.Groups[1].Value;progress?.Invoke("Checking Paradox mod "+id+"…");
                JObject result;
                try
                {
                    var files=HashFolder(folder,cancellation);
                    result=await Lookup(origin,id,files,cancellation).ConfigureAwait(false);
                    result["content_sha256"]=Sha(Encoding.UTF8.GetBytes(string.Concat(files.Select(p=>p.Key+"\0"+p.Value+"\n"))));
                    result["hashed_files"]=files.Count;
                }
                catch(OperationCanceledException){throw;}
                catch(Exception e){result=new JObject{{"status","UNAVAILABLE"},{"reason",e.Message}};}
                result["mod_id"]=id;result["folder"]=name;
                AddMetadata(result,name,metadata);
                results.Add(result);
            }
            return results;
        }

        // Paradox's own local metadata avoids one extra network request per package.
        // Names and version labels are descriptive only; they never grant verification.
        public static JArray ReadMetadata(string root)
        {
            try
            {
                var path=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(root)),"pdx_mods_cache.json");
                if(!File.Exists(path) || new FileInfo(path).Length>32*1024*1024 || (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)return new JArray();
                using(var stream=File.OpenRead(path))
                using(var reader=new StreamReader(stream))
                using(var json=new JsonTextReader(reader){MaxDepth=32,DateParseHandling=DateParseHandling.None})
                    return JObject.Load(json)["items"] as JArray??new JArray();
            }
            catch{return new JArray();}
        }

        public static void AddMetadata(JObject result,string folder,JArray metadata)
        {
            var match=Regex.Match(folder,@"^([1-9][0-9]{0,18})_([0-9]+)$");
            if(!match.Success)return;
            string id=match.Groups[1].Value,revision=match.Groups[2].Value;
            result["paradox_revision"]=revision;
            var candidates=metadata.OfType<JObject>().Where(m=>(string)m["id"]==id &&
                ((string)m["operatingSystem"]=="Windows" || m["operatingSystem"]==null)).ToArray();
            var item=candidates.FirstOrDefault(m=>(string)m["version"]==revision)??candidates.FirstOrDefault();
            result["name"]="Paradox mod "+id;
            if(item==null)return;
            string name=(string)item["displayName"];
            if(!string.IsNullOrWhiteSpace(name))result["name"]=name.Length>160?name.Substring(0,160):name;
            result["metadata_source"]="Paradox game cache";
            result["metadata_cached_at"]=item["timestamp"]?.DeepClone();
            var entry=(item["changelog"] as JArray)?.OfType<JObject>().FirstOrDefault(c=>(string)c["Version"]==revision);
            // Never label an old installed folder with the current/latest version's metadata.
            if((string)item["version"]==revision)
            {
                result["version"]=item["userModVersion"]?.DeepClone();
                result["installed_at"]=item["installedDate"]?.DeepClone();
            }
            else if(entry!=null)result["version"]=entry["UserModVersion"]?.DeepClone();
            if(entry!=null)result["released_at"]=entry["ReleasedDate"]?.DeepClone();
            result["listing_updated_at"]=item["latestUpdate"]?.DeepClone();
        }
    }
}
