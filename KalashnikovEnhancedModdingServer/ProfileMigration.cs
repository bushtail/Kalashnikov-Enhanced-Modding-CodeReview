using JetBrains.Annotations;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Servers;

namespace KalashnikovEnhancedModding;

[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.SaveCallbacks + 1), UsedImplicitly]
public sealed class ProfileMigration(
    KalashnikovEnhancedModding kalashnikovEnhancedModding,
    SaveServer saves,
    ISptLogger<ProfileMigration> logger) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        foreach (var profile in saves.GetProfiles().Values)
        {
            var migrated = kalashnikovEnhancedModding.Transform(profile);
            foreach (var property in profile.GetType().GetProperties().Where(p => p is
                     { CanRead: true, CanWrite: true } 
                         && p.GetIndexParameters().Length == 0))
            {
                property.SetValue(profile, property.GetValue(migrated));
            }
        }

        logger.Info("Kalashnikov weapon assembly migration completed for loaded profiles.");
        return Task.CompletedTask;
    }
}