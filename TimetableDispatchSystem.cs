using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Pathfind;
using Game.Routes;
using Game.Simulation;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using PublicTransport = Game.Vehicles.PublicTransport;

namespace TransitTimetables
{
    // T2 — fixed-departure timetabling for opted-in lines. Owns three things per timetabled line:
    //
    //  1. FLEET: derived from the current window's headway (round-trip / interval) and applied via the vanilla
    //     vehicle-count policy (HourlyFleetSystem.TrySetLineFleet). The player sets departures, the fleet follows.
    //  2. NO MID-ROUTE IDLE: zeroes TransportLine.m_UnbunchingFactor so the vehicle never waits at intermediate
    //     stops to even out spacing — it leaves as soon as boarding/alighting completes.
    //  3. TERMINUS HOLD (the timing point): the vehicle boarding at the first stop is held to the next scheduled
    //     clock departure if it is EARLY (writes PublicTransport.m_DepartureFrame); if on-time or late it departs
    //     immediately. Holding only at the terminus keeps it from blocking other lines mid-route (single-slot stops).
    //
    // Runs every 8 frames so it always re-asserts the hold before the vanilla 16-frame AI release.
    public partial class TimetableDispatchSystem : GameSystemBase
    {
        private SimulationSystem m_Sim;
        private TimeSystem m_Time;
        private HourlyFleetSystem m_Fleet;
        private EntityQuery m_LineQuery;
        private readonly Dictionary<Entity, int> m_LastFleet = new Dictionary<Entity, int>();
        // Per line: vehicles the game flagged to retire that we're driving to the terminus before letting them go.
        private readonly Dictionary<Entity, HashSet<Entity>> m_PendingRetire = new Dictionary<Entity, HashSet<Entity>>();
        // Per line: buses seen AWAY from the terminus (serving the loop) since they appeared — i.e. that have earned
        // a full loop and may now retire on their next return. A freshly-deployed bus is absent from this set until
        // it leaves the terminus, so it always completes one serving lap before it can be recalled.
        private readonly Dictionary<Entity, HashSet<Entity>> m_LapServed = new Dictionary<Entity, HashSet<Entity>>();
        private uint m_LastLog;

