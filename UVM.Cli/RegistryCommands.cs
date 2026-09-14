using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Uvm;

internal static class RegistryCommands
{
    static string[] Args=[];
    static string Origin=>Opt("registry")??Scanner.Origin;
    static string KeyPath=>Path.GetFullPath(Opt("key")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"UVM","signing-key.p8"));
    static string? Opt(string name){int i=Array.IndexOf(Args,"--"+name);return i>=0 && i+1<Args.Length?Args[i+1]:null;}
    static string Need(string name)=>Opt(name)??throw new ArgumentException("Supply --"+name);
    static readonly string[] Commands=["keygen","key-proof","register","build","publish","attest","scan","check","inspect","revoke","verify","help","--help"];
    public static async Task<bool> TryRun(string[] args)
    {
        if(args.Length==0)return false;
        if(!Commands.Contains(args[0]))return false;
        Args=args;
        try
        {
            switch(args[0])
            {
                case "help":case "--help": Help();break;
                case "keygen": Keygen();break;
                case "key-proof": await KeyProof();break;
                case "register": Print(await Scanner.Api(Origin,"/api/v1/keys",new{gist_url=Need("gist")}));break;
                case "build": await Build();break;
                case "publish": var built=await Build();await Submit(built,"published",ReadFiles(built));break;
                case "attest": await Attest();break;
                case "verify": await Verify();break;
                case "scan":
                    var scan=await Scanner.Scan(Opt("mods-root")??Scanner.DefaultRoot,Origin,Console.Error.WriteLine);
                    Print(scan);if(Opt("out") is string output)Save(output,scan);break;
                case "check":Print(await Scanner.Lookup(Origin,Need("mod-id"),Scanner.HashFolder(Need("package"))));break;
                case "inspect":await Inspect();break;
                case "revoke":using(var key=LoadKey()){var body=Body(key,"revoke-key");body["target_key"]=Need("target");Print(await Scanner.Api(Origin,"/api/v1/keys/"+Uri.EscapeDataString(Need("target"))+"/revoke",Sign(key,body)));}break;
            }
        }
        catch(Exception e){Console.Error.WriteLine("UVM: "+e.Message);Environment.ExitCode=1;}
        return true;
    }
    public static void Help()=>Console.WriteLine("""
        UVM — Unified Verified Mods 0.3.0
        uvm scan [--mods-root folder] [--out report.json]
        uvm check --package folder --mod-id 155518
        uvm keygen [--key path]
        uvm key-proof [--out uvm-key.json]
        uvm register --gist https://gist.github.com/account/id
        uvm build --package folder [--out release.json]
        uvm publish --package folder [--out release.json]
        uvm attest --manifest release.json --package freshly-built-folder
        uvm inspect --release release-uuid
        uvm revoke --target key-fingerprint
        Common: --registry https://vezit.net, --key encrypted-key.p8
        Build/publish use uvm.json in the current source checkout. Build commands execute project code.
        Attest compares the supplied build to the manifest and signs your assertion; inspect the source first.
        Legacy local commands: release, manifest, compare, verify --package folder --manifest file.
        A hash match is not a safety audit. GitHub accounts are not necessarily independent people.
        """);

    static void Print(object data)=>Console.WriteLine(JsonConvert.SerializeObject(data,Formatting.Indented));
    static void Save(string path,object data){path=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,JsonConvert.SerializeObject(data,Formatting.Indented)+"\n",new UTF8Encoding(false));}
    static string Password(bool confirm=false)
    {
        var value=Environment.GetEnvironmentVariable("UVM_KEY_PASSWORD");if(!string.IsNullOrEmpty(value))return value;
        if(Console.IsInputRedirected)throw new InvalidOperationException("Set UVM_KEY_PASSWORD for unattended use, or run in a terminal.");
        Console.Error.Write("Key passphrase: ");var first=ReadPassword();
        if(confirm){Console.Error.Write("Confirm passphrase: ");if(first!=ReadPassword())throw new InvalidOperationException("Passphrases differ.");}
        if(first.Length<12)throw new InvalidOperationException("Use at least 12 characters.");return first;
    }
    static string ReadPassword(){var text=new StringBuilder();while(true){var key=Console.ReadKey(true);if(key.Key==ConsoleKey.Enter)break;if(key.Key==ConsoleKey.Backspace){if(text.Length>0)text.Length--;}else if(!char.IsControl(key.KeyChar))text.Append(key.KeyChar);}Console.Error.WriteLine();return text.ToString();}
    static ECDsa LoadKey(){var key=ECDsa.Create();key.ImportEncryptedPkcs8PrivateKey(Password(),File.ReadAllBytes(KeyPath),out _);if(key.KeySize!=256)throw new InvalidDataException("Expected P-256 key.");return key;}
    static byte[] Public(ECDsa key){var q=key.ExportParameters(false).Q;return new byte[]{4}.Concat(q.X!).Concat(q.Y!).ToArray();}
    static void Keygen(){if(File.Exists(KeyPath))throw new IOException("Key already exists; choose a new --key path to rotate.");using(var key=ECDsa.Create(ECCurve.NamedCurves.nistP256)){var encrypted=key.ExportEncryptedPkcs8PrivateKey(Password(true),new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc,HashAlgorithmName.SHA256,250000));Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);using(var output=new FileStream(KeyPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))output.Write(encrypted);Save(KeyPath+".public.json",new{public_key=Convert.ToBase64String(Public(key)),key_id=Scanner.Sha(Public(key))});Console.WriteLine("Encrypted key saved to "+KeyPath+". Back up the key and passphrase separately.");}}
    static JObject Sign(ECDsa key,JObject body){var raw=Encoding.UTF8.GetBytes(body.ToString(Formatting.None));return new JObject{{"key_id",Scanner.Sha(Public(key))},{"payload",Convert.ToBase64String(raw)},{"signature",Convert.ToBase64String(key.SignData(raw,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation))}};}
    static JObject Body(ECDsa key,string purpose)=>new(){{"schema",1},{"registry",Origin.TrimEnd('/')},{"purpose",purpose},{"key_id",Scanner.Sha(Public(key))},{"issued_at",DateTimeOffset.UtcNow.ToString("O")},{"nonce",Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}};
    static async Task KeyProof(){using(var key=LoadKey()){var response=await Scanner.Api(Origin,"/api/v1/keys/challenge",new{public_key=Convert.ToBase64String(Public(key))});var body=JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String((string)response["payload"]!)));if((string?)body["registry"]!=Origin.TrimEnd('/') || (string?)body["purpose"]!="register-key" || (string?)body["public_key"]!=Convert.ToBase64String(Public(key)))throw new InvalidDataException("Registry returned an unexpected challenge.");var path=Opt("out")??"uvm-key.json";Save(path,Sign(key,body));Console.WriteLine("Post "+Path.GetFullPath(path)+" as a PUBLIC gist named uvm-key.json within one hour, then run uvm register --gist <URL>. This proof contains no private key.");}}
    static SortedDictionary<string,string> ReadFiles(JObject manifest)=>manifest["files"]!.ToObject<SortedDictionary<string,string>>()!;
    static string Git(params string[] args)=>Run("git",Directory.GetCurrentDirectory(),args);
    static string Run(string exe,string cwd,params string[] args)
    {
        var info=new ProcessStartInfo(exe){WorkingDirectory=cwd,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true};foreach(var arg in args)info.ArgumentList.Add(arg);
        using var process=Process.Start(info)??throw new IOException("Cannot start "+exe);var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();process.WaitForExit();Task.WaitAll(stdout,stderr);if(process.ExitCode!=0)throw new InvalidOperationException(exe+" failed: "+stderr.Result+stdout.Result);return stdout.Result.Trim();
    }
    static string ManagedPath()
    {
        var candidate=Opt("game-managed")??Environment.GetEnvironmentVariable("CSII_MANAGEDPATH")??(OperatingSystem.IsWindows()?Environment.GetEnvironmentVariable("CSII_MANAGEDPATH",EnvironmentVariableTarget.User):null);
        if(candidate!=null && File.Exists(Path.Combine(candidate,"Game.dll")))return Path.GetFullPath(candidate);
        var standard=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Steam","steamapps","common","Cities Skylines II","Cities2_Data","Managed");
        if(File.Exists(Path.Combine(standard,"Game.dll")))return standard;
        throw new DirectoryNotFoundException("Install the CS2 modding toolchain or supply --game-managed <Cities2_Data/Managed>.");
    }
    static JObject Config()=>JObject.Parse(File.ReadAllText("uvm.json"));
    static string Repo(JObject config)
    {
        string url=(string?)config["repository"]??Git("remote","get-url","origin");
        if(url.StartsWith("git@github.com:"))url="https://github.com/"+url[15..];
        if(url.EndsWith(".git"))url=url[..^4];if(!Regex.IsMatch(url,@"^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))throw new InvalidDataException("Set repository to a public GitHub HTTPS URL in uvm.json.");return url;
    }
    static async Task<JObject> Build()
    {
        var config=Config();if((int?)config["schema"]!=1)throw new InvalidDataException("uvm.json schema must be 1.");
        if(Git("status","--porcelain").Length!=0)throw new InvalidDataException("Commit or remove uncommitted changes before recording a release.");
        var commit=Git("rev-parse","HEAD");var repository=Repo(config);var root=Git("rev-parse","--show-toplevel");
        var project=(string?)config["build"]?["project"]??(string?)config["project"]??throw new InvalidDataException("Set build.project in uvm.json.");
        var projectPath=Scanner.SafePath(Directory.GetCurrentDirectory(),project.Replace('\\','/'));if(!File.Exists(projectPath))throw new FileNotFoundException("Project not found",projectPath);
        var configuration=(string?)config["build"]?["configuration"]??"Release";if(configuration!="Release"&&configuration!="Debug")throw new InvalidDataException("Use Release or Debug.");
        var publishXml=Path.Combine(Path.GetDirectoryName(projectPath)!,"Properties","PublishConfiguration.xml");var xml=File.Exists(publishXml)?XDocument.Load(publishXml):null;
        string Meta(string json,string tag)=>Opt(json)??(string?)config[json]??xml?.Descendants(tag).FirstOrDefault()?.Attribute("Value")?.Value??throw new InvalidDataException("Set "+json+" in uvm.json or --"+json);
        var modId=(string?)config["modId"]??Meta("mod_id","ModId");var name=Meta("name","DisplayName");var version=Meta("version","ModVersion");var gameVersion=Meta("game_version","GameVersion");
        var managed=ManagedPath();var sdk=Run("dotnet",root,"--version");
        var stage=Path.Combine(Path.GetTempPath(),"uvm-build-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
        var output=Path.GetFullPath(Need("package"));if(Directory.Exists(output)&&Directory.EnumerateFileSystemEntries(output).Any())throw new IOException("Package output must be empty; UVM does not overwrite packages.");
        Console.Error.WriteLine("Building "+project+" at "+commit+". Temporary output: "+stage);
        var buildArgs=new[]{"build",projectPath,"-c",configuration,"--no-incremental","-p:Deterministic=true","-p:ContinuousIntegrationBuild=true","-p:PathMap="+root+"=/_/source","-p:ManagedPath="+managed,"-p:GameManagedPath="+managed,"-p:OutDir="+Path.Combine(stage,"bin")+Path.DirectorySeparatorChar,"-p:DeployDir="+Path.Combine(stage,"deployed"),"-p:LocalModsPath="+Path.Combine(stage,"mods")};
        Console.Error.WriteLine(await Task.Run(()=>Run("dotnet",root,buildArgs)));
        if(Git("rev-parse","HEAD")!=commit || Git("status","--porcelain").Length!=0)throw new InvalidDataException("Build changed the source checkout; no release recorded.");
        var declared=(JArray?)config["output"]??throw new InvalidDataException("Declare output file paths in uvm.json.");
        // Some CS2 toolchain versions derive DeployDir again from LocalModsPath.
        // Only consider deployment folders inside our isolated build stage.
        var deploymentRoots=new List<string>{Path.Combine(stage,"deployed")};
        var localMods=Path.Combine(stage,"mods");
        if(Directory.Exists(localMods))deploymentRoots.AddRange(Directory.GetDirectories(localMods));
        Directory.CreateDirectory(output);
        foreach(var entry in declared)
        {
            var relative=(string?)entry??throw new InvalidDataException("Output entries must be file paths.");var destination=Scanner.SafePath(output,relative);var generated=Scanner.SafePath(Path.Combine(stage,"bin"),relative);
            var deployed=deploymentRoots.Select(r=>Scanner.SafePath(r,relative)).Where(File.Exists).ToArray();
            if(deployed.Length>1)throw new InvalidDataException("Ambiguous deployed output: "+relative);
            if(deployed.Length==1)generated=deployed[0];
            if(!File.Exists(generated))throw new FileNotFoundException("Declared output not generated: "+relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(generated,destination,false);
        }
        var allBuilt=deploymentRoots.Concat(new[]{Path.Combine(stage,"bin")}).Where(Directory.Exists)
            .SelectMany(r=>Directory.GetFiles(r,"*",SearchOption.AllDirectories).Where(f=>Scanner.CodeExtensions.Contains(Path.GetExtension(f)))
                .Select(f=>Path.GetRelativePath(r,f).Replace('\\','/').ToLowerInvariant())).ToArray();
        var hashes=Scanner.HashFolder(output);if(allBuilt.Any(p=>!hashes.ContainsKey(p)))throw new InvalidDataException("The build produced undeclared executable files. Add them to output.");
        var manifest=new JObject{{"schema",1},{"platform","paradox"},{"mod_id",modId},{"name",name},{"version",version},{"repository",repository},{"commit",commit},{"game_version",gameVersion},
          {"build",new JObject{{"project",Path.GetRelativePath(root,projectPath).Replace('\\','/')},{"configuration",configuration},{"sdk",sdk},{"game_assembly_sha256",Scanner.FileHash(Path.Combine(managed,"Game.dll"))}}},{"files",JObject.FromObject(hashes)}};
        Save(Opt("out")??Path.Combine(root,"artifacts","release.json"),manifest);Print(manifest);return manifest;
    }
    static async Task Submit(JObject manifest,string result,SortedDictionary<string,string> built)
    {
        using(var key=LoadKey()){var body=Body(key,"attestation");body["manifest"]=manifest;body["built_files"]=JObject.FromObject(built);body["result"]=result;Print(await Scanner.Api(Origin,"/api/v1/attestations",Sign(key,body)));}
    }
    static async Task Attest()
    {
        var manifest=JObject.Parse(File.ReadAllText(Need("manifest")));var root=Git("rev-parse","--show-toplevel");
        if(Git("rev-parse","HEAD")!=(string?)manifest["commit"] || Git("status","--porcelain").Length!=0)throw new InvalidDataException("Check out the exact manifest commit with a clean tree before attesting.");
        if(Repo(Config())!=(string?)manifest["repository"])throw new InvalidDataException("Repository does not match manifest.");
        if(Scanner.FileHash(Path.Combine(ManagedPath(),"Game.dll"))!=(string?)manifest["build"]?["game_assembly_sha256"])throw new InvalidDataException("Game.dll differs from the release build.");
        var built=Scanner.HashFolder(Need("package"));var expected=ReadFiles(manifest);
        var selected=new SortedDictionary<string,string>(StringComparer.Ordinal);foreach(var p in built)if(expected.ContainsKey(p.Key)||Scanner.CodeExtensions.Contains(Path.GetExtension(p.Key)))selected.Add(p.Key,p.Value);
        var matches=selected.Count==expected.Count && expected.All(p=>selected.TryGetValue(p.Key,out var hash)&&hash==p.Value);
        await Submit(manifest,matches?"reproduced":"mismatch",selected);if(!matches)Environment.ExitCode=2;
    }
    static async Task Inspect()
    {
        var data=await Scanner.Api(Origin,"/api/v1/releases/"+Uri.EscapeDataString(Need("release"))+"/attestations");
        foreach(JObject evidence in (JArray)data["results"]!){bool valid=Scanner.VerifyEvidence(evidence,Origin);Console.WriteLine($"{evidence["key"]?["github"]?["login"]}: {evidence["result"]} — {(valid?"valid active signature":"invalid or revoked signature")}");if(!valid)Environment.ExitCode=2;}
        Print(data["release"]!);
    }
    static async Task Verify()
    {
        JObject manifest;
        if(Opt("manifest") is string path)manifest=JObject.Parse(File.ReadAllText(path));
        else
        {
            var config=Config();var modId=(string?)config["modId"]??throw new InvalidDataException("Set modId in uvm.json.");
            var version=Opt("version")??(string?)config["version"]??throw new InvalidDataException("Supply --version or set version in uvm.json.");
            var data=await Scanner.Api(Origin,"/api/v1/mods/paradox/"+Uri.EscapeDataString(modId)+"/releases/"+Uri.EscapeDataString(version));
            var candidates=(JArray)data["results"]!;if(candidates.Count!=1)throw new InvalidDataException("Conflicting manifests exist; inspect the release evidence before choosing one.");
            manifest=(JObject)candidates[0]["manifest"]!;
        }
        if(Opt("package") is null)
        {
            var package=Path.Combine(Path.GetTempPath(),"uvm-verify-"+Guid.NewGuid().ToString("N"));Args=Args.Concat(new[]{"--package",package}).ToArray();
            if(Opt("out") is null)Args=Args.Concat(new[]{"--out",package+".build.json"}).ToArray();
            if(Git("rev-parse","HEAD")!=(string?)manifest["commit"])throw new InvalidDataException("Check out the exact release commit: "+manifest["commit"]);
            var built=await Build();if(!JToken.DeepEquals(built["build"],manifest["build"]))throw new InvalidDataException("Build recipe/toolchain differs from the release.");
        }
        var expected=new SortedDictionary<string,string>(StringComparer.Ordinal);
        var entries=manifest["files"]??manifest["Files"]??throw new InvalidDataException("Missing manifest files.");
        foreach(var prop in ((JObject)entries).Properties())
        {var relative=prop.Name.Replace('\\','/').ToLowerInvariant();Scanner.SafePath(Need("package"),relative);expected.Add(relative,prop.Value.Type==JTokenType.String?(string)prop.Value!:(string?)(prop.Value["sha256"]??prop.Value["Sha256"])??throw new InvalidDataException("Missing SHA-256."));}
        if(expected.Count==0 || !expected.Keys.Any(p=>Scanner.CodeExtensions.Contains(Path.GetExtension(p))))throw new InvalidDataException("Manifest must cover code files.");
        var installed=Scanner.HashFolder(Need("package"));bool matches=expected.All(p=>installed.TryGetValue(p.Key,out var hash)&&hash==p.Value)&&installed.Keys.Where(p=>Scanner.CodeExtensions.Contains(Path.GetExtension(p))).All(expected.ContainsKey);
        Console.WriteLine(matches?"HASH MATCH — declared files match; all executable files are covered. This is not a safety audit or a signed attestation.":"HASH MISMATCH — files differ, are missing, or include undeclared executable code.");if(!matches)Environment.ExitCode=2;
    }
}
