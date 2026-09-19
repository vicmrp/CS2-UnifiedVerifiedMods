using Colossal;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Settings;
using Game.UI.Localization;
using Game.UI.Widgets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace Uvm
{
    public sealed class Mod : IMod
    {
        internal static readonly ILog Log=LogManager.GetLogger("UVM");
        Settings settings;
        UnityEngine.GameObject pump;
        public void OnLoad(UpdateSystem updateSystem)
        {
            settings=new Settings(this);
            pump=new UnityEngine.GameObject("UVM inventory");UnityEngine.Object.DontDestroyOnLoad(pump);
            pump.AddComponent<InventoryPump>().Inventory=settings.Inventory;
            settings.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US",new Locale(settings));
            Log.Info("Unified Verified Mods 0.5.0 loaded. Open Options > Unified Verified Mods to check downloaded code mods.");
        }
        public void OnDispose(){settings?.Stop();settings?.UnregisterInOptionsUI();settings=null;if(pump!=null)UnityEngine.Object.Destroy(pump);}
    }

    [FileLocation("ModsSettings/UVM/UVM")]
    [SettingsUITabOrder("Scan", "Report", "Filters")]
    [SettingsUIGroupOrder("Verification", "Inventory", "Summary", "Package", "Export")]
    [SettingsUIShowGroupName("Package")]
    public sealed partial class Settings : ModSetting
    {
        CancellationTokenSource cancellation=new CancellationTokenSource();
        int running;
        volatile string status="Ready to scan downloaded mods.";
        volatile ScanReport report=ScanReport.Empty;
        int selectedPackage;
        int reportVersion;
        readonly string reportFolder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"AppData","LocalLow","Colossal Order","Cities Skylines II","ModsData","UVM");
        public Settings(IMod mod):base(mod)
        {
            LoadFilters();
            Inventory=new ModInventory(()=>scanResults,()=>scanTime);
            // Listen automatically on first use, while retaining an explicit opt-out.
            bool connect=true;try{if(File.Exists(BridgeConfig))connect=(bool?)JObject.Parse(File.ReadAllText(BridgeConfig))["enabled"]??true;}catch{}
            EnableObserveBridge=connect;
            var path=Path.Combine(reportFolder,"report.json");
            if(!File.Exists(path))return;
            try
            {
                if(new FileInfo(path).Length>4*1024*1024)throw new IOException("Saved report is too large.");
                var saved=JArray.Parse(File.ReadAllText(path));
                var metadata=Scanner.ReadMetadata(Scanner.DefaultRoot);
                foreach(JObject row in saved)Scanner.AddMetadata(row,(string)row["folder"]??"",metadata);
                scanResults=saved;scanTime=File.GetLastWriteTimeUtc(path).ToString("O");
                report=ScanReport.From(saved,File.GetLastWriteTime(path));
                status="Previous results are available in Report.";
            }
            catch(Exception e){Mod.Log.Warn("Could not read the previous UVM report: "+e.Message);}
        }
        [XmlIgnore,SettingsUISection("Scan","Verification"),SettingsUIMultilineText,
         SettingsUIDisplayName(typeof(Settings),nameof(GetStatusText))]
        public string Status => status;
        public LocalizedString GetStatusText()=>LocalizedString.Value(status);
        [XmlIgnore,SettingsUIButton,SettingsUISection("Scan","Verification"),SettingsUIDisableByCondition(typeof(Settings),nameof(IsRunning))]
        public bool ScanDownloadedMods {set{if(value)Start();}}

        [XmlIgnore,SettingsUISection("Report","Summary"),SettingsUIMultilineText,
         SettingsUIDisplayName(typeof(Settings),nameof(GetSummaryText))]
        public string ReportSummary=>string.Empty;
        public LocalizedString GetSummaryText()=>LocalizedString.Value(report.Summary);

        [XmlIgnore,SettingsUISection("Report","Package"),SettingsUIDropdown(typeof(Settings),nameof(GetPackages)),
         SettingsUIValueVersion(typeof(Settings),nameof(GetReportVersion)),
         SettingsUIDisableByCondition(typeof(Settings),nameof(HasNoPackages))]
        public int SelectedPackage
        {
            get=>Math.Min(Math.Max(0,selectedPackage),Math.Max(0,report.Items.Length-1));
            set=>selectedPackage=Math.Min(Math.Max(0,value),Math.Max(0,report.Items.Length-1));
        }
        public DropdownItem<int>[] GetPackages()=>report.Items;
        public int GetReportVersion()=>Volatile.Read(ref reportVersion);
        public bool HasNoPackages()=>report.Details.Length==0;

        [XmlIgnore,SettingsUISection("Report","Package"),SettingsUIMultilineText,
         SettingsUIDisplayName(typeof(Settings),nameof(GetPackageText))]
        public string PackageDetails=>string.Empty;
        public LocalizedString GetPackageText()
        {
            var snapshot=report;
            var index=Math.Min(Math.Max(0,selectedPackage),snapshot.Details.Length-1);
            return LocalizedString.Value(index<0 ? "Run a scan to see package results here." : snapshot.Details[index]);
        }

        [XmlIgnore,SettingsUISection("Report","Summary"),SettingsUIMultilineText]
        public string ReportNotice=>string.Empty;

        [XmlIgnore,SettingsUIButton,SettingsUISection("Report","Export"),SettingsUIDisableByCondition(typeof(Settings),nameof(HasNoPackages))]
        public bool OpenReport {set{if(value){var path=Path.Combine(reportFolder,"report.html");if(File.Exists(path))Process.Start(new ProcessStartInfo(path){UseShellExecute=true});else status="Run a scan first to create the report.";}}}
        [XmlIgnore,SettingsUIButton,SettingsUISection("Scan","Verification")]
        public bool OpenRegistry {set{if(value)Process.Start(new ProcessStartInfo(Scanner.Origin){UseShellExecute=true});}}
        [XmlIgnore,SettingsUIButton,SettingsUISection("Scan","Verification")]
        public bool OpenObserver {set{if(value)Process.Start(new ProcessStartInfo("https://vezit.net#observer"){UseShellExecute=true});}}
        public bool IsRunning()=>Volatile.Read(ref running)!=0;
        public override void SetDefaults(){}
        internal void Stop(){cancellation.Cancel();bridge?.Dispose();Inventory.Dispose();}
        void Start()
        {
            if(Interlocked.CompareExchange(ref running,1,0)!=0)return;
            status="Starting background scan…";
            Task.Run(async()=>
            {
                try
                {
                    var results=await Scanner.Scan(Scanner.DefaultRoot,Scanner.Origin,s=>status=s,cancellation.Token);
                    ObserveActivity.Record("network","Completed UVM registry build-evidence lookups",Scanner.Origin);
                    scanResults=results;scanTime=DateTimeOffset.UtcNow.ToString("O");
                    report=ScanReport.From(results,DateTime.Now);Inventory.Invalidate();
                    Interlocked.Increment(ref reportVersion);
                    Directory.CreateDirectory(reportFolder);
                    File.WriteAllText(Path.Combine(reportFolder,"report.json"),results.ToString(Formatting.Indented));
                    var html=new StringBuilder("<!doctype html><html lang='en'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>UVM scan report</title><style>body{font:16px/1.6 system-ui;background:#f6f7f2;color:#193d31;max-width:1000px;margin:40px auto;padding:24px}table{border-collapse:collapse;width:100%;background:white}td,th{text-align:left;padding:14px;border-bottom:1px solid #ddd}small{color:#68756b}a{color:#226246}</style><h1>Unified Verified Mods</h1><p>Downloaded mod cache · "+WebUtility.HtmlEncode(DateTimeOffset.Now.ToString("g"))+"</p><p>Cached mods may include disabled or older versions. A reproduced build is not a safety audit. Results reflect this scan only.</p><table><tr><th>Paradox mod</th><th>Evidence</th><th>Details</th></tr>");
                    foreach(JObject row in results){var id=(string)row["mod_id"];html.Append("<tr><td><a href='https://mods.paradoxplaza.com/mods/"+Uri.EscapeDataString(id)+"/Windows'>"+WebUtility.HtmlEncode((string)row["name"]??id)+"</a><br><small>Version "+WebUtility.HtmlEncode((string)row["version"]??"unknown")+" · revision "+WebUtility.HtmlEncode((string)row["paradox_revision"])+" · ID "+WebUtility.HtmlEncode(id)+"</small></td><td>"+WebUtility.HtmlEncode((string)row["status"])+"</td><td>"+WebUtility.HtmlEncode((string)row["reason"])+"</td></tr>");}
                    html.Append("</table><p><a href='https://vezit.net'>Inspect evidence at vezit.net →</a></p></html>");File.WriteAllText(Path.Combine(reportFolder,"report.html"),html.ToString(),Encoding.UTF8);
                    ObserveActivity.Record("file","Saved scan report",reportFolder);
                    status=$"Scan complete: {results.Count} packages. Open the Report tab for results.";Mod.Log.Info(status);
                }
                catch(OperationCanceledException){status="Scan cancelled.";}
                catch(Exception e){status="Scan unavailable: "+e.Message;Mod.Log.Warn(status);}
                finally{Interlocked.Exchange(ref running,0);}
            });
        }
    }

    // Constructed once after a scan, then published as an immutable snapshot to the options UI.
    internal sealed class ScanReport
    {
        public static readonly ScanReport Empty=new ScanReport("No scan results yet.",new DropdownItem<int>[0],new string[0]);
        public readonly string Summary;
        public readonly DropdownItem<int>[] Items;
        public readonly string[] Details;
        ScanReport(string summary,DropdownItem<int>[] items,string[] details){Summary=summary;Items=items;Details=details;}
        public static ScanReport From(JArray results,DateTime scannedAt)
        {
            var rows=results.OfType<JObject>().ToArray();
            var counts=rows.GroupBy(r=>(string)r["status"]??"UNAVAILABLE")
                .Select(g=>g.Count()+" "+g.Key.ToLowerInvariant());
            var summary="Downloaded mod cache · "+scannedAt.ToString("g")+"\n"+
                rows.Length+" packages · "+string.Join(" · ",counts);
            var items=new DropdownItem<int>[rows.Length];
            var details=new string[rows.Length];
            for(int i=0;i<rows.Length;i++)
            {
                var row=rows[i];
                string id=Clean((string)row["mod_id"]),folder=Clean((string)row["folder"]);
                string state=Clean((string)row["status"]),reason=Clean((string)row["reason"]);
                string name=Clean((string)row["name"]??("Paradox mod "+id)),version=Clean((string)row["version"]??"unknown");
                items[i]=new DropdownItem<int>{value=i,displayName=name+" · "+version};
                details[i]=name+"\n"+state+" — "+reason+"\nInstalled version: "+version+
                    " · Paradox revision: "+Clean((string)row["paradox_revision"]??folder.Split('_').Last())+
                    "\nParadox ID: "+id+" · Cached package: "+folder;
                if(state=="REPRODUCED" && row["verified_by"] is JArray verifiers && verifiers.Count>0)
                    details[i]+="\nVerified by: "+string.Join(", ",verifiers.Select(v=>Clean((string)v)))+
                        "\n"+row["matching_accounts"]+" GitHub account(s) with signed matching builds.";
                foreach(var field in new[]{new[]{"released_at","Release date (reported)"},new[]{"listing_updated_at","Listing updated (reported)"},new[]{"installed_at","Installed"}})
                    if(row[field[0]]?.Type==JTokenType.String)details[i]+="\n"+field[1]+": "+Clean((string)row[field[0]]);
                if(row["content_sha256"]!=null)
                {
                    var hash=Clean((string)row["content_sha256"]);
                    details[i]+="\nContent SHA-256 ("+row["hashed_files"]+" hashed files):\n"+
                        (hash.Length==64?hash.Substring(0,32)+"\n"+hash.Substring(32):hash);
                }
            }
            return new ScanReport(summary,items,details);
        }
        // Native rich-text labels must not interpret downloaded report text as markup.
        static string Clean(string value)=>(value??"Unknown").Replace("<","‹").Replace(">","›");
    }

    public sealed class Locale : IDictionarySource
    {
        readonly Dictionary<string,string> entries;
        public Locale(Settings s){entries=new Dictionary<string,string>{
            {s.GetOptionLabelLocaleID(nameof(Settings.OpenObserver)),"Observer"},{s.GetOptionDescLocaleID(nameof(Settings.OpenObserver)),"Learn about Observe and download its optional Windows installer at vezit.net#observer."},
            {s.GetOptionTabLocaleID("Filters"),"Filters"},
            {s.GetOptionLabelLocaleID(nameof(Settings.CodeModsOnly)),"Code mods only"},{s.GetOptionDescLocaleID(nameof(Settings.CodeModsOnly)),"Hide content-only packages from the Scan list. Enabled by default. Code is detected from packaged DLLs, scripts and other executable files, including subfolders. Packages with incomplete inspection stay visible. The Report tab retains the full scan."},
            {s.GetOptionLabelLocaleID(nameof(Settings.LoadedModsOnly)),"Loaded mods only"},{s.GetOptionDescLocaleID(nameof(Settings.LoadedModsOnly)),"Only show mods whose code is loaded in this game process. Turn off to include downloaded, disabled and local packages. Filters are saved for next time."},
            {s.GetOptionLabelLocaleID(nameof(Settings.EnableObserveBridge)),"Connect to Observe on this PC"},{s.GetOptionDescLocaleID(nameof(Settings.EnableObserveBridge)),"Share mod names, loaded assemblies, build scan results, compiled API references and cooperative activity receipts with Observe for this Windows user. No network port or automatic uploads. Turn off to disconnect."},
            {s.GetSettingsLocaleID(),"Unified Verified Mods"},{s.GetOptionTabLocaleID("Scan"),"Scan"},{s.GetOptionTabLocaleID("Report"),"Report"},
            {s.GetOptionGroupLocaleID("Verification"),"Build evidence"},{s.GetOptionGroupLocaleID("Package"),"Package results"},
            {s.GetOptionLabelLocaleID(nameof(Settings.Status)),"Scan status"},{s.GetOptionDescLocaleID(nameof(Settings.Status)),"Evidence from distinct GitHub accounts. Reproducibility does not establish that a mod is safe."},
            {s.GetOptionLabelLocaleID(nameof(Settings.ScanDownloadedMods)),"Scan downloaded mods"},{s.GetOptionDescLocaleID(nameof(Settings.ScanDownloadedMods)),"Hash downloaded Paradox code packages in the background and check vezit.net. Only mod IDs, relative file paths and SHA-256 hashes are sent. No DLLs are uploaded. The cache may contain disabled mods and older versions."},
            {s.GetOptionLabelLocaleID(nameof(Settings.SelectedPackage)),"Package"},{s.GetOptionDescLocaleID(nameof(Settings.SelectedPackage)),"Choose a package by name and installed version. Names and dates come from the game's cached Paradox metadata; evidence is checked against actual file hashes. The folder suffix is Paradox's revision number."},
            {s.GetOptionLabelLocaleID(nameof(Settings.ReportNotice)),"This snapshot may include disabled mods and older versions. Reproduced means signed matching build evidence; it is not a safety audit."},
            {s.GetOptionLabelLocaleID(nameof(Settings.OpenReport)),"Open browser copy"},{s.GetOptionDescLocaleID(nameof(Settings.OpenReport)),"Optional: open an HTML copy of this report in your browser."},
            {s.GetOptionLabelLocaleID(nameof(Settings.OpenRegistry)),"Open UVM registry"},{s.GetOptionDescLocaleID(nameof(Settings.OpenRegistry)),"Browse public evidence at https://vezit.net."}};}
        public IEnumerable<KeyValuePair<string,string>> ReadEntries(IList<IDictionaryEntryError> errors,Dictionary<string,int> indexCounts)=>entries;
        public void Unload(){}
    }
}
