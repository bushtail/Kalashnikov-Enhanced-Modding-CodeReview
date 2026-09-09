using System.Reflection;
using System.Text.Json.Nodes;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;
using WTTServerCommonLib.Services;
using Version = SemanticVersioning.Version;
using Range = SemanticVersioning.Range;
using Path = System.IO.Path;

namespace KalashnikovEnhancedModding;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.baliston.kalashnikov-enhanced-modding";
    public string Name { get; init; } = "Kalashnikov Enhanced Modding";
    public string Author { get; init; } = "BALIST0N, ChoccyMilk & Gatsu667";
    public List<string>? Contributors { get; init; }
    public Version Version { get; init; } = new("4.1.1");
    public Range SptVersion { get; init; } = new("~4.1.0");
    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, Range>? ModDependencies { get; init; } = new() { ["com.wtt.commonlib"] = new(">=3.0.6 <4.0.0") };
    public string? Url { get; init; } = "https://github.com/BALIST0N/AkEnhancedModding";
    public string License { get; init; } = "NCSA";
}

[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.Preload + 100)]
public sealed class Mod(TemplateTable templates, TradersTable traders, GlobalTable globals, LocationTable locations,
    WTTCustomItemServiceExtended customItems, JsonUtil json, ISptLogger<Mod> logger) : IOnLoad
{
    public MigrationEngine? Engine { get; private set; }
    private readonly object _migrationLock = new();
    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var assembly=Assembly.GetExecutingAssembly();var dir=Path.GetDirectoryName(assembly.Location)!;
        var configs=JsonNode.Parse(File.ReadAllText(Path.Combine(dir,"db/CustomItems/parts.json")))!.AsObject();
        foreach(var (id,_) in configs)if(templates.Items.ContainsKey(id))throw new InvalidDataException($"Custom item ID collision: {id}");
        await customItems.CreateCustomItems(assembly);
        foreach(var (id,_) in configs)if(!templates.Items.ContainsKey(id))throw new InvalidDataException($"CommonLib failed to register {id}; see its preceding errors.");
        var offerCount=0;
        foreach(var (id,config) in configs)
            foreach(var (traderId,offers) in config?["traders"] as JsonObject??new JsonObject())
                foreach(var (offerId,_) in offers!.AsObject())
                {
                    if(!traders.TryGetValue(traderId,out var trader)||!trader.Assort.Items.Any(i=>i.Id.ToString()==offerId&&i.Template.ToString()==id)||!trader.Assort.BarterScheme.ContainsKey(offerId))
                        throw new InvalidDataException($"CommonLib did not create trader offer {offerId} for {id}.");
                    offerCount++;
                }
        var items=JsonNode.Parse(json.Serialize(templates.Items)!)!.AsObject();
        var rules=JsonNode.Parse(File.ReadAllText(Path.Combine(dir,"db/migration.json")))!.AsObject();
        Engine=new MigrationEngine(items,rules);
        var patches=JsonNode.Parse(File.ReadAllText(Path.Combine(dir,"db/patches.json")))!.AsArray();
        Engine.PatchTemplates(patches);
        foreach(var patch in patches){var id=MigrationEngine.S(patch!["_id"]);templates.Items[id]=json.Deserialize<TemplateItem>(items[id]!.ToJsonString())!;}
        foreach(var preset in globals.ItemPresets.Values)Repair(preset.Items);
        foreach(var trader in traders.Values)if(trader.Assort?.Items is {} itemsList)Repair(itemsList);
        foreach(var (id,quest) in templates.Quests.ToArray())templates.Quests[id]=Transform(quest);
        foreach(var (id,profile) in templates.Profiles.ToArray())templates.Profiles[id]=Transform(profile);
        foreach(var location in locations.GetDictionary().Values)
        {
            location.LooseLoot?.AddTransformer(loot=>loot is null?null:loot with
            {
                Spawnpoints=loot.Spawnpoints?.Select(p=>p with {Template=MigrateLoot(p.Template)}).ToList()??[],
                SpawnpointsForced=loot.SpawnpointsForced?.Select(p=>p with {Template=MigrateLoot(p.Template)}).ToList()
            });
            location.StaticContainers?.AddTransformer(loot=>loot is null?null:loot with
            {StaticWeapons=loot.StaticWeapons?.Select(p=>MigrateLoot(p)!).ToList()??[]});
        }
        new BotWeaponPools(templates,rules).Enable();
        foreach(var warning in Engine.Warnings)logger.Warning(warning);
        logger.Info($"Registered {configs.Count} custom parts and {offerCount} trader offers with CommonLib; repaired {Engine.RepairedWeapons} database weapon assemblies.");
    }
    public T Transform<T>(T value)
    {
        var node=JsonNode.Parse(json.Serialize(value)!);
        lock(_migrationLock)Engine!.Visit(node);
        return json.Deserialize<T>(node!.ToJsonString())!;
    }
    private SpawnpointTemplate? MigrateLoot(SpawnpointTemplate? template)
    {
        if(template?.Items is null||!template.Items.Any(i=>Engine!.IsWeapon(i.Template.ToString())))return template;
        return template with {Items=Transform(template.Items)};
    }
    public void Repair(List<Item> items)
    {
        var updated=Transform(items);items.Clear();items.AddRange(updated);
    }
}

[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.SaveCallbacks + 1)]
public sealed class ProfileMigration(Mod mod, SaveServer saves, ISptLogger<ProfileMigration> logger) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        foreach(var profile in saves.GetProfiles().Values)
        {
            // Migrate all item trees in memory, including scav inventory, builds, rewards and mail attachments.
            // SPT's normal save path persists them after its profile backup/load stage.
            var migrated=mod.Transform(profile);
            foreach(var property in profile.GetType().GetProperties().Where(p=>p.CanRead&&p.CanWrite&&p.GetIndexParameters().Length==0))
                property.SetValue(profile,property.GetValue(migrated));
        }
        logger.Info("Kalashnikov weapon assembly migration completed for loaded profiles.");
        return Task.CompletedTask;
    }
}

