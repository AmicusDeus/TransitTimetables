using Colossal.Mathematics;
using Game;
using Game.Prefabs;
using Game.Routes;
using Unity.Entities;

namespace TransitTimetables
{
    // Raises the ceiling on how many vehicles a line may run, above the game's length/stops-derived maximum.
    //
    // A line's max vehicle count = CalculateVehicleCount(minInterval, lineDuration), where minInterval is bounded by
    // the VehicleInterval RouteModifierData range on the single vanilla vehicle-count POLICY (asset-authored, so its
    // numbers only exist at runtime). Making the interval-reducing end of that range more extreme lets the interval
    // go lower, so the same slider position — and this mod's own scheduling — can request more vehicles.
    //
    //   * Relative / InverseRelative mode (interval *= 1 + delta): newReducing = (1 + reducing)/M - 1. This scales
    //     every line's max count by exactly M regardless of the line's own default interval (the default cancels).
    //   * Absolute mode (interval += delta): newReducing = reducing * M (best-effort; not perfectly uniform).
    //
    // Default multiplier 1 = the policy is left exactly as vanilla (no write at all). The real policy ranges are
    // logged once so the effect can be verified. Idempotent: always recomputed from the captured ORIGINAL range.
    public partial class VehicleLimitSystem : GameSystemBase
    {
        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_ConfigQuery;
        private Entity m_Policy = Entity.Null;

        private bool m_HasOrig;
        private Bounds1 m_OrigRange;
        private ModifierValueMode m_Mode;
        private bool m_Logged;
        private int m_LastApplied = int.MinValue;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_ConfigQuery = GetEntityQuery(ComponentType.ReadOnly<UITransportConfigurationData>());
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 256;

        protected override void OnUpdate()
        {
            Setting s = Mod.ActiveSetting;
            if (s == null)
                return;

            if (m_Policy == Entity.Null)
            {
                if (m_ConfigQuery.IsEmptyIgnoreFilter)
                    return;
                UITransportConfigurationPrefab prefab = m_PrefabSystem.GetSingletonPrefab<UITransportConfigurationPrefab>(m_ConfigQuery);
                if (prefab == null || prefab.m_VehicleCountPolicy == null)
                    return;
                m_Policy = m_PrefabSystem.GetEntity(prefab.m_VehicleCountPolicy);
            }
            if (m_Policy == Entity.Null || !EntityManager.HasBuffer<RouteModifierData>(m_Policy))
                return;

            DynamicBuffer<RouteModifierData> mods = EntityManager.GetBuffer<RouteModifierData>(m_Policy);
            int idx = -1;
            for (int i = 0; i < mods.Length; i++)
            {
                if (mods[i].m_Type == RouteModifierType.VehicleInterval) { idx = i; break; }
            }
            if (idx < 0)
                return;

            if (!m_HasOrig)
            {
                m_OrigRange = mods[idx].m_Range;
                m_Mode = mods[idx].m_Mode;
                m_HasOrig = true;
            }
            if (!m_Logged)
            {
                m_Logged = true;
                Mod.log.Info($"[SelfTest] vehicleLimit: policy VehicleInterval modifier mode={m_Mode} range=[{m_OrigRange.min:F3},{m_OrigRange.max:F3}]");
            }

            int m = s.VehicleLimitMultiplier;
            // Timetabled lines derive their own fleet; auto-uncap so a derived count is never clamped by the cap.
            if (TimetableDispatchSystem.TimetableInUse && m < 8) m = 8;
            if (m < 1) m = 1;

            Bounds1 r = m_OrigRange;
            if (m > 1)
            {
                // Which end of the range yields MORE vehicles (shorter interval) depends on the modifier mode:
                //   InverseRelative (interval *= 1/(1+d)) -> larger d = shorter interval -> the MAX end.
                //   Relative        (interval *= 1+d)     -> more negative d = shorter interval -> the MIN end.
                //   Absolute        (interval += d)       -> more negative d = shorter interval -> the MIN end.
                if (m_Mode == ModifierValueMode.InverseRelative)
                {
                    bool maxIsBuses = r.max >= r.min;
                    float end = maxIsBuses ? r.max : r.min;
                    float widened = (1f + end) * m - 1f; // scales the max vehicle count by ~m
                    if (maxIsBuses) r.max = widened; else r.min = widened;
                }
                else if (m_Mode == ModifierValueMode.Relative)
                {
                    bool minIsBuses = r.min <= r.max;
                    float end = minIsBuses ? r.min : r.max;
                    float widened = (1f + end) / m - 1f;
                    if (widened < -0.95f) widened = -0.95f; // keep interval strictly positive
                    if (minIsBuses) r.min = widened; else r.max = widened;
                }
                else // Absolute
                {
                    bool minIsBuses = r.min <= r.max;
                    float end = minIsBuses ? r.min : r.max;
                    float widened = end * m;
                    if (minIsBuses) r.min = widened; else r.max = widened;
                }
            }

            RouteModifierData md = mods[idx];
            if (md.m_Range.min != r.min || md.m_Range.max != r.max)
            {
                md.m_Range = r;
                mods[idx] = md;
            }

            if (m != m_LastApplied)
            {
                m_LastApplied = m;
                Mod.log.Info($"[SelfTest] vehicleLimit: multiplier={m} → VehicleInterval range now [{r.min:F3},{r.max:F3}] (orig [{m_OrigRange.min:F3},{m_OrigRange.max:F3}])");
            }
        }
    }
}
