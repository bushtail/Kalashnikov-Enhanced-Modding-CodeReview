using System.Reflection;
using System.Text.Json.Nodes;
using JetBrains.Annotations;
using KalashnikovEnhancedModding.Patches;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;
using WTTServerCommonLib.Services;
using Path = System.IO.Path;

namespace KalashnikovEnhancedModding;

[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.Preload + 100), UsedImplicitly]
public sealed class KalashnikovEnhancedModding(
    TemplateTable templates,
    TradersTable traders,
    GlobalTable globals,
    LocationTable locations,
    WTTCustomItemServiceExtended customItems,
    JsonUtil json,
    ISptLogger<KalashnikovEnhancedModding> logger) : IOnLoad
{
    private MigrationEngine? Engine { get; set; }
    private readonly Lock _migrationLock = new();

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var dir = Path.GetDirectoryName(assembly.Location)!;
        var configs = JsonNode.Parse(await 
            File.ReadAllTextAsync(Path.Combine(dir, "db/CustomItems/parts.json"), cancellationToken))!.AsObject();
        
        foreach (var (id, _) in configs)
        {
            if (templates.Items.ContainsKey(id))
            {
                throw new InvalidDataException($"Custom item ID collision: {id}");
            }
        }
        
        await customItems.CreateCustomItems(assembly);
        
        foreach (var (id, _) in configs)
        {
            if (!templates.Items.ContainsKey(id))
            {
                throw new InvalidDataException($"CommonLib failed to register {id}; see its preceding errors.");
            }
        }
        
        var offerCount = 0;
        foreach (var (id, config) in configs)
        {
            foreach (var (traderId, offers) in config?["traders"] as JsonObject ?? new JsonObject())
            {
                foreach (var (offerId, _) in offers!.AsObject())
                {
                    if (!traders.TryGetValue(traderId, out var trader) 
                        || !trader.Assort.Items.Any(i => i.Id.ToString() == offerId && i.Template.ToString() == id)
                        || !trader.Assort.BarterScheme.ContainsKey(offerId))
                    {
                        throw new InvalidDataException($"CommonLib did not create trader offer {offerId} for {id}.");
                    }
                    offerCount++;
                }
            }
        }

        var items = JsonNode.Parse(json.Serialize(templates.Items)!)!.AsObject();
        var rules = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "db/migration.json"), cancellationToken))!.AsObject();
        
        Engine = new MigrationEngine(items, rules);
        
        var patches = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "db/patches.json"), cancellationToken))!.AsArray();
        
        Engine.PatchTemplates(patches);
        
        foreach (var patch in patches)
        {
            var id = MigrationEngine.GetString(patch!["_id"]);
            templates.Items[id] = json.Deserialize<TemplateItem>(items[id]!.ToJsonString())!;
        }

        foreach (var preset in globals.ItemPresets.Values)
        {
            Repair(preset.Items);
        }
        foreach (var trader in traders.Values)
        {
            if (trader.Assort.Items is { } itemsList)
            {
                Repair(itemsList);
            }
        }
        
        foreach (var (id, quest) in templates.Quests.ToArray())
        {
            templates.Quests[id] = Transform(quest);
        }
        
        foreach (var (id, profile) in templates.Profiles.ToArray())
        {
            templates.Profiles[id] = Transform(profile);
        }
        
        foreach (var location in locations.GetDictionary().Values)
        {
            location.LooseLoot?.AddTransformer(loot => loot is null ? null : loot with
                {
                    Spawnpoints = loot.Spawnpoints?.Select(p =>
                            p with { Template = MigrateLoot(p.Template) }).ToList() ?? [],
                    
                    SpawnpointsForced = loot.SpawnpointsForced?.Select(p => 
                            p with { Template = MigrateLoot(p.Template) }).ToList()
                }
            );
            
            location.StaticContainers?.AddTransformer(loot => loot is null ? null : loot with
                {
                    StaticWeapons = [ .. loot.StaticWeapons.Select(p => MigrateLoot(p)!) ]
                }
            );
        }

        new GenerateWeaponByTplPatch(templates, rules).Enable();
        foreach (var warning in Engine.Warnings)
        {
            logger.Warning(warning);
        }
        
        logger.Info($"Registered {configs.Count} custom parts and {offerCount} trader offers with CommonLib; " +
                    $"repaired {Engine.RepairedWeapons} database weapon assemblies.");
    }

    public T Transform<T>(T value)
    {
        var node = JsonNode.Parse(json.Serialize(value)!);
        lock (_migrationLock)
        {
            Engine!.Visit(node);
        }
        return json.Deserialize<T>(node!.ToJsonString())!;
    }

    private SpawnpointTemplate? MigrateLoot(SpawnpointTemplate? template)
    {
        if (template?.Items is null || !template.Items.Any(i => Engine!.IsWeapon(i.Template.ToString())))
        {
            return template;
        }
        
        return template with { Items = Transform(template.Items) };
    }

    private void Repair(List<Item> items)
    {
        var updated = Transform(items);
        items.Clear();
        items.AddRange(updated);
    }
}
