using System.Reflection;
using System.Text.Json.Nodes;
using HarmonyLib;
using JetBrains.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Generators.Bot;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace KalashnikovEnhancedModding.Patches;

public sealed class GenerateWeaponByTplPatch(TemplateTable templates, JsonObject rules) : AbstractPatch
{
    private static GenerateWeaponByTplPatch? _instance;

    protected override MethodBase GetTargetMethod()
    {
        _instance = this;
        return AccessTools.Method(typeof(BotWeaponGenerator), nameof(BotWeaponGenerator.GenerateWeaponByTpl));
    }

    [PatchPrefix, UsedImplicitly]
    private static void Prefix(MongoId weaponTpl, ref BotTypeInventory botTemplateInventory)
    {
        if (_instance is { } instance && instance._weapons.Contains(weaponTpl))
        {
            botTemplateInventory = instance.Prepare(weaponTpl, botTemplateInventory);
        }
    }
    
    private readonly HashSet<MongoId> _weapons =
    [ 
        .. rules["weaponIds"]!.AsArray().Select(id => (MongoId)MigrationEngine.GetString(id)) 
    ];
    
    private BotTypeInventory Prepare(MongoId weapon, BotTypeInventory inventory)
    {
        var pools = new Dictionary<MongoId, Dictionary<string, HashSet<MongoId>>>(inventory.Mods);
        var visited = new HashSet<MongoId>();
        var original = pools.GetValueOrDefault(weapon) ?? new Dictionary<string, HashSet<MongoId>>();
        var relocated = new Dictionary<string, HashSet<MongoId>>();
        
        foreach (var name in new[] { "mod_gas_block", "mod_muzzle", "mod_sight_rear", "mod_launcher" })
        {
            if (original.TryGetValue(name, out var choices))
            {
                relocated[name] = [.. choices];
            }
        }

        var lowers = new HashSet<MongoId>();
        if (original.TryGetValue("mod_gas_block", out var gases))
        {
            foreach (var gas in gases)
            {
                if (!pools.TryGetValue(gas, out var gasPool) 
                    || !gasPool.TryGetValue("mod_handguard", out var guards))
                {
                    continue;
                }

                foreach (var guard in guards)
                {
                    lowers.Add(rules["separatedLowers"] 
                        ? [guard.ToString()] is { } lower
                        ? (MongoId)MigrationEngine.GetString(lower) : guard);
                }
            }
        }

        Populate(weapon);
        return inventory with { Mods = pools };

        void Populate(MongoId tpl)
        {
            if (!visited.Add(tpl) || !templates.Items.TryGetValue(tpl, out var item))
            {
                return;
            }
            
            var slots = item.Properties?.Slots?.ToArray() ?? [];
            if (slots.Length == 0)
            {
                return;
            }
            
            var source = pools.GetValueOrDefault(tpl);
            var current = new Dictionary<string, HashSet<MongoId>>();

            if (source is not null)
            {
                foreach (var (name, choices) in source)
                {
                    if (!name.StartsWith("mod_", StringComparison.Ordinal))
                    {
                        current[name] = [.. choices];
                    }
                }
            }
            
            foreach (var slot in slots)
            {
                if (string.IsNullOrEmpty(slot.Name))
                {
                    continue;
                }
                var allowed = new HashSet<MongoId>();
                
                foreach (var filter in slot.Properties?.Filters ?? [])
                {
                    foreach (var id in filter.Filter ?? [])
                    {
                        if (templates.Items.ContainsKey(id))
                        {
                            allowed.Add(id);
                        }
                    }
                }
                
                var selected = new HashSet<MongoId>(source?.GetValueOrDefault(slot.Name) ?? []);
                selected.IntersectWith(allowed);
                
                if (selected.Count == 0 && tpl == weapon && slot.Name == "mod_handguard")
                {
                    selected.UnionWith(lowers);
                    selected.IntersectWith(allowed);
                }
                
                if (selected.Count == 0 && item.Parent.ToString() == "555ef6e44bdc2de9068b457e"
                    && relocated.TryGetValue(slot.Name, out var moved))
                {
                    selected.UnionWith(moved);
                    selected.IntersectWith(allowed);
                }
                
                if (selected.Count == 0 && slot.Name == "mod_barrel" && tpl == weapon
                    && rules["barrels"]?[weapon.ToString()] is { } barrel && allowed.Contains(MigrationEngine.GetString(barrel)))
                {
                    selected.Add(MigrationEngine.GetString(barrel));
                }
                
                if (selected.Count == 0 && (slot.Required == true || source is null || source.ContainsKey(slot.Name)))
                {
                    selected.UnionWith(allowed);
                }
                
                if (selected.Count == 0)
                {
                    continue;
                }
                
                current[slot.Name] = selected;
                
                foreach (var child in selected)
                {
                    Populate(child);
                }
            }
            pools[tpl] = current;
        }
    }
}