        // Read by VehicleLimitSystem to auto-uncap the vehicle ceiling while any line is timetabled.
        public static bool TimetableInUse;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Time = World.GetOrCreateSystemManaged<TimeSystem>();
            m_Fleet = World.GetOrCreateSystemManaged<HourlyFleetSystem>();
            m_LineQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Route>(),
                    ComponentType.ReadWrite<TransportLine>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<RouteWaypoint>(),
                    ComponentType.ReadOnly<TimetableSchedule>(),
                },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() },
            });
            RequireForUpdate(m_LineQuery);
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 8;

        protected override void OnUpdate()
        {
            Setting s = Mod.ActiveSetting;
            if (s == null)
                return;

            uint frame = m_Sim.frameIndex;
            int nowMin = (int)(m_Time.normalizedTime * 1440f) % 1440;

            NativeArray<Entity> lines = m_LineQuery.ToEntityArray(Allocator.Temp);
            bool anyEnabled = false;
            int enabledCount = 0;
            string sample = null;
            for (int i = 0; i < lines.Length; i++)
            {
                Entity line = lines[i];
                TimetableSchedule sch = EntityManager.GetComponentData<TimetableSchedule>(line);
                TransportLine tl = EntityManager.GetComponentData<TransportLine>(line);

                if (!sch.m_Enabled)
                {
                    RestoreUnbunching(line, tl);
                    m_LastFleet.Remove(line);
                    m_PendingRetire.Remove(line);
                    m_LapServed.Remove(line);
                    continue;
                }
                anyEnabled = true;
                enabledCount++;

                // (2) never idle at intermediate stops
                if (tl.m_UnbunchingFactor != 0f)
                {
                    tl.m_UnbunchingFactor = 0f;
                    EntityManager.SetComponentData(line, tl);
                }

                // The line's day/night operating schedule — which intervals apply and when it runs.
                int sched = LineSchedule.Of(EntityManager, line);

                // (1) derive + apply fleet for the current headway
                int desiredFleet = 0;
                float durUnits = m_Fleet.LineStableDurationUnits(line);
                if (durUnits > 1f)
                {
                    int interval = ScheduleMath.IntervalFor(s, sch, nowMin, sched);
                    desiredFleet = ScheduleMath.DerivedFleet(durUnits, interval);
                    m_LastFleet.TryGetValue(line, out int last);
                    if (desiredFleet != last && m_Fleet.TrySetLineFleet(line, desiredFleet))
                        m_LastFleet[line] = desiredFleet;
                }

                // (3) terminus = timing point + retirement anchor (player-chosen stop, or the first stop)
                FindTerminus(line, sch, out Entity terminusStop, out Entity terminusWaypoint);

                // (3a) FULL TIMETABLE: hold EACH stop's boarding bus to that stop's scheduled departure — the terminus
                // schedule shifted by the stop's cumulative travel offset from the terminus (offset 0 at the terminus).
                // Offsets come from the route itself: each RouteSegment's PathInformation.m_Duration (60-frame route
                // units) summed from the terminus, converted to schedule minutes via ScheduleMath.UnitMinutes.
                int curInterval = ScheduleMath.IntervalFor(s, sch, nowMin, sched);
                bool diagLog = frame - m_LastLog >= 16384; // [SelfTest] cadence — dump the hold's numbers periodically
                HoldAllStops(line, s, sch, sched, terminusStop, terminusWaypoint, frame, nowMin, curInterval, diagLog);

                // (3b) SLOT-COUPLED DRAIN: shed surplus buses at the terminus WITHOUT skipping departures.
                //
                // When the schedule widens the headway (e.g. 15 buses/4min -> 4 buses/15min) the game wants to cull the
                // extras by odometer (AbandonRoute) and would retire each wherever it sits — dumping passengers mid-route
                // and, worse, retiring the very buses due to depart, so several scheduled departures in a row go unserved
                // (the "10:00/10:10/10:20 dead slots" case). We take ownership of the cull instead.
                //
                // The key: the terminus timing-point bus is held (3a) to the next scheduled departure and OCCUPIES the
                // single boarding spot for the whole headway. So while a bus is boarding the terminus, this slot's
                // departure is guaranteed (exactly one bus leaves per slot) — and ONLY then may an extra that has arrived
                // behind it retire. That gate ("slotCovered") is what turns a burst of retirements into a trickle: one
                // bus departs each slot, the extras drain in the gaps, the fleet glides down to target with no missed
                // departure, and retirement stops on its own once surplus hits zero. Lap-before-retire is preserved
                // (a freshly-deployed peak bus completes one serving loop before it can be recalled), and if the fleet
                // is raised back up the surplus vanishes and every pending retirement is forgotten.
                if (terminusWaypoint != Entity.Null && EntityManager.HasBuffer<RouteVehicle>(line))
                {
                    DynamicBuffer<RouteVehicle> vehicles = EntityManager.GetBuffer<RouteVehicle>(line, isReadOnly: true);
                    if (!m_PendingRetire.TryGetValue(line, out HashSet<Entity> pending))
                        m_PendingRetire[line] = pending = new HashSet<Entity>();
                    if (!m_LapServed.TryGetValue(line, out HashSet<Entity> lapServed))
                        m_LapServed[line] = lapServed = new HashSet<Entity>();

                    // Is a bus boarding the terminus right now as the timing-point front? While one is, this slot's
                    // departure is covered — it holds the single boarding spot until its scheduled time — so extras may
                    // retire without skipping a departure. No front boarding => hold off, let a bus flow through first.
                    bool slotCovered = false;
                    if (terminusStop != Entity.Null && EntityManager.HasComponent<BoardingVehicle>(terminusStop))
                    {
                        Entity frontVeh = EntityManager.GetComponentData<BoardingVehicle>(terminusStop).m_Vehicle;
                        if (frontVeh != Entity.Null && EntityManager.HasComponent<PublicTransport>(frontVeh))
                        {
                            PublicTransport fpt = EntityManager.GetComponentData<PublicTransport>(frontVeh);
                            slotCovered = (fpt.m_State & PublicTransportFlags.Boarding) != 0
                                       && (fpt.m_State & PublicTransportFlags.EnRoute) != 0;
                        }
                    }

                    // Pass 1: count live buses and mark lap-eligibility. A bus whose current target is NOT the terminus
                    // has left the terminus and is serving the loop, so it has earned a retirement on its next return.
                    HashSet<Entity> live = new HashSet<Entity>();
                    int liveCount = 0;
                    for (int v = 0; v < vehicles.Length; v++)
                    {
                        Entity veh = vehicles[v].m_Vehicle;
                        if (veh == Entity.Null || !EntityManager.HasComponent<PublicTransport>(veh))
                            continue;
                        live.Add(veh);
                        liveCount++;
                        if (EntityManager.HasComponent<Target>(veh)
                            && EntityManager.GetComponentData<Target>(veh).m_Target != terminusWaypoint)
                            lapServed.Add(veh);
                    }
                    int surplus = desiredFleet > 0 ? liveCount - desiredFleet : 0;

                    if (diagLog)
                        Mod.log.Info($"[SelfTest] fleet line#{line.Index} now={nowMin}m live={liveCount} target={desiredFleet} surplus={surplus} slotCovered={slotCovered} pending={pending.Count}");

                    if (surplus <= 0)
                    {
                        pending.Clear(); // at/under target — nothing to retire; forget any latched buses
                    }
                    else
                    {
                        for (int v = 0; v < vehicles.Length; v++)
                        {
                            Entity veh = vehicles[v].m_Vehicle;
                            if (veh == Entity.Null || !EntityManager.HasComponent<PublicTransport>(veh))
                                continue;
                            PublicTransport pt = EntityManager.GetComponentData<PublicTransport>(veh);
                            bool flagged = (pt.m_State & PublicTransportFlags.AbandonRoute) != 0;
                            if (flagged && pending.Count < surplus)
                                pending.Add(veh); // the game wants this one gone — latch it (bounded by live surplus)
                            if (!pending.Contains(veh))
                                continue;
                            // Final approach: not boarding and its target is the terminus (last leg of the loop).
                            bool onFinalApproach = (pt.m_State & PublicTransportFlags.Boarding) == 0
                                && EntityManager.HasComponent<Target>(veh)
                                && EntityManager.GetComponentData<Target>(veh).m_Target == terminusWaypoint;
                            // Retire only once a front bus is covering this slot's departure (so we never retire the bus
                            // the terminus needs to send out next). Once a bus on final approach is flagged we KEEP it
                            // flagged (commit) even across the brief no-front gaps; a bus still out on the loop is
                            // deferred (flag cleared) and keeps serving.
                            if (onFinalApproach && lapServed.Contains(veh) && (flagged || slotCovered))
                            {
                                if (!flagged) // lap done, back at the terminus, slot covered — assert; vanilla retires it here
                                {
                                    pt.m_State |= PublicTransportFlags.AbandonRoute;
                                    EntityManager.SetComponentData(veh, pt);
                                    Mod.log.Info($"[SelfTest] retire line#{line.Index} veh#{veh.Index} now={nowMin}m live={liveCount} target={desiredFleet}");
                                }
                            }
                            else if (flagged) // mid-route, or slot not yet covered — defer: keep it serving
                            {
                                pt.m_State &= ~PublicTransportFlags.AbandonRoute;
                                EntityManager.SetComponentData(veh, pt);
                            }
                        }
                        pending.RemoveWhere(e => !live.Contains(e)); // drop buses that already retired / left the line
                    }
                    lapServed.RemoveWhere(e => !live.Contains(e)); // forget buses no longer on the line
                }

                if (sample == null)
                    sample = $"line#{line.Index} sched{sched} every {ScheduleMath.IntervalFor(s, sch, nowMin, sched)}m";
            }
            lines.Dispose();

            TimetableInUse = anyEnabled;

            if (anyEnabled && frame - m_LastLog >= 16384)
            {
                m_LastLog = frame;
                Mod.log.Info($"[SelfTest] timetableDispatch: timetabledLines={enabledCount} nowMin={nowMin} {sample}");
            }
        }

        // (3a helper) Hold every stop's boarding bus to that stop's scheduled departure. Per-stop offset = cumulative
        // route travel time from the terminus (Σ RouteSegment.PathInformation.m_Duration, 60-frame units) -> schedule
        // minutes. Segment i is the leg from waypoint i to waypoint i+1 (matches HourlyFleetSystem.ComputeStableDuration).
        private void HoldAllStops(Entity line, Setting s, TimetableSchedule sch, int sched, Entity terminusStop,
            Entity terminusWaypoint, uint frame, int nowMin, int interval, bool diagLog)
        {
            if (!EntityManager.HasBuffer<RouteWaypoint>(line) || !EntityManager.HasBuffer<RouteSegment>(line))
                return;
            DynamicBuffer<RouteWaypoint> wps = EntityManager.GetBuffer<RouteWaypoint>(line, isReadOnly: true);
            DynamicBuffer<RouteSegment> segs = EntityManager.GetBuffer<RouteSegment>(line, isReadOnly: true);
            int len = wps.Length;
            if (len == 0 || segs.Length < len)
                return;

            // Start accumulating at the terminus waypoint (the schedule's timing anchor); fall back to index 0.
            int start = 0;
            if (terminusWaypoint != Entity.Null)
                for (int i = 0; i < len; i++)
                    if (wps[i].m_Waypoint == terminusWaypoint) { start = i; break; }

            // [SelfTest] diagnostic — one line per route dump showing every stop's derived offset (min from terminus)
            // and, for any stop with a boarding bus right now, its until/HOLD-or-GO decision. Read once live to see why
            // intermediate stops aren't waiting (expected suspect: offset==travel -> until~0 -> nothing to hold).
            System.Text.StringBuilder diag = diagLog
                ? new System.Text.StringBuilder("[SelfTest] hold line#").Append(line.Index)
                    .Append(" now=").Append(nowMin).Append("m int=").Append(interval).Append("m stops:")
                : null;

            float offUnits = 0f;
            for (int j = 0; j < len; j++)
            {
                int wpIdx = start + j; if (wpIdx >= len) wpIdx -= len;
                Entity wp = wps[wpIdx].m_Waypoint;
                int offMin = (int)System.Math.Round(offUnits * ScheduleMath.UnitMinutes);
                bool boarding = false;
                if (EntityManager.HasComponent<Connected>(wp))
                {
                    Entity stop = EntityManager.GetComponentData<Connected>(wp).m_Connected;
                    if (stop != Entity.Null && EntityManager.HasComponent<BoardingVehicle>(stop))
                    {
                        boarding = true;
                        HoldStop(s, sch, sched, stop, frame, nowMin, offMin, stop == terminusStop, interval, diag);
                    }
                }
                if (diag != null && !boarding)
                    diag.Append(" [").Append(j).Append(":off").Append(offMin).Append(']');
                // Add the leg LEAVING this waypoint so the next waypoint's offset is correct.
                int segIdx = start + j; if (segIdx >= len) segIdx -= len;
                Entity seg = segs[segIdx].m_Segment;
                if (seg != Entity.Null && EntityManager.HasComponent<PathInformation>(seg))
                    offUnits += EntityManager.GetComponentData<PathInformation>(seg).m_Duration;
            }

            if (diag != null)
                Mod.log.Info(diag.ToString());
        }

        // Hold one stop's in-service boarding bus to its scheduled clock departure (the schedule shifted by offMin), or
        // force-depart it on/after time. EVERY stop (terminus and intermediate) holds to its next scheduled slot: a bus
        // that arrives early waits for its clock minute; one that missed its slot rides the next one — a bounded, ONE-TIME
        // wait (until is always < one headway), after which it's on time at every later stop, so it never cascades.
        // The m_DepartureFrame bump is honored at all stops per TransportCarAISystem.StopBoarding (line ~1265: while
        // frame < m_DepartureFrame the boarding vehicle stays), not just the terminus.
        // When diag != null, appends this stop's decision (or skip reason) to the route's [SelfTest] dump.
        private void HoldStop(Setting s, TimetableSchedule sch, int sched, Entity stop, uint frame, int nowMin,
            int offMin, bool isTerminus, int interval, System.Text.StringBuilder diag)
        {
            string tag = isTerminus ? "T" : "";
            Entity veh = EntityManager.GetComponentData<BoardingVehicle>(stop).m_Vehicle;
            if (veh == Entity.Null || !EntityManager.HasComponent<PublicTransport>(veh))
            { diag?.Append(" [off").Append(offMin).Append(tag).Append(":noveh]"); return; }
            PublicTransport pt = EntityManager.GetComponentData<PublicTransport>(veh);
            // Only hold an IN-SERVICE boarding bus; a retiring one has EnRoute cleared and must reach the depot.
            bool isBoarding = (pt.m_State & PublicTransportFlags.Boarding) != 0;
            bool isEnRoute = (pt.m_State & PublicTransportFlags.EnRoute) != 0;
            if (!isBoarding || !isEnRoute)
            { diag?.Append(" [off").Append(offMin).Append(tag).Append(":brd").Append(isBoarding ? 1 : 0).Append("/enr").Append(isEnRoute ? 1 : 0).Append(']'); return; }
            int nextDep = ScheduleMath.NextDeparture(s, sch, sched, nowMin - offMin) + offMin;
            int until = nextDep - nowMin;
            bool hold = until > 0; // hold to this stop's next scheduled slot (terminus + travel offset), like the terminus
            diag?.Append(" [off").Append(offMin).Append(tag).Append(":dep").Append(nextDep).Append(" until").Append(until).Append(hold ? " HOLD]" : " GO]");
            if (hold)
            {
                // EARLY: hold until this stop's scheduled clock minute.
                uint target = frame + (uint)(until * ScheduleMath.FramesPerMinute);
                if (target > pt.m_DepartureFrame) { pt.m_DepartureFrame = target; EntityManager.SetComponentData(veh, pt); }
            }
            else
            {
                // AT/PAST schedule (or a late bus at an intermediate stop): leave NOW with whoever boarded. Push
                // m_DepartureFrame >=1800 frames into the past so vanilla StopBoarding force-departs (departure = cutoff).
                uint force = frame > 1800u ? frame - 1800u : 1u;
                if (pt.m_DepartureFrame > force) { pt.m_DepartureFrame = force; EntityManager.SetComponentData(veh, pt); }
            }
        }

        // Resolve the line's terminus stop and its waypoint: the player-chosen stop if set and valid, otherwise the
        // first stop on the line that has a boarding slot.
        private void FindTerminus(Entity line, TimetableSchedule sch, out Entity stop, out Entity waypoint)
        {
            stop = Entity.Null;
            waypoint = Entity.Null;
            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, isReadOnly: true);

            if (sch.m_TerminusStop != Entity.Null && EntityManager.Exists(sch.m_TerminusStop)
                && EntityManager.HasComponent<BoardingVehicle>(sch.m_TerminusStop))
            {
                for (int j = 0; j < waypoints.Length; j++)
                {
                    Entity wp = waypoints[j].m_Waypoint;
                    if (EntityManager.HasComponent<Connected>(wp)
                        && EntityManager.GetComponentData<Connected>(wp).m_Connected == sch.m_TerminusStop)
                    {
                        stop = sch.m_TerminusStop;
                        waypoint = wp;
                        return;
                    }
                }
            }

            for (int j = 0; j < waypoints.Length; j++)
            {
                Entity wp = waypoints[j].m_Waypoint;
                if (!EntityManager.HasComponent<Connected>(wp))
                    continue;
                Entity s = EntityManager.GetComponentData<Connected>(wp).m_Connected;
                if (s != Entity.Null && EntityManager.HasComponent<BoardingVehicle>(s))
                {
                    stop = s;
                    waypoint = wp;
                    return;
                }
            }
        }

        // On disable, put the line's spacing behaviour back to the prefab default.
        private void RestoreUnbunching(Entity line, TransportLine tl)
        {
            if (!EntityManager.HasComponent<PrefabRef>(line))
                return;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (!EntityManager.HasComponent<TransportLineData>(prefab))
                return;
            float def = EntityManager.GetComponentData<TransportLineData>(prefab).m_DefaultUnbunchingFactor;
            if (tl.m_UnbunchingFactor != def)
            {
                tl.m_UnbunchingFactor = def;
                EntityManager.SetComponentData(line, tl);
            }
        }
    }
}
