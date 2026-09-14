using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using Uvm;

class Program
{
 static int Main(string[] args)
 {
  var workspace=Path.GetFullPath(args[0]);Directory.CreateDirectory(workspace);int count=0;
  Action<bool,string> check=(value,name)=>{if(!value)throw new Exception("FAILED: "+name);Console.WriteLine("PASS: "+name);count++;};
#if !NET48
  using(var key=ECDsa.Create(ECCurve.NamedCurves.nistP256))
  {
   var q=key.ExportParameters(false).Q;var pub=new byte[]{4}.Concat(q.X).Concat(q.Y).ToArray();var id=Scanner.Sha(pub);
   var body=new JObject{{"schema",1},{"registry",Scanner.Origin},{"purpose","attestation"},{"key_id",id},{"issued_at",DateTimeOffset.UtcNow.ToString("O")},{"nonce",Guid.NewGuid().ToString("N")},{"result","reproduced"},
    {"manifest",new JObject{{"schema",1},{"platform","paradox"},{"mod_id","999999999"},{"name","LOCAL TEST ONLY"},{"version","test"},{"repository","https://github.com/test/example"},{"commit",new string('a',40)},{"game_version","test"},{"build",new JObject{{"project","test.csproj"},{"configuration","Release"},{"sdk","10.0.302"},{"game_assembly_sha256",new string('b',64)}}},{"files",new JObject{{"example.dll",new string('c',64)}}}}},
    {"built_files",new JObject{{"example.dll",new string('c',64)}}}};
   var raw=Encoding.UTF8.GetBytes(body.ToString(Formatting.None));var evidence=new JObject{{"result","reproduced"},{"payload",Convert.ToBase64String(raw)},{"signature",Convert.ToBase64String(key.SignData(raw,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation))},{"key",new JObject{{"id",id},{"public_key",Convert.ToBase64String(pub)},{"revoked_at",null}}}};
   File.WriteAllText(Path.Combine(workspace,"cross-runtime-proof.json"),evidence.ToString());
  }
#endif
  var original=JObject.Parse(File.ReadAllText(Path.Combine(workspace,"cross-runtime-proof.json")));
  check(Scanner.VerifyEvidence(original,Scanner.Origin),".NET-generated P256 signature verifies");
  check(!Scanner.VerifyEvidence(original,"https://wrong.example"),"Audience mismatch rejected");
  var tampered=(JObject)original.DeepClone();tampered["signature"]=Convert.ToBase64String(new byte[64]);check(!Scanner.VerifyEvidence(tampered,Scanner.Origin),"Tampered signature rejected");
  tampered=(JObject)original.DeepClone();tampered["key"]["revoked_at"]="2026-01-01";check(!Scanner.VerifyEvidence(tampered,Scanner.Origin),"Revoked key rejected");
  tampered=(JObject)original.DeepClone();tampered["key"]["id"]=new string('0',64);check(!Scanner.VerifyEvidence(tampered,Scanner.Origin),"Wrong key fingerprint rejected");
  foreach(var path in new[]{"../outside.dll","/absolute.dll","a/../bad.dll","C:/bad.dll","a./bad.dll"})
  {bool rejected=false;try{Scanner.SafePath(workspace,path);}catch(InvalidDataException){rejected=true;}check(rejected,"Unsafe path rejected: "+path);}
  var package=Path.Combine(workspace,"package");Directory.CreateDirectory(package);File.WriteAllBytes(Path.Combine(package,"example.dll"),new byte[]{1,2,3});var hashes=Scanner.HashFolder(package);check(hashes["example.dll"]==Scanner.Sha(new byte[]{1,2,3}),"Files hashed from bytes");
  var payload=JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String((string)original["payload"])));
  var expected=payload["manifest"]["files"].ToObject<System.Collections.Generic.SortedDictionary<string,string>>();
  check(Scanner.MatchesEvidence(original,Scanner.Origin,"999999999",payload["manifest"],expected),"Matching signed executable coverage accepted");
  check(!Scanner.MatchesEvidence(original,Scanner.Origin,"156780",payload["manifest"],expected),"Evidence for another mod rejected");
  expected["example.dll"]=new string('d',64);
  check(!Scanner.MatchesEvidence(original,Scanner.Origin,"999999999",payload["manifest"],expected),"Changed downloaded bytes rejected");
  expected["example.dll"]=new string('c',64);expected["extra.dll"]=new string('e',64);
  check(!Scanner.MatchesEvidence(original,Scanner.Origin,"999999999",payload["manifest"],expected),"Undeclared executable rejected");
  var metadata=JArray.Parse("[{id:128566,operatingSystem:'Linux',version:11,userModVersion:'wrong',displayName:'Wrong OS'},{id:128566,operatingSystem:'Windows',version:11,userModVersion:'3.1.2',displayName:'Traffic Spy',changelog:[{Version:10,UserModVersion:'3.1.1',ReleasedDate:'2026-08-18'}]}]");
  var row=new JObject();Scanner.AddMetadata(row,"128566_11",metadata);
  check((string)row["name"]=="Traffic Spy" && (string)row["version"]=="3.1.2" && (string)row["paradox_revision"]=="11","Installed version uses exact Windows revision");
  row=new JObject();Scanner.AddMetadata(row,"128566_10",metadata);
  check((string)row["version"]=="3.1.1" && (string)row["released_at"]=="2026-08-18","Old revision uses matching changelog");
  row=new JObject();Scanner.AddMetadata(row,"128566_9",metadata);
  check(row["version"]==null,"Missing old revision never inherits latest version");
  row=new JObject();Scanner.AddMetadata(row,"156780_1",metadata);
  check((string)row["name"]=="Paradox mod 156780" && row["version"]==null,"Missing metadata keeps usable ID fallback");
  var cache=Path.Combine(workspace,"cache");Directory.CreateDirectory(cache);
  File.WriteAllText(Path.Combine(cache,"pdx_mods_cache.json"),"invalid JSON");
  check(Scanner.ReadMetadata(Path.Combine(cache,"pdx_mods")).Count==0,"Malformed metadata does not stop scans");
  Console.WriteLine(count+" client checks passed.");return 0;
 }
}
