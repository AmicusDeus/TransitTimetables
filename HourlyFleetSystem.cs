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
        private EntityQuery m_LineQuery;
        private EntityQuery m_ConfigQuery;

        private Entity m_VehicleCountPolicy = Entity.Null;
        private bool m_Analyzed;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Policies = World.GetOrCreateSystemManaged<PoliciesUISystem>();
            m_ConfigQuery = GetEntityQuery(ComponentType.ReadOnly<UITransportConfigurationData>());
            m_LineQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Route>(),
                    ComponentType.ReadOnly<TransportLine>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<RouteWaypoint>(),
                    ComponentType.ReadOnly<RouteSegment>(),
                },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() },
            });
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 256;

        protected override void OnUpdate()
        {
            Setting s = Mod.ActiveSetting;
            if (s == null)
                return;
            if (m_VehicleCountPolicy == Entity.Null)
                ResolvePolicy();
            MaybeAnalyze(s);
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

        // Set a line's vehicle count to an absolute target via the vanilla vehicle-count policy. True if applied.
        public bool TrySetLineFleet(Entity line, int target)
        {
            if (m_VehicleCountPolicy == Entity.Null)
            {
                ResolvePolicy();
                if (m_VehicleCountPolicy == Entity.Null)
                    return false;
            }
            if (!EntityManager.HasComponent<PrefabRef>(line))
                return false;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (!EntityManager.HasComponent<TransportLineData>(prefab))
                return false;
            TransportLineData tld = EntityManager.GetComponentData<TransportLineData>(prefab);
            float duration = ComputeStableDuration(line, tld);
            if (duration <= 1f)
                return false;
            if (TryCalcAdjustment(target, tld.m_DefaultVehicleInterval, duration, out float adj))
            {
                m_Policies.SetPolicy(line, m_VehicleCountPolicy, active: true, adj);
                return true;
            }
            return false;
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

        // Policy adjustment that makes the vehicle-count policy yield `targetCount` vehicles. Replicates
        // VehicleCountSection.CalculateVehicleCountJob.CalculateAdjustmentFromVehicleCount (nested in a private
        // struct, so uncallable). Returns false if the policy has no VehicleInterval modifier (it always does).
        private bool TryCalcAdjustment(int targetCount, float originalInterval, float duration, out float adjustment)
        {
            adjustment = 0f;
            float wanted = TransportLineSystem.CalculateVehicleInterval(duration, targetCount);
            DynamicBuffer<RouteModifierData> modifierDatas = EntityManager.GetBuffer<RouteModifierData>(m_VehicleCountPolicy, isReadOnly: true);
            PolicySliderData slider = EntityManager.GetComponentData<PolicySliderData>(m_VehicleCountPolicy);
            for (int i = 0; i < modifierDatas.Length; i++)
            {
                RouteModifierData item = modifierDatas[i];
                if (item.m_Type != RouteModifierType.VehicleInterval)
                    continue;
                RouteModifier modifier = default;
                if (item.m_Mode == ModifierValueMode.Absolute)
                    modifier.m_Delta.x = wanted - originalInterval;
                else
                    modifier.m_Delta.y = (0f - originalInterval + wanted) / originalInterval;
                float delta = RouteModifierInitializeSystem.RouteModifierRefreshData.GetDeltaFromModifier(modifier, item);
                adjustment = RouteModifierInitializeSystem.RouteModifierRefreshData.GetPolicyAdjustmentFromModifierDelta(item, delta, slider);
                return true;
            }
            return false;
        }

        // Read-only: how many stops are shared by two or more lines (informational). Runs once per session.
        private void MaybeAnalyze(Setting s)
        {
            if (!s.AnalyzeSharedStops || m_Analyzed)
                return;
            m_Analyzed = true;

            var stopLineCount = new Dictionary<Entity, int>();
            NativeArray<Entity> lines = m_LineQuery.ToEntityArray(Allocator.Temp);
            int lineCount = lines.Length;
            for (int i = 0; i < lines.Length; i++)
            {
                DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(lines[i], isReadOnly: true);
                var seen = new HashSet<Entity>();
                for (int j = 0; j < waypoints.Length; j++)
                {
                    Entity wp = waypoints[j].m_Waypoint;
                    if (!EntityManager.HasComponent<Connected>(wp))
                        continue;
                    Entity stop = EntityManager.GetComponentData<Connected>(wp).m_Connected;
                    if (stop == Entity.Null || !seen.Add(stop))
                        continue;
                    stopLineCount.TryGetValue(stop, out int c);
                    stopLineCount[stop] = c + 1;
                }
            }
            lines.Dispose();

            int shared = 0, maxShare = 0;
            foreach (var kv in stopLineCount)
            {
                if (kv.Value >= 2) shared++;
                if (kv.Value > maxShare) maxShare = kv.Value;
            }
            Mod.log.Info($"[SelfTest] timetable: sharedStopAnalysis lines={lineCount} stops={stopLineCount.Count} sharedByTwoOrMore={shared} busiestStopLines={maxShare}");
        }
    }
}
