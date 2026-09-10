using System.Reflection;
using JetBrains.Annotations;
using SPTarkov.Server.Core.Models.Spt.Mod;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace KalashnikovEnhancedModding;

[UsedImplicitly]
public record ModMetadata : IModMetadata
{
    private static string BuildValue(string key) => typeof(ModMetadata).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == key).Value!;
    
    public string ModGuid { get; init; } = BuildValue("ModGuid");
    public string Name { get; init; } = BuildValue("ModName");
    public string Author { get; init; } = BuildValue("ModAuthor");
    public List<string>? Contributors { get; init; } = 
    [
        "ChoccyMilk", 
        "Gatsu667"
    ];
    public Version Version { get; init; } = new(BuildValue("ModVersion"));
    public Range SptVersion { get; init; } = new("~4.1.0");
    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, Range>? ModDependencies { get; init; } = new()
    {
        ["com.wtt.commonlib"] = new Range(">=3.0.6 <4.0.0")
    };
    public string? Url { get; init; } = BuildValue("ModSourceUrl");
    public string License { get; init; } = "NCSA";
}