using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Game.Settings;
using Game.UI.Menu;
using Game.UI.Localization;
using Newtonsoft.Json.Linq;
using Uvm.Bridge;

namespace Uvm
{
    public sealed partial class Settings
    {
        internal readonly ModInventory Inventory;
        volatile JArray scanResults=new JArray();
        volatile string scanTime="";
        ObserveServer bridge;
        BridgeFilters filters;
        bool codeOnly=>filters.CodeOnly;
        bool loadedOnly=>filters.LoadedOnly;
        string FilterConfig=>Path.Combine(reportFolder,"filters.json");
        [XmlIgnore,SettingsUISection("Filters","ListFilters")]
        public bool CodeModsOnly {get=>codeOnly;set=>filters.Set(value,loadedOnly);}
        [XmlIgnore,SettingsUISection("Filters","ListFilters")]
        public bool LoadedModsOnly {get=>loadedOnly;set=>filters.Set(codeOnly,value);}
        [XmlIgnore,SettingsUISection("Scan","Verification"),SettingsUIMultilineText,
         SettingsUIDisplayName(typeof(Settings),nameof(GetFilterSummary))]
        public string FilterSummary=>string.Empty;
        public LocalizedString GetFilterSummary()=>LocalizedString.Value("Showing "+Inventory.Rows.Count(MatchesFilters)+" of "+Inventory.Count+" packages · "+(codeOnly?"Code mods":"All mods")+(loadedOnly?" · Loaded only":"")+". Change this in Filters.");
        internal bool MatchesFilters(JObject row)=>(!codeOnly||(bool?)row["is_code"]!=false)&&(!loadedOnly||(bool?)row["loaded"]==true);
        void LoadFilters(){bool code=true,loaded=false;try{if(File.Exists(FilterConfig)){var saved=JObject.Parse(File.ReadAllText(FilterConfig));code=(bool?)saved["code_only"]??true;loaded=(bool?)saved["loaded_only"]??false;}}catch{}filters=new BridgeFilters(Path.Combine(reportFolder,"bridge-filters.txt"),code,loaded);}
        string BridgeConfig=>Path.Combine(reportFolder,"observe-bridge.json");
        [XmlIgnore,SettingsUISection("Scan","Verification")]
        public bool EnableObserveBridge
        {
            get=>bridge!=null;
            set
            {
                if(value==EnableObserveBridge)return;
                if(value)bridge=new ObserveServer(()=>Inventory.Snapshot,filters);
                else{bridge?.Dispose();bridge=null;}
                Directory.CreateDirectory(reportFolder);File.WriteAllText(BridgeConfig,new JObject{{"enabled",value}}.ToString());
            }
        }
        public override AutomaticSettings.SettingPageData GetPageData(string id,bool addPrefix)
        {
            var page=base.GetPageData(id,addPrefix);page["Scan"].AddItem(new InventoryItem(this,page.prefix,-1));
            for(int i=0;i<512;i++)page["Scan"].AddItem(new InventoryItem(this,page.prefix,i));return page;
        }
        internal JObject ConnectionRow()=>new JObject{{"name","Observe ↔ Unified Verified Mods"},{"connection",bridge?.Connected==true},{"description",bridge==null?"Connection is switched off. Enable Connect to Observe above to reconnect automatically.":"Connects automatically while Observe is running with its Cities II plugin enabled. Use the OBSERVER button above for installation. The green check requires a successful exchange within the last 12 seconds. "+(bridge?.Error??"")+"\nFilters synchronize in both directions. Mod reports are cooperative; the game shares one process. Windows evidence cannot normally identify an individual mod caller."}};
    }
}
