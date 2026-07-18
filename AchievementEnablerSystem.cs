using Colossal.PSI.Common;
using Game;
using Unity.Entities;

namespace TransitTimetables
{
    // Re-enables platform achievements in modded sessions (the game disables them whenever any mod is active). The
    // vanilla check keeps re-disabling, so this re-asserts the flag periodically while the toggle is on. Idempotent.
    public partial class AchievementEnablerSystem : GameSystemBase
    {
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 256;

        protected override void OnUpdate()
        {
            Setting s = Mod.ActiveSetting;
            if (s != null && s.EnableAchievements && PlatformManager.instance != null)
                PlatformManager.instance.achievementsEnabled = true;
        }
    }
}
