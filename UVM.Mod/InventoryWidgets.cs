using System;
using System.Collections.Generic;
using System.Linq;
using Colossal.UI.Binding;
using Game.Reflection;
using Game.UI.Localization;
using Game.UI.Menu;
using Game.UI.Widgets;
using Newtonsoft.Json.Linq;

namespace Uvm
{
    // Reuse the native Options > Modding renderer, with a read-only UVM value model.
    internal sealed class InventoryItem : AutomaticSettings.SettingItemData
    {
        readonly Settings owner;
        readonly int index;
        public InventoryItem(Settings owner,string prefix,int index):base(AutomaticSettings.WidgetType.None,owner,new AutomaticSettings.ProxyProperty(typeof(Settings).GetProperty(nameof(Settings.Status))),prefix){this.owner=owner;this.index=index;simpleGroup="Inventory";}
        protected override IWidget GetWidget()
        {
            return new ModStatusWidget(()=>index<0?owner.ConnectionRow():owner.Inventory.Row(index),()=>index<0?(int)(DateTime.UtcNow.Ticks/TimeSpan.TicksPerSecond):owner.Inventory.Version)
            {
                path=index<0?"ObserveConnection":"UvmMod"+index,
                hidden=()=>index>=0&&(index>=owner.Inventory.Count||!owner.MatchesFilters(owner.Inventory.Row(index)))
            };
        }
    }
    internal sealed class ModStatusWidget : ReadonlyField<JObject>
    {
        readonly Func<JObject> row;
        readonly IWidget[] details;
        public override string propertiesTypeName=>typeof(ModdingToolchainDependency).FullName;
        // The Options renderer owns its expansion state and needs the child row even while collapsed.
        public override IList<IWidget> visibleChildren=>details;
        public ModStatusWidget(Func<JObject> row,Func<int> version)
        {
            this.row=row;valueWriter=new StatusWriter();accessor=new DelegateAccessor<JObject>(row);valueVersion=version;
            displayNameAction=()=>LocalizedString.Value(Clean((string)row()["name"]));
            descriptionAction=()=>LocalizedString.Value(Details(row()));
            details=new IWidget[]{new MultilineText{path="details",displayNameAction=()=>LocalizedString.Value(Details(row()))}};
        }
        static string Clean(string s)=>(s??"").Replace("<","‹").Replace(">","›");
        static string Details(JObject r)
        {
            if(r["connection"]!=null)return Clean((string)r["description"]);
            var build=r["build"] as JObject??new JObject();
            return Clean((string)r["availability"])+"\n"+Clean((string)r["description"])+"\n"+
                "Last build scan: "+Clean((string)build["status"])+" — "+Clean((string)build["reason"])+"\n"+
                "Verified by: "+Clean(string.Join(", ",(build["verified_by"] as JArray??new JArray()).Select(x=>(string)x)))+"\n"+
                "Paradox ID: "+Clean((string)r["mod_id"]??"local")+" · revision "+Clean((string)r["paradox_revision"]??"—")+"\n"+
                "Compiled API references: "+(r["capabilities"] as JArray??new JArray()).Count(c=>(string)c["category"]!="coverage")+" (not proof of execution).\n"+
                "Open Observe > Cities II mods for processes, network and file evidence. Use Inform the modder there for a copyable UVM request and the Paradox listing. A matching build is not a safety audit.";
        }
        sealed class StatusWriter:IWriter<JObject>
        {
            public void Write(IJsonWriter writer,JObject r)
            {
                r=r??new JObject();var state=(string)r["build"]?["status"]??"NOT_SCANNED";
                bool connected=(bool?)r["connection"]==true, isConnection=r["connection"]!=null;
                writer.TypeBegin("Uvm.ModStatus");
                writer.PropertyName("name");writer.Write(LocalizedString.Value(Clean((string)r["name"])));
                writer.PropertyName("state");writer.Write((isConnection?connected:state=="REPRODUCED")?0:2);
                writer.PropertyName("progress");writer.Write(-1);
                writer.PropertyName("details");writer.Write(LocalizedString.Value(isConnection?(connected?"Connected":"Not connected"):state=="REPRODUCED"?"Reproduced · last scan":"Unverified · "+state));
                writer.PropertyName("version");writer.Write(LocalizedString.Value(Clean((string)r["version"]??"")));
                writer.PropertyName("icon");writer.Write((string)r["icon"]??"");writer.TypeEnd();
            }
        }
    }
}
