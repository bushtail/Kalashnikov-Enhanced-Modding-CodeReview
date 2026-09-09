using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace KalashnikovEnhancedModding;

/// <summary>Pure database/weapon migration shared by the mod and the offline verifier.</summary>
public sealed class MigrationEngine(JsonObject templates, JsonObject rules)
{
    public int RepairedWeapons { get; private set; }
    public HashSet<string> Warnings { get; } = [];
    private readonly HashSet<string> _weapons = rules["weaponIds"]!.AsArray().Select(S).ToHashSet();
    public bool IsWeapon(string templateId) => _weapons.Contains(templateId);
    public static string S(JsonNode? n) => n?.GetValue<string>() ?? "";
    private static string Id(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("kalashnikov-enhanced:"+key)))[..24];
    private JsonArray Slots(string tpl) => templates[tpl]?["_props"]?["Slots"] as JsonArray ?? [];
    private JsonObject? Slot(string tpl, string name) => Slots(tpl).OfType<JsonObject>().FirstOrDefault(s=>S(s["_name"])==name);
    private bool Accepts(string tpl, string slot, string child) => Slot(tpl,slot)?["_props"]?["filters"] is JsonArray filters &&
        filters.Any(f=>f?["Filter"] is JsonArray a && a.Any(v=>S(v)==child));
    private JsonObject? Child(JsonArray tree, JsonObject parent, string slot) => tree.OfType<JsonObject>().FirstOrDefault(i=>S(i["parentId"])==S(parent["_id"])&&S(i["slotId"])==slot);
    private JsonObject Add(JsonArray tree, JsonObject parent, string slot, string tpl)
    {
        var existing=Child(tree,parent,slot);if(existing is not null)return existing;
        var key=S(parent["_id"])+":"+slot;var id=Id(key);var salt=0;
        while(tree.Any(i=>S(i?["_id"])==id))id=Id(key+":"+(++salt));
        var item=new JsonObject{["_id"]=id,["_tpl"]=tpl,["parentId"]=S(parent["_id"]),["slotId"]=slot};
        tree.Add(item);return item;
    }
    public void PatchTemplates(JsonArray patches)
    {
        foreach(var patch in patches.OfType<JsonObject>())
        {
            var id=S(patch["_id"]);
            if(templates[id] is not JsonObject item)throw new InvalidDataException($"Missing SPT template {id}");
            var props=item["_props"]!.AsObject();
            foreach(var (key,value) in patch["_props"]!.AsObject())
            {
                // Preserve new 4.1 choices in unchanged attachment slots. Structural slots use the mod's filters.
                var replacement=value?.DeepClone();
                if(key=="Slots" && replacement is JsonArray slots)
                {
                    foreach(var slot in slots.OfType<JsonObject>())
                    {
                        var name=S(slot["_name"]);
                        var old=Slot(id,name);
                        if(name is "mod_stock" or "mod_magazine" or "mod_pistol_grip" or "mod_charge")
                            if(old?["_props"]?["filters"]?[0]?["Filter"] is JsonArray prior && slot["_props"]?["filters"]?[0]?["Filter"] is JsonArray target)
                                foreach(var entry in prior)if(!target.Any(v=>S(v)==S(entry)))target.Add(entry!.DeepClone());
                        slot["_id"]=Id(id+":"+name);slot["_parent"]=id;
                    }
                }
                props[key]=replacement;
            }
        }
    }
    public void Visit(JsonNode? node)
    {
        if(node is JsonArray array)
        {
            if(array.OfType<JsonObject>().Any(i=>i.ContainsKey("_tpl")&&i.ContainsKey("_id")))FixTree(array);
            foreach(var child in array.ToArray())Visit(child);
        }
        else if(node is JsonObject obj)foreach(var child in obj.Select(p=>p.Value).ToArray())Visit(child);
    }
    public void FixTree(JsonArray tree)
    {
        foreach(var weapon in tree.OfType<JsonObject>().Where(i=>_weapons.Contains(S(i["_tpl"]))).ToArray())
        {
            var before=tree.ToJsonString();
            var tpl=S(weapon["_tpl"]);var root=S(weapon["_id"]);
            var descendants=new HashSet<string>{root};bool found;
            do {found=false;foreach(var i in tree.OfType<JsonObject>())if(descendants.Contains(S(i["parentId"])))found|=descendants.Add(S(i["_id"]));}while(found);
            var parts=tree.OfType<JsonObject>().Where(i=>descendants.Contains(S(i["_id"]))&&i!=weapon).ToArray();
            var barrel=Child(tree,weapon,"mod_barrel");
            if(barrel is null && rules["barrels"]?[tpl] is JsonValue barrelTpl)barrel=Add(tree,weapon,"mod_barrel",S(barrelTpl));
            if(barrel is not null)
                foreach(var part in parts.Where(p=>S(p["parentId"])==root))
                    if(S(part["slotId"]) is "mod_gas_block" or "mod_muzzle" or "mod_sight_rear" or "mod_sight_front" or "mod_launcher")
                        if(Slot(S(barrel["_tpl"]),S(part["slotId"])) is not null)part["parentId"]=S(barrel["_id"]);
            var gas=tree.OfType<JsonObject>().FirstOrDefault(i=>S(i["slotId"])=="mod_gas_block"&&(S(i["parentId"])==root||S(i["parentId"])==S(barrel?["_id"])));
            if(Slot(tpl,"mod_handguard") is not null)
            {
                var lower=Child(tree,weapon,"mod_handguard");
                var onGas=gas is null?null:Child(tree,gas,"mod_handguard");
                if(lower is null && onGas is not null && Accepts(tpl,"mod_handguard",S(onGas["_tpl"])))
                {lower=onGas;lower["parentId"]=root;}
                if(lower is null && gas is not null && rules["separatedLowers"]?[S(gas["_tpl"])] is JsonValue separated)
                    lower=Add(tree,weapon,"mod_handguard",S(separated));
                if(lower is not null)
                {
                    var lowerTpl=S(lower["_tpl"]);
                    if(gas is not null && rules["gasUppers"]?[lowerTpl] is JsonValue upper && Accepts(S(gas["_tpl"]),"mod_handguard",S(upper)))
                        Add(tree,gas,"mod_handguard",S(upper));
                    if(rules["lowerUppers"]?[lowerTpl] is JsonValue ownUpper && Accepts(lowerTpl,"mod_handguard",S(ownUpper)))
                        Add(tree,lower,"mod_handguard",S(ownUpper));
                }
            }
            // Reroute attachments displaced by the split, including scopes on upper guards and side rails.
            foreach(var part in parts)
            {
                if(S(part["slotId"]) is "cartridges" or "patron_in_weapon")continue;
                var parent=tree.OfType<JsonObject>().FirstOrDefault(i=>S(i["_id"])==S(part["parentId"]));
                if(parent is null)continue;
                var slot=S(part["slotId"]);var childTpl=S(part["_tpl"]);
                if(Accepts(S(parent["_tpl"]),slot,childTpl))continue;
                // The folding AKMS receiver uses specific stock/grip transform names.
                var rename=slot switch {"mod_stock"=>"mod_stock_akms","mod_pistol_grip"=>"mod_pistol_grip_akms",_=>""};
                if(rename!=""&&Accepts(S(parent["_tpl"]),rename,childTpl)){part["slotId"]=rename;continue;}
                var renamedSlot=Slots(S(parent["_tpl"])).Select(s=>S(s!["_name"])).FirstOrDefault(s=>Accepts(S(parent["_tpl"]),s,childTpl)&&Child(tree,parent,s) is null);
                if(renamedSlot is not null){part["slotId"]=renamedSlot;continue;}
                do {found=false;foreach(var i in tree.OfType<JsonObject>())if(descendants.Contains(S(i["parentId"])))found|=descendants.Add(S(i["_id"]));}while(found);
                var candidates=tree.OfType<JsonObject>().Where(i=>descendants.Contains(S(i["_id"]))).ToArray();
                var destination=candidates.FirstOrDefault(p=>p!=part && Accepts(S(p["_tpl"]),slot,childTpl)&&Child(tree,p,slot) is null);
                if(destination is not null){part["parentId"]=S(destination["_id"]);continue;}
                // Insert a custom side rail only when an existing optic needs it.
                foreach(var mountSlot in Slots(S(parent["_tpl"])).OfType<JsonObject>().Where(s=>S(s["_name"]).StartsWith("mod_mount")))
                {
                    var name=S(mountSlot["_name"]);
                    if(Child(tree,parent,name) is { } occupied && occupied!=part)continue;
                    var rail=mountSlot["_props"]?["filters"]?[0]?["Filter"]?.AsArray().Select(S).FirstOrDefault(id=>id.StartsWith("67597e")&&Slots(id).Any(s=>Accepts(id,S(s!["_name"]),childTpl)));
                    if(rail is null)continue;
                    part["parentId"]="";
                    var mount=Add(tree,parent,name,rail);part["parentId"]=S(mount["_id"]);
                    part["slotId"]=Slots(rail).Select(s=>S(s!["_name"])).First(s=>Accepts(rail,s,childTpl));
                    destination=mount;break;
                }
                if(destination is null && Slot(S(parent["_tpl"]),slot) is null)
                    Warnings.Add($"Cannot route {childTpl} in {slot} on {tpl}; kept the item for manual inspection.");
            }
            if(before!=tree.ToJsonString())RepairedWeapons++;
        }
    }
}
