using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace KalashnikovEnhancedModding;

public sealed class MigrationEngine(JsonObject templates, JsonObject rules)
{
    public int RepairedWeapons { get; private set; }
    
    public HashSet<string> Warnings { get; } = [];
    private readonly HashSet<string> _weapons = [ .. rules["weaponIds"]!.AsArray().Select(GetString) ];
    
    public bool IsWeapon(string templateId) => _weapons.Contains(templateId);
    
    public static string GetString(JsonNode? n) => n?.GetValue<string>() ?? "";

    private static string Id(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("kalashnikov-enhanced:" + key)))[..24];

    private JsonArray Slots(string tpl) => templates[tpl]?["_props"]?["Slots"] as JsonArray ?? [];

    private JsonObject? Slot(string tpl, string name) =>
        Slots(tpl).OfType<JsonObject>().FirstOrDefault(s => GetString(s["_name"]) == name);

    private bool Accepts(string tpl, string slot, string child) =>
        Slot(tpl, slot)?["_props"]?["filters"] is JsonArray filters &&
        filters.Any(f => f?["Filter"] is JsonArray a && a.Any(v => GetString(v) == child));

    private static JsonObject? Child(JsonArray tree, JsonObject parent, string slot) => tree.OfType<JsonObject>()
        .FirstOrDefault(i => GetString(i["parentId"]) == GetString(parent["_id"]) && GetString(i["slotId"]) == slot);

    private static JsonObject Add(JsonArray tree, JsonObject parent, string slot, string tpl)
    {
        var existing = Child(tree, parent, slot);
        
        if (existing is not null)
        {
            return existing;
        }
        
        var key = GetString(parent["_id"]) + ":" + slot;
        var id = Id(key);
        var salt = 0;
        
        while (tree.Any(i => GetString(i?["_id"]) == id))
        {
            id = Id(key + ":" + ++salt);
        }
        
        var item = new JsonObject
        {
            ["_id"] = id, 
            ["_tpl"] = tpl, 
            ["parentId"] = GetString(parent["_id"]), 
            ["slotId"] = slot
        };
        tree.Add(item);
        
        return item;
    }

    public void PatchTemplates(JsonArray patches)
    {
        foreach (var patch in patches.OfType<JsonObject>())
        {
            var id = GetString(patch["_id"]);
            if (templates[id] is not JsonObject item)
            {
                throw new InvalidDataException($"Missing SPT template {id}");
            }
            
            var props = item["_props"]!.AsObject();
            
            foreach (var (key, value) in patch["_props"]!.AsObject())
            {
                var replacement = value?.DeepClone();
                
                if (key == "Slots" && replacement is JsonArray slots)
                {
                    foreach (var slot in slots.OfType<JsonObject>())
                    {
                        var name = GetString(slot["_name"]);
                        var old = Slot(id, name);
                        if (name is "mod_stock" or "mod_magazine" or "mod_pistol_grip" or "mod_charge")
                        {
                            if (old?["_props"]?["filters"]?[0]?["Filter"] is JsonArray prior 
                                && slot["_props"]?["filters"]?[0]?["Filter"] is JsonArray target)
                            {
                                foreach (var entry in prior)
                                {
                                    if (!target.Any(v => GetString(v) == GetString(entry)))
                                    {
                                        target.Add(entry!.DeepClone());
                                    }
                                }
                            }
                        }
                        
                        slot["_id"] = Id(id + ":" + name);
                        slot["_parent"] = id;
                    }
                }

                props[key] = replacement;
            }
        }
    }

    public void Visit(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray array:
            {
                if (array.OfType<JsonObject>().Any(i => 
                        i.ContainsKey("_tpl") 
                        && i.ContainsKey("_id")))
                {
                    FixTree(array);
                }
                
                foreach (var child in array.ToArray())
                {
                    Visit(child);
                }
                
                break;
            }
            case JsonObject obj:
            {
                foreach (var child in obj
                             .Select(p => p.Value)
                             .ToArray())
                {
                    Visit(child);
                }
                
                break;
            }
        }
    }

    private void FixTree(JsonArray tree)
    {
        foreach (var weapon in tree.OfType<JsonObject>().Where(i => _weapons.Contains(GetString(i["_tpl"]))).ToArray())
        {
            var before = tree.ToJsonString();
            var tpl = GetString(weapon["_tpl"]);
            var root = GetString(weapon["_id"]);
            var descendants = new HashSet<string> { root };
            bool found;
            
            do
            {
                found = false;
                
                foreach (var i in tree.OfType<JsonObject>())
                {
                    if (descendants.Contains(GetString(i["parentId"])))
                    {
                        found |= descendants.Add(GetString(i["_id"]));
                    }
                }
            } while (found);

            var parts = tree.OfType<JsonObject>()
                .Where(i => descendants.Contains(GetString(i["_id"])) && i != weapon)
                .ToArray();
            
            var barrel = Child(tree, weapon, "mod_barrel");
            
            if (barrel is null && rules["barrels"]?[tpl] is JsonValue barrelTpl)
            {
                barrel = Add(tree, weapon, "mod_barrel", GetString(barrelTpl));
            }
            
            if (barrel is not null)
            {
                foreach (var part in parts.Where(p => GetString(p["parentId"]) == root))
                {
                    if (GetString(part["slotId"]) is not ("mod_gas_block" 
                        or "mod_muzzle" or "mod_sight_rear"
                        or "mod_sight_front" or "mod_launcher"))
                    {
                        continue;
                    }

                    if (Slot(GetString(barrel["_tpl"]), GetString(part["slotId"])) is not null)
                    {
                        part["parentId"] = GetString(barrel["_id"]);
                    }
                }
            }
            
            var gas = tree.OfType<JsonObject>().FirstOrDefault(
                i => GetString(i["slotId"]) == "mod_gas_block" 
                     && (GetString(i["parentId"]) == root || GetString(i["parentId"]) == GetString(barrel?["_id"])));
            
            if (Slot(tpl, "mod_handguard") is not null)
            {
                var lower = Child(tree, weapon, "mod_handguard");
                var onGas = gas is null ? null : Child(tree, gas, "mod_handguard");
                
                if (lower is null && onGas is not null && Accepts(tpl, "mod_handguard", GetString(onGas["_tpl"])))
                {
                    lower = onGas;
                    lower["parentId"] = root;
                }

                if (lower is null 
                    && gas is not null 
                    && rules["separatedLowers"]?[GetString(gas["_tpl"])] is JsonValue separated)
                {
                    lower = Add(tree, weapon, "mod_handguard", GetString(separated));
                }
                
                if (lower is not null)
                {
                    var lowerTpl = GetString(lower["_tpl"]);
                    
                    if (gas is not null 
                        && rules["gasUppers"]?[lowerTpl] is JsonValue upper 
                        && Accepts(GetString(gas["_tpl"]), "mod_handguard", GetString(upper)))
                    {
                        Add(tree, gas, "mod_handguard", GetString(upper));
                    }
                    
                    if (rules["lowerUppers"]?[lowerTpl] is JsonValue ownUpper 
                        && Accepts(lowerTpl, "mod_handguard", GetString(ownUpper)))
                    {
                        Add(tree, lower, "mod_handguard", GetString(ownUpper));
                    }
                }
            }

            foreach (var part in parts)
            {
                if (GetString(part["slotId"]) is "cartridges" or "patron_in_weapon")
                {
                    continue;
                }
                
                var parent = tree.OfType<JsonObject>().FirstOrDefault(i => GetString(i["_id"]) == GetString(part["parentId"]));
                if (parent is null)
                {
                    continue;
                }
                
                var slot = GetString(part["slotId"]);
                var childTpl = GetString(part["_tpl"]);
                
                if (Accepts(GetString(parent["_tpl"]), slot, childTpl))
                {
                    continue;
                }
                
                var rename = slot switch
                {
                    "mod_stock" => "mod_stock_akms", 
                    "mod_pistol_grip" => "mod_pistol_grip_akms", 
                    _ => ""
                };
                
                if (rename != "" && Accepts(GetString(parent["_tpl"]), rename, childTpl))
                {
                    part["slotId"] = rename;
                    continue;
                }

                var renamedSlot = Slots(GetString(parent["_tpl"]))
                    .Select(s => GetString(s!["_name"])).FirstOrDefault(s => 
                        Accepts(GetString(parent["_tpl"]), s, childTpl) && Child(tree, parent, s) is null);
                
                if (renamedSlot is not null)
                {
                    part["slotId"] = renamedSlot;
                    continue;
                }
                
                do
                {
                    found = false;
                    foreach (var i in tree.OfType<JsonObject>())
                    {
                        if (descendants.Contains(GetString(i["parentId"])))
                        {
                            found |= descendants.Add(GetString(i["_id"]));
                        }
                    }
                } while (found);

                var candidates = tree.OfType<JsonObject>()
                    .Where(i => descendants.Contains(GetString(i["_id"]))).ToArray();
                
                var destination = candidates
                    .FirstOrDefault(p => p != part 
                                         && Accepts(GetString(p["_tpl"]), slot, childTpl) 
                                         && Child(tree, p, slot) is null);
                
                if (destination is not null)
                {
                    part["parentId"] = GetString(destination["_id"]);
                    continue;
                }

                foreach (var mountSlot in Slots(GetString(parent["_tpl"])).OfType<JsonObject>()
                             .Where(s => GetString(s["_name"]).StartsWith("mod_mount")))
                {
                    var name = GetString(mountSlot["_name"]);
                    
                    if (Child(tree, parent, name) is { } occupied && occupied != part)
                    {
                        continue;
                    }
                    
                    var rail = mountSlot["_props"]?["filters"]?[0]?["Filter"]?
                        .AsArray()
                        .Select(GetString).FirstOrDefault(
                            id => id.StartsWith("67597e") 
                                  && Slots(id).Any(
                                      s => Accepts(id, GetString(s!["_name"]), childTpl)));
                    
                    if (rail is null)
                    {
                        continue;
                    }
                    
                    part["parentId"] = "";
                    
                    var mount = Add(tree, parent, name, rail);
                    part["parentId"] = GetString(mount["_id"]);
                    part["slotId"] = Slots(rail).Select(s => GetString(s!["_name"])).First(s => Accepts(rail, s, childTpl));
                    
                    destination = mount;
                    break;
                }

                if (destination is null && Slot(GetString(parent["_tpl"]), slot) is null)
                {
                    Warnings.Add($"Cannot route {childTpl} in {slot} on {tpl}; kept the item for manual inspection.");
                }
            }

            if (before != tree.ToJsonString())
            {
                RepairedWeapons++;
            }
        }
    }
}