using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Pathfind;
using Game.Policies;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.UI.InGame;
using Unity.Collections;
using Unity.Entities;

namespace TransitTimetables
{
    // Fleet-control helper (name kept for compatibility). No longer schedules anything itself — TimetableDispatchSystem
    // owns the timetables and calls the helpers here:
    //   * LineStableDurationUnits(line) — the line's round-trip time in route units, for deriving the fleet.
    //   * TrySetLineFleet(line, n) — set a line's vehicle count to n via the vanilla vehicle-count POLICY (the exact
    //     path the in-game slider uses), so vehicles spawn from depots / retire the same as dragging the slider.
    // The only thing its own OnUpdate does is the optional read-only shared-stop analysis.
    public partial class HourlyFleetSystem : GameSystemBase
    {
        private PrefabSystem m_PrefabSystem;
        private PoliciesUISystem m_Policies;
        private EntityQuery m_ConfigQuery;

        private Entity m_VehicleCountPolicy = Entity.Null;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Policies = World.GetOrCreateSystemManaged<PoliciesUISystem>();
            m_ConfigQuery = GetEntityQuery(ComponentType.ReadOnly<UITransportConfigurationData>());
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 256;

        protected override void OnUpdate()
        {
            Setting s = Mod.ActiveSetting;
            if (s == null)
                return;
            if (m_VehicleCountPolicy == Entity.Null)
                ResolvePolicy();
        }

        private void ResolvePolicy()
        {
            if (m_ConfigQuery.IsEmptyIgnoreFilter)
                return;
            UITransportConfigurationPrefab prefab = m_PrefabSystem.GetSingletonPrefab<UITransportConfigurationPrefab>(m_ConfigQuery);
            if (prefab != null && prefab.m_VehicleCountPolicy != null)
                m_VehicleCountPolicy = m_PrefabSystem.GetEntity(prefab.m_VehicleCountPolicy);
        }

        // Stable line duration in route "units" (segment travel + stop dwell). 0 if not measurable yet.
        public float LineStableDurationUnits(Entity line)
        {
            if (!EntityManager.HasComponent<PrefabRef>(line))
                return 0f;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (!EntityManager.HasComponent<TransportLineData>(prefab))
                return 0f;
            return ComputeStableDuration(line, EntityManager.GetComponentData<TransportLineData>(prefab));
        }

        // Set a line's vehicle count to an absolute target by writing the line's OWN VehicleInterval modifier directly
        // — NOT via the shared vehicle-count POLICY. TransportLineSystem derives the count from
        // value = m_DefaultVehicleInterval then RouteUtils.ApplyModifier(VehicleInterval) (value += delta.x;
        // value += value*delta.y). So delta.x = wanted - default makes value == wanted and the count == target,
        // UNCAPPED and PER-LINE. This is the fix for issue #2: the mod no longer widens the shared policy's modifier
        // RANGE (VehicleLimitSystem), which is what distorted the vanilla "Assigned Vehicles" slider on EVERY line.
        // The buffer is type-indexed, so pad it exactly the way vanilla's RouteModifierInitializeSystem.AddModifier
        // does. Must be re-asserted every tick by the dispatch: a line edit or route (re)creation rebuilds this buffer
        // from the line's policies (RouteModifierInitializeSystem runs on Created / a policy Modify). True if applied.
        public bool TrySetLineFleet(Entity line, int target)
        {
            if (!EntityManager.HasComponent<PrefabRef>(line) || !EntityManager.HasBuffer<RouteModifier>(line))
                return false;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (!EntityManager.HasComponent<TransportLineData>(prefab))
                return false;
            TransportLineData tld = EntityManager.GetComponentData<TransportLineData>(prefab);
            float duration = ComputeStableDuration(line, tld);
            if (duration <= 1f)
                return false;

            DynamicBuffer<RouteModifier> mods = EntityManager.GetBuffer<RouteModifier>(line);
            int idx = (int)RouteModifierType.VehicleInterval;
            while (mods.Length <= idx)
                mods.Add(default(RouteModifier));
            float wantX = TransportLineSystem.CalculateVehicleInterval(duration, target) - tld.m_DefaultVehicleInterval;
            RouteModifier m = mods[idx];
            if (m.m_Delta.x != wantX || m.m_Delta.y != 0f)
            {
                m.m_Delta.x = wantX;
                m.m_Delta.y = 0f;
                mods[idx] = m;
            }
            return true;
        }

        // Deactivate a mod-applied vehicle-count policy so the line reverts to vanilla's automatic vehicle count —
        // used when a timetable is switched off. Without it the line stays pinned at the last derived count, and that
        // override is serialized into the save with no timetable left to explain it. (A previously player-set manual
        // count on the same line is also cleared to automatic; re-pin it via the vanilla slider if wanted.)
        public bool TryClearLineFleet(Entity line)
        {
            // Reset the line's own VehicleInterval modifier to default (delta 0) so it reverts to vanilla's automatic
            // count immediately, and deactivate any vehicle-count policy an OLDER version of this mod may have set
            // (so it doesn't get restored by a later re-lerp). Robust regardless of re-lerp timing.
            if (EntityManager.HasBuffer<RouteModifier>(line))
            {
                DynamicBuffer<RouteModifier> mods = EntityManager.GetBuffer<RouteModifier>(line);
                int idx = (int)RouteModifierType.VehicleInterval;
                if (mods.Length > idx)
                {
                    RouteModifier m = mods[idx];
                    if (m.m_Delta.x != 0f || m.m_Delta.y != 0f) { m.m_Delta.x = 0f; m.m_Delta.y = 0f; mods[idx] = m; }
                }
            }
            if (m_VehicleCountPolicy == Entity.Null)
                ResolvePolicy();
            if (m_VehicleCountPolicy != Entity.Null)
                m_Policies.SetPolicy(line, m_VehicleCountPolicy, active: false);
            return true;
        }

        // Stable line duration = sum of segment path durations + stop dwell per timed stop. Mirrors
        // VehicleCountSection.CalculateVehicleCountJob.CalculateStableDuration so our count math matches the game's.
        private float ComputeStableDuration(Entity line, TransportLineData tld)
        {
            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, isReadOnly: true);
            DynamicBuffer<RouteSegment> segments = EntityManager.GetBuffer<RouteSegment>(line, isReadOnly: true);
            int len = waypoints.Length;
            if (len == 0 || segments.Length < len)
                return 0f;

            int start = 0;
            for (int i = 0; i < len; i++)
            {
                if (EntityManager.HasComponent<VehicleTiming>(waypoints[i].m_Waypoint))
                {
                    start = i;
                    break;
                }
            }

            float total = 0f;
            for (int j = 0; j < len; j++)
            {
                int segIdx = start + j;
                int wpIdx = segIdx + 1;
                if (segIdx >= len) segIdx -= len;
                if (wpIdx >= len) wpIdx -= len;
                Entity segment = segments[segIdx].m_Segment;
                Entity waypoint = waypoints[wpIdx].m_Waypoint;
                if (EntityManager.HasComponent<PathInformation>(segment))
                    total += EntityManager.GetComponentData<PathInformation>(segment).m_Duration;
                if (EntityManager.HasComponent<VehicleTiming>(waypoint))
                    total += tld.m_StopDuration;
            }
            return total;
        }
    }
}
