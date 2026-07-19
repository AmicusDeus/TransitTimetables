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
    //  2. NO MID-ROUTE IDLE: the schedule itself supplies the spacing — the hold below OVERWRITES m_DepartureFrame
    //     every tick, so vanilla's unbunching delay (which only feeds RouteUtils.CalculateDepartureFrame) can never
    //     apply to a line we're holding. TransportLine.m_UnbunchingFactor is deliberately left at the prefab default
    //     and never written: it is serialized, nothing in vanilla restores it, and no UI exposes it, so writing it
    //     would outlive the mod. Earlier versions zeroed it; (2) in OnUpdate now heals that.
    //  3. STOP HOLD (the timing point): each stop's boarding vehicle is held to that stop's next scheduled clock
    //     departure if it is EARLY (writes PublicTransport.m_DepartureFrame); if on-time or late it departs
    //     immediately. Only this line's own vehicles are ever touched (a shared stop's single boarding slot may hold
    //     another line's bus), and a hold can never exceed one headway (see the clamp in HoldStop).
    //
    // Runs every 8 frames so it always re-asserts the hold before the vanilla 16-frame AI release.
    //
    // ============================ DESIGN DECISIONS — deliberate, NOT bugs ============================
    // Both of these look like defects to anyone (or any audit) reading them cold. One already WAS mistaken for a bug
    // and "fixed", which silently broke the timetable. Read this before changing either.
    //
    //  A. A VEHICLE DEPARTS ON ITS POSTED MINUTE AND NEVER WAITS FOR ANYONE. Once the scheduled minute arrives it
    //     leaves — over a cim walking up, over a passenger still boarding. Waiting would drag the line off schedule,
    //     and staying on schedule is the entire product. Stragglers take the next slot, exactly as with a real
    //     printed timetable. Implemented by the frame-1800 cutoff in HoldStop's GO branch (see the note there).
    //
    //  B. SURPLUS VEHICLES FINISH THEIR LOOP — THEY NEVER ABANDON MID-ROUTE. When the headway widens (peak ->
    //     off-peak) vanilla flags the highest-ODOMETER vehicles and would retire each one wherever it stands,
    //     dumping its passengers. Block (3b) strips that flag back off every tick for any vehicle not on its final
    //     approach, so it keeps serving; it may only go once it is back at the terminus, has completed a full
    //     serving lap (m_LapServed), and another vehicle is covering this slot's departure. That is what stops the
    //     deploy-then-instantly-recall yo-yo and the run of dead departure slots.
    // ===============================================================================================
    public partial class TimetableDispatchSystem : GameSystemBase
    {
        private SimulationSystem m_Sim;
        private TimeSystem m_Time;
        private HourlyFleetSystem m_Fleet;
        private TimebaseSystem m_Timebase;
        // Per-tick snapshot of the runtime frame<->minute scale (from TimebaseSystem): frames per in-game minute and
        // in-game minutes per route "duration unit". Snapshotted once at the top of OnUpdate so all math in a tick uses
        // one consistent value; the HoldAllStops/HoldStop helpers read these fields directly (no signature churn).
        private float m_Fpm;
        private float m_Um;
        // Last day-length "regime" we saw. When TimebaseSystem's generation changes (a real day-length change, e.g. a
        // slow-time mod toggled), the per-vehicle slots were scaled by the OLD frames/minute, so drop them and let each
        // bus re-derive its slot against the new scale on its next terminus visit.
        private uint m_TimebaseGen;
        private EntityQuery m_LineQuery;
        private readonly Dictionary<Entity, int> m_LastFleet = new Dictionary<Entity, int>();
        // Per line: vehicles the game flagged to retire that we're driving to the terminus before letting them go.
        private readonly Dictionary<Entity, HashSet<Entity>> m_PendingRetire = new Dictionary<Entity, HashSet<Entity>>();
        // Per line: buses seen AWAY from the terminus (serving the loop) since they appeared — i.e. that have earned
        // a full loop and may now retire on their next return. A freshly-deployed bus is absent from this set until
        // it leaves the terminus, so it always completes one serving lap before it can be recalled.
        private readonly Dictionary<Entity, HashSet<Entity>> m_LapServed = new Dictionary<Entity, HashSet<Entity>>();
        // Reused scratch for pruning the above dicts against the live query (a line bulldozed while enabled leaves the
        // query without hitting the disable branch, so its keys would otherwise leak). Members = no per-update alloc.
        private readonly HashSet<Entity> m_LiveScratch = new HashSet<Entity>();
        private readonly List<Entity> m_StaleScratch = new List<Entity>();
        private uint m_LastLog;
        // Throttle for the hold-clamp warning (see HoldStop): it fires per stop per 8-frame tick, so rate-limit it to
        // the [SelfTest] cadence — one WARN is a signal, one every 8 frames is noise.
        private uint m_LastClampWarn;

        // PER-VEHICLE SLOT (issue #4): the sim FRAME at which each vehicle is scheduled to depart the TERMINUS on its
        // current run. Holding a bus to ITS slot (shifted by each stop's offset) — rather than to "the next slot after
        // now" — means a bus that falls slightly behind rides its own slot LATE instead of being bumped to the next
        // cycle and stranded for a whole interval. A frame (not a minute) so comparisons are monotonic across midnight.
        // Keyed by vehicle Entity (globally unique); pruned each tick against m_LiveVehScratch so despawned buses drop.
        private readonly Dictionary<Entity, uint> m_RunSlotFrame = new Dictionary<Entity, uint>();
        private readonly HashSet<Entity> m_LiveVehScratch = new HashSet<Entity>();

        // Minimum stop dwell (minutes) for a bus that arrives ON its slot or LATE, so it still boards/offloads instead
        // of being force-departed the instant it pulls in. Early buses are unaffected (they board during their hold).
        private const int kMinDwellMinutes = 2;
        // The frame each vehicle started boarding its CURRENT stop. Presence == "boarding now"; stamped on the first
        // tick boarding and dropped when it leaves (so the same stop next loop re-stamps). Feeds HoldStop's min-dwell.
        private readonly Dictionary<Entity, uint> m_ArrivedFrame = new Dictionary<Entity, uint>();

        // ===== DIAGNOSTIC (read-only): the game's ESTIMATED line duration vs the MEASURED actual loop time =====
        // Purpose: gather hard per-line data on how well the pathfinder estimate (ComputeStableDuration) matches the
        // real time a bus takes, with and without a slow-time mod, so we can decide whether a travel-time correction is
        // warranted (issue: reporter measured ~150m estimated vs ~180m actual). Measures each vehicle's terminus
        // DEPARTURE -> next terminus ARRIVAL span (travel + intermediate dwells, EXCLUDING the terminus hold) so it is
        // directly comparable to the estimate. EMA'd per line. Writes NOTHING to the world — pure observation.
        private readonly Dictionary<Entity, Entity> m_LapFront = new Dictionary<Entity, Entity>();        // line -> vehicle at its terminus now
        private readonly Dictionary<Entity, uint>   m_VehTerminusDepart = new Dictionary<Entity, uint>(); // vehicle -> frame it last left the terminus
        private readonly Dictionary<Entity, float>  m_LineLoopEma = new Dictionary<Entity, float>();      // line -> EMA of measured loop frames
        private readonly Dictionary<Entity, int>    m_LineLoopSamples = new Dictionary<Entity, int>();    // line -> loop samples so far
        private readonly Dictionary<Entity, float>  m_LineLoopMin = new Dictionary<Entity, float>();      // line -> running MIN loop (the true single loop; doubles sit above it)
        private readonly Dictionary<Entity, int>    m_LineRejectStreak = new Dictionary<Entity, int>();   // line -> consecutive gate rejects (drives the stale-anchor reset)
        // ===== PER-STOP measured arrival offset (frames from the terminus) — the fix for "buses leave early" AND the
        // feedback loop. Learned ONLY from buses that ran the loop with NO early-arrival hold (m_VehHeld), so the value
        // is real travel + natural dwell, never the mod's own holds. Drives each stop's posted time directly (per-stop
        // accurate), replacing the uniform loop-factor that mis-distributed the correction across stops. =====
        private readonly Dictionary<Entity, float>  m_StopOffsetEma = new Dictionary<Entity, float>();     // waypoint -> EMA arrival offset (frames)
        private readonly Dictionary<Entity, int>    m_StopOffsetSamples = new Dictionary<Entity, int>();   // waypoint -> samples
        private readonly HashSet<Entity>            m_VehHeld = new HashSet<Entity>();                      // vehicles EARLY-HELD this loop (excluded from measurement)
        private readonly Dictionary<Entity, Entity> m_VehLastRecordedStop = new Dictionary<Entity, Entity>(); // veh -> last stop recorded (once per arrival)
        private const uint  kMinLoopFrames = 1000u;      // ignore absurdly short spans (jitter / same-tick slot churn)
        private const uint  kMaxLoopFrames = 4194304u;   // ...and absurdly long ones (a loop can't exceed a stretched day)
        private const float kLoopAlpha     = 0.30f;      // EMA smoothing for the measured loop
        private const int   kMinTrustSamples = 4;        // measured correction is trusted over the density prior at >= this
        private const int   kResetAfterRejects = 4;      // consecutive rejects => the min anchor is stale (route edit / glitch): re-anchor
        private const int   kFleetCap        = 100;      // absolute per-line vehicle sanity cap (a bad reading can't flood)

        // Read by VehicleLimitSystem to auto-uncap the vehicle ceiling while any line is timetabled.
        public static bool TimetableInUse;

        protected override void OnCreate()
        {
            base.OnCreate();
            // TimetableInUse is a static read by VehicleLimitSystem; reset it on every system-creation (i.e. per world /
            // save load) so a stale "true" left over from a previous session can't keep the global vehicle cap uncapped.
            TimetableInUse = false;
            m_Sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Time = World.GetOrCreateSystemManaged<TimeSystem>();
            m_Fleet = World.GetOrCreateSystemManaged<HourlyFleetSystem>();
            m_Timebase = World.GetOrCreateSystemManaged<TimebaseSystem>();
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
            // Intentionally NOT RequireForUpdate(m_LineQuery): OnUpdate must keep ticking when the query EMPTIES (the
            // last timetabled line was deleted) so it can set TimetableInUse=false and let VehicleLimitSystem restore
            // the global vehicle cap. With RequireForUpdate the system stops on an empty query, latching the 8x uncap
            // on forever (and bleeding it into the next save loaded this session). The empty-query loop is trivial.
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 8;

        protected override void OnUpdate()
        {
            Setting s = Mod.ActiveSetting;
            if (s == null)
                return;

            uint frame = m_Sim.frameIndex;
            int nowMin = (int)(m_Time.normalizedTime * 1440f) % 1440;

            // Runtime frame<->minute scale (vanilla 262144 frames/day unless a slow-time mod stretches the day). One
            // consistent snapshot per tick. On a real day-length change, drop the per-vehicle slots that were scaled by
            // the previous value so each bus re-derives against the new one at its next terminus visit.
            m_Fpm = m_Timebase.FramesPerMinute;
            m_Um = m_Timebase.UnitMinutes;
            uint tbGen = m_Timebase.RegimeGeneration;
            if (tbGen != m_TimebaseGen) { m_TimebaseGen = tbGen; m_RunSlotFrame.Clear(); }

            NativeArray<Entity> lines = m_LineQuery.ToEntityArray(Allocator.Temp);
            bool anyEnabled = false;
            int enabledCount = 0;
            string sample = null;
            m_LiveVehScratch.Clear(); // repopulated in the drain below, then used to prune m_RunSlotFrame (issue #4)
            for (int i = 0; i < lines.Length; i++)
            {
                Entity line = lines[i];
                TimetableSchedule sch = EntityManager.GetComponentData<TimetableSchedule>(line);
                CustomPeakSchedule customSch = EntityManager.HasComponent<CustomPeakSchedule>(line)
                    ? EntityManager.GetComponentData<CustomPeakSchedule>(line) : CustomPeakSchedule.Default(); // PR #5 per-line peak
                TransportLine tl = EntityManager.GetComponentData<TransportLine>(line);

                if (!sch.m_Enabled)
                {
                    RestoreUnbunching(line, tl);
                    if (m_LastFleet.ContainsKey(line))
                    {
                        // We were managing this line — hand it back to vanilla EXACTLY ONCE (m_LastFleet is cleared
                        // just below, so later disabled frames skip this): release any bus we were holding so it
                        // departs immediately instead of idling to a stale scheduled frame (#8), and deactivate the
                        // mod-applied vehicle-count policy so the fleet reverts to vanilla's automatic count rather
                        // than staying frozen at the last derived number — which otherwise persists into the save (#4).
                        ReleaseHeldVehicles(line, frame);
                        m_Fleet.TryClearLineFleet(line);
                    }
                    m_LastFleet.Remove(line);
                    m_PendingRetire.Remove(line);
                    m_LapServed.Remove(line);
                    // Reset the loop-time measurement so a re-enabled line measures fresh (its route may have changed).
                    m_LapFront.Remove(line);
                    m_LineLoopEma.Remove(line);
                    m_LineLoopSamples.Remove(line);
                    m_LineLoopMin.Remove(line);
                    m_LineRejectStreak.Remove(line);
                    continue;
                }
                anyEnabled = true;
                enabledCount++;

                // (2) Leave TransportLine.m_UnbunchingFactor at the PREFAB DEFAULT — and heal it if an older version
                // of this mod zeroed it.
                //
                // Until v0.2.2 this zeroed the factor so a vehicle wouldn't idle mid-route to self-space. That was a
                // serious mistake: m_UnbunchingFactor is SERIALIZED into the save (Game.Routes/TransportLine), NOTHING
                // in vanilla ever restores it (the only assignment is the component's ctor from the prefab default,
                // which runs once at creation), and NO UI anywhere exposes it. So uninstalling the mod — or the mod
                // failing to load after a game patch — left those lines permanently unable to unbunch, invisibly and
                // unrecoverably, looking exactly like a base-game bug.
                //
                // It is also unnecessary. Unbunching only ever feeds RouteUtils.CalculateDepartureFrame, i.e. it just
                // inflates m_DepartureFrame at StartBoarding — and HoldStop now writes m_DepartureFrame
                // authoritatively every 8 frames, so the factor cannot affect a line we are actively holding.
                // Leaving it alone is strictly better: an out-of-service window (day-only line at night) now unbunches
                // normally instead of staying silently crippled.
                //
                // RestoreUnbunching only writes when the value differs from the prefab default, so this is a no-op on
                // a healthy line and a ONE-TIME repair on a save damaged by an earlier version.
                RestoreUnbunching(line, tl);

                // The line's day/night operating schedule — which intervals apply and when it runs.
                int sched = LineSchedule.Of(EntityManager, line);

                // (2b) Keep the STORED first departure equal to the EFFECTIVE one.
                //
                // ScheduleMath.FirstDeparture already clamps a first departure that falls outside the line's operating
                // window (night-only -> NightStart, day-only -> NightEnd) — but only as a RETURN VALUE. The stored
                // field kept whatever the player set, and the panel displays the stored field, so the UI LIED: a
                // night-only line reading "First departure 05:00" was actually running its first bus at 22:00.
                // Writing the clamp back makes stored == effective == displayed, and gives the behaviour you'd expect:
                // switch a line to night-only and its first departure jumps to the start of the night; switch it to
                // day-only and it jumps to the morning. DayAndNight lines are never clamped, so they are untouched.
                //
                // It also gives the steppers honest bounds for free: on a day-only line "-" stops at 06:00 because
                // 05:59 is a minute that line genuinely cannot run, and on a night-only line "+" past 05:59 comes back
                // round to 22:00 instead of escaping into daytime.
                //
                // Cost, accepted deliberately: a value set while the line was DayAndNight is overwritten (not
                // remembered) if the line is later switched to day- or night-only. It was inoperative for that line
                // anyway. Only writes when it actually differs, so this is a one-time correction, not a per-tick write.
                int effFirst = ScheduleMath.FirstDeparture(s, sch, sched);
                if (effFirst != sch.m_FirstDeparture)
                {
                    sch.m_FirstDeparture = (ushort)effFirst; // FirstDeparture returns a minute-of-day, always 0..1439
                    EntityManager.SetComponentData(line, sch);
                }

                // (1) derive + apply fleet for the current headway. Re-assert EVERY tick (not just on change): the
                // fleet is now applied by writing the line's own VehicleInterval modifier directly (see
                // HourlyFleetSystem.TrySetLineFleet), and that buffer is rebuilt from the line's policies whenever the
                // line is edited or its route recreated — so a periodic re-write keeps our derived count in place.
                // TrySetLineFleet only touches the buffer when the value actually differs, so this is cheap.
                int desiredFleet = 0;
                float durUnits = m_Fleet.LineStableDurationUnits(line);
                if (durUnits > 1f)
                {
                    int interval = ScheduleMath.IntervalFor(s, sch, customSch, nowMin, sched);
                    // Phase 2: size the fleet to the REAL loop when the player opts in (costs money); otherwise the
                    // estimate, exactly as before. LineCorrection is grow-only for fleet; kFleetCap is the hard backstop.
                    float fleetUnits = s.ProvisionRealFleet ? durUnits * LineCorrection(line, durUnits, forFleet: true) : durUnits;
                    desiredFleet = ScheduleMath.DerivedFleet(fleetUnits, interval, m_Um);
                    // Cap only guards a bad CORRECTION reading, which can only occur under ProvisionRealFleet; gating it
                    // there keeps the both-settings-OFF path bit-identical to before (an uncorrected line stays uncapped).
                    if (s.ProvisionRealFleet && desiredFleet > kFleetCap) desiredFleet = kFleetCap;
                    if (m_Fleet.TrySetLineFleet(line, desiredFleet))
                        m_LastFleet[line] = desiredFleet;
                }

                // (3) terminus = timing point + retirement anchor (player-chosen stop, or the first stop)
                FindTerminus(line, sch, out Entity terminusStop, out Entity terminusWaypoint);

                // Accumulate this line's measured loop time from terminus front-vehicle changes (feeds LineCorrection).
                MeasureLap(line, terminusStop, frame, durUnits);

                // (3-pre) FORCE STOPS: make our buses actually pull in and STOP rather than let vanilla skip a stop
                // where nobody boards or alights — ALWAYS at the terminus (skipping it strands the whole schedule), and
                // at every stop when the player opts in. A skipped stop never enters Boarding, so the hold below can't
                // touch it and the bus rolls on early. See ForceStops.
                int forcedStops = ForceStops(line, terminusWaypoint, s.StopAtEveryStop);

                // (3a) FULL TIMETABLE: hold EACH stop's boarding bus to that stop's scheduled departure — the terminus
                // schedule shifted by the stop's cumulative offset from the terminus (offset 0 at the terminus).
                // Offsets come from the route itself: each RouteSegment's PathInformation.m_Duration PLUS the dwell at
                // each intermediate timed stop (60-frame route units), summed from the terminus and converted to
                // schedule minutes via the runtime unit scale (m_Um) — matching the UI board's TravelUnitsBetween.
                int curInterval = ScheduleMath.IntervalFor(s, sch, customSch, nowMin, sched);
                bool diagLog = frame - m_LastLog >= 16384; // [SelfTest] cadence — dump the hold's numbers periodically

                // Estimated vs measured loop time for this line (the data that tells us whether the pathfinder estimate
                // undershoots the real drive time, and by how much — RT on or off). estDur uses the same durUnits that
                // sizes the fleet; measLoop is the observed terminus-to-terminus travel (see MeasureLap).
                if (diagLog && m_LineLoopSamples.TryGetValue(line, out int loopN) && loopN > 0)
                {
                    float measMin = m_LineLoopEma[line] / m_Fpm;
                    float estMin  = durUnits * m_Um;
                    float ratio   = estMin > 0.01f ? measMin / estMin : 0f;
                    Mod.log.Info($"[SelfTest] laptime line#{line.Index} estDur={estMin:F1}m measLoop={measMin:F1}m " +
                                 $"ratio={ratio:F2} n={loopN} compat={(s.RealisticTripsCompat ? 1 : 0)}");
                }

                HoldAllStops(line, s, sch, customSch, sched, terminusStop, terminusWaypoint, frame, nowMin, curInterval, diagLog);

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
                        m_LiveVehScratch.Add(veh); // union of all live vehicles -> prunes m_RunSlotFrame after the loop
                        liveCount++;
                        // Arrival stamp for HoldStop's min-dwell: presence in m_ArrivedFrame == "currently boarding,
                        // arrived at this frame". Stamp on the first boarding tick; drop when it leaves the stop so the
                        // next stop (or the same stop next loop) re-stamps a fresh arrival.
                        if ((EntityManager.GetComponentData<PublicTransport>(veh).m_State & PublicTransportFlags.Boarding) != 0)
                        {
                            if (!m_ArrivedFrame.ContainsKey(veh)) m_ArrivedFrame[veh] = frame;
                        }
                        else m_ArrivedFrame.Remove(veh);
                        if (EntityManager.HasComponent<Target>(veh)
                            && EntityManager.GetComponentData<Target>(veh).m_Target != terminusWaypoint)
                            lapServed.Add(veh);
                    }
                    int surplus = desiredFleet > 0 ? liveCount - desiredFleet : 0;

                    if (diagLog)
                        Mod.log.Info($"[SelfTest] fleet line#{line.Index} now={nowMin}m live={liveCount} target={desiredFleet} surplus={surplus} slotCovered={slotCovered} pending={pending.Count} forced={forcedStops}");

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
                            //
                            // *** DESIGN DECISION B (see the header) — deliberate. Clearing vanilla's AbandonRoute on a
                            // mid-route bus is the POINT, not an oversight: it makes the surplus finish its loop and
                            // drop its passengers at the terminus instead of vanishing wherever it happened to be.
                            // The deferral reliably wins the race — this system runs every 8 frames, the AI that
                            // CONSUMES the flag (StartBoarding) every 16, and vanilla re-flags surplus only every 256,
                            // so there are always two of our ticks between AI ticks. ***
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
                    sample = $"line#{line.Index} sched{sched} every {ScheduleMath.IntervalFor(s, sch, customSch, nowMin, sched)}m";
            }

            // Prune tracking entries for lines that left the query (e.g. bulldozed while enabled) so they don't leak.
            m_LiveScratch.Clear();
            for (int i = 0; i < lines.Length; i++) m_LiveScratch.Add(lines[i]);
            PruneToLive(m_LastFleet, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_PendingRetire, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LapServed, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LapFront, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LineLoopEma, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LineLoopSamples, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LineLoopMin, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LineRejectStreak, m_LiveScratch, m_StaleScratch);
            // Drop per-vehicle slots for buses that despawned/retired (m_LiveVehScratch = every live vehicle this tick).
            PruneToLive(m_RunSlotFrame, m_LiveVehScratch, m_StaleScratch);
            PruneToLive(m_ArrivedFrame, m_LiveVehScratch, m_StaleScratch);
            PruneToLive(m_VehTerminusDepart, m_LiveVehScratch, m_StaleScratch);
            PruneToLive(m_VehLastRecordedStop, m_LiveVehScratch, m_StaleScratch);
            m_VehHeld.RemoveWhere(v => !m_LiveVehScratch.Contains(v));
            // Per-stop offsets are keyed by waypoint (not line); drop entries whose waypoint no longer exists (route
            // edited / line deleted). Periodic (aligned with the [SelfTest] cadence) so it's a cheap occasional scan.
            if (frame - m_LastLog >= 16384 && m_StopOffsetSamples.Count > 0)
            {
                m_StaleScratch.Clear();
                foreach (Entity wpKey in m_StopOffsetSamples.Keys)
                    if (!EntityManager.Exists(wpKey)) m_StaleScratch.Add(wpKey);
                for (int i = 0; i < m_StaleScratch.Count; i++)
                { m_StopOffsetSamples.Remove(m_StaleScratch[i]); m_StopOffsetEma.Remove(m_StaleScratch[i]); }
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
        // route time from the terminus (Σ RouteSegment.PathInformation.m_Duration + dwell at each intermediate timed
        // stop, 60-frame units) -> schedule minutes. Segment i is the leg from waypoint i to waypoint i+1, and the
        // dwell term mirrors HourlyFleetSystem.ComputeStableDuration / the UI board so posted and held times agree.
        private void HoldAllStops(Entity line, Setting s, TimetableSchedule sch, CustomPeakSchedule customSch, int sched, Entity terminusStop,
            Entity terminusWaypoint, uint frame, int nowMin, int interval, bool diagLog)
        {
            // Outside the line's operating window (day-only at night, night-only by day, or a degenerate EMPTY window
            // like NightStart==NightEnd) -> don't hold or force-depart anything; let it run vanilla headway instead of
            // silently churning every bus through the force-depart path (which is what an empty window used to do).
            if (!ScheduleMath.InService(s, sched, nowMin))
                return;
            if (!EntityManager.HasBuffer<RouteWaypoint>(line) || !EntityManager.HasBuffer<RouteSegment>(line))
                return;
            DynamicBuffer<RouteWaypoint> wps = EntityManager.GetBuffer<RouteWaypoint>(line, isReadOnly: true);
            DynamicBuffer<RouteSegment> segs = EntityManager.GetBuffer<RouteSegment>(line, isReadOnly: true);
            int len = wps.Length;
            if (len == 0 || segs.Length < len)
                return;

            // Per-stop dwell (route units), added to each downstream stop's offset so the hold matches the departure
            // board (TravelUnitsBetween / ComputeStableDuration both count intermediate dwell). Without it the offset
            // was travel-only, so downstream stops departed ~1 min (one cumulative dwell) BEFORE their posted time.
            float stopDur = 1f;
            if (EntityManager.HasComponent<PrefabRef>(line))
            {
                Entity pf = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
                if (EntityManager.HasComponent<TransportLineData>(pf))
                    stopDur = EntityManager.GetComponentData<TransportLineData>(pf).m_StopDuration;
            }

            // A shared physical stop exposes ONE BoardingVehicle slot regardless of line, so its boarding bus may
            // belong to a DIFFERENT line. Build this line's own roster so HoldStop only ever holds our own buses.
            HashSet<Entity> lineVehicles = null;
            if (EntityManager.HasBuffer<RouteVehicle>(line))
            {
                DynamicBuffer<RouteVehicle> rv = EntityManager.GetBuffer<RouteVehicle>(line, isReadOnly: true);
                lineVehicles = new HashSet<Entity>();
                for (int i = 0; i < rv.Length; i++)
                    if (rv[i].m_Vehicle != Entity.Null) lineVehicles.Add(rv[i].m_Vehicle);
            }

            // Last tick's lap-served set (buses seen AWAY from the terminus). At the terminus HoldStop uses it to tell a
            // bus that has COMPLETED a lap (reassign it the next slot) from one that merely hasn't left yet (keep its
            // slot, depart late). May be null on a line's first managed tick — treated as "nobody has lapped".
            m_LapServed.TryGetValue(line, out HashSet<Entity> lapServed);

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

            bool useMeasured = s.RealisticTravelTime;
            float offUnits = 0f;
            for (int j = 0; j < len; j++)
            {
                int wpIdx = start + j; if (wpIdx >= len) wpIdx -= len;
                Entity wp = wps[wpIdx].m_Waypoint;
                // Posted offset for this stop (minutes from the terminus departure): the MEASURED per-stop arrival offset
                // when the feature is on and we have enough clean samples; otherwise the game's estimate. No uniform
                // factor — the per-stop measurement is per-stop accurate, so posted times match what the buses do.
                int offMin = (int)System.Math.Round(offUnits * m_Um);       // estimate fallback (terminus is j==0 -> 0)
                if (useMeasured && j >= 1 && m_StopOffsetSamples.TryGetValue(wp, out int sn) && sn >= kMinTrustSamples
                    && m_StopOffsetEma.TryGetValue(wp, out float emaF) && m_Fpm > 0.01f)
                    offMin = (int)System.Math.Round(emaF / m_Fpm);
                bool boarding = false;
                if (EntityManager.HasComponent<Connected>(wp))
                {
                    Entity stop = EntityManager.GetComponentData<Connected>(wp).m_Connected;
                    if (stop != Entity.Null && EntityManager.HasComponent<BoardingVehicle>(stop))
                    {
                        boarding = true;
                        Entity bveh = EntityManager.GetComponentData<BoardingVehicle>(stop).m_Vehicle;
                        // Stamp the arrival frame HERE (before RecordStopOffset/HoldStop) for our own boarding bus. The
                        // drain stamps it too, but one tick LATER — too late for RecordStopOffset to see it before
                        // HoldStop flags a bus that arrives EARLY at THIS stop. Stamping first lets us record this stop's
                        // CLEAN arrival: a hold at THIS stop happens AFTER arrival, so it does not inflate THIS stop's
                        // offset — only an UPSTREAM hold does, and that is already excluded via m_VehHeld. Without this,
                        // only late/dwelling buses ever recorded, biasing each stop's EMA upward toward the slow tail.
                        if (bveh != Entity.Null && (lineVehicles == null || lineVehicles.Contains(bveh))
                            && !m_ArrivedFrame.ContainsKey(bveh))
                            m_ArrivedFrame[bveh] = frame;
                        // Learn this stop's real arrival offset from the (our-line, upstream-unheld) boarding bus.
                        if (j >= 1)
                            RecordStopOffset(line, wp, bveh, lineVehicles, frame);
                        HoldStop(s, sch, customSch, sched, stop, frame, nowMin, offMin, stop == terminusStop, lineVehicles, lapServed, diag);
                    }
                }
                if (diag != null && !boarding)
                    diag.Append(" [").Append(j).Append(":off").Append(offMin).Append(']');
                // Add the leg LEAVING this waypoint so the next waypoint's offset is correct.
                int segIdx = start + j; if (segIdx >= len) segIdx -= len;
                Entity seg = segs[segIdx].m_Segment;
                if (seg != Entity.Null && EntityManager.HasComponent<PathInformation>(seg))
                    offUnits += EntityManager.GetComponentData<PathInformation>(seg).m_Duration;
                // ...plus this stop's own dwell so DOWNSTREAM offsets match the board (which counts intermediate
                // dwell). Intermediate timed stops only: j==0 is the terminus, and a stop's own dwell never enters
                // its own offset. Omitting this is what made downstream stops depart a dwell-time early.
                if (j >= 1 && EntityManager.HasComponent<VehicleTiming>(wp))
                    offUnits += stopDur;
            }

            if (diag != null)
                Mod.log.Info(diag.ToString());
        }

        // Hold one stop's in-service boarding bus to its scheduled clock departure (the schedule shifted by offMin), or
        // release it on/after time. EVERY stop (terminus and intermediate) holds to its next scheduled slot: a bus that
        // arrives early waits for its clock minute; one that missed its slot rides the next one — a bounded, ONE-TIME
        // wait, after which it's on time at every later stop, so it never cascades.
        // The bound is now ENFORCED rather than assumed (see the slotInterval clamp below) — the old code trusted it.
        // The m_DepartureFrame bump is honored at all stops per TransportCarAISystem.StopBoarding (line ~1265: while
        // frame < m_DepartureFrame the boarding vehicle stays), not just the terminus.
        // When diag != null, appends this stop's decision (or skip reason) to the route's [SelfTest] dump.
        private void HoldStop(Setting s, TimetableSchedule sch, CustomPeakSchedule customSch, int sched, Entity stop, uint frame, int nowMin,
            int offMin, bool isTerminus, HashSet<Entity> lineVehicles, HashSet<Entity> lapServed, System.Text.StringBuilder diag)
        {
            string tag = isTerminus ? "T" : "";
            Entity veh = EntityManager.GetComponentData<BoardingVehicle>(stop).m_Vehicle;
            if (veh == Entity.Null || !EntityManager.HasComponent<PublicTransport>(veh))
            { diag?.Append(" [off").Append(offMin).Append(tag).Append(":noveh]"); return; }
            // The boarding slot at a shared stop can hold ANOTHER line's bus — never write its departure frame.
            if (lineVehicles != null && !lineVehicles.Contains(veh))
            { diag?.Append(" [off").Append(offMin).Append(tag).Append(":foreign]"); return; }
            PublicTransport pt = EntityManager.GetComponentData<PublicTransport>(veh);
            // Only hold an IN-SERVICE boarding bus; a retiring one has EnRoute cleared and must reach the depot.
            bool isBoarding = (pt.m_State & PublicTransportFlags.Boarding) != 0;
            bool isEnRoute = (pt.m_State & PublicTransportFlags.EnRoute) != 0;
            if (!isBoarding || !isEnRoute)
            { diag?.Append(" [off").Append(offMin).Append(tag).Append(":brd").Append(isBoarding ? 1 : 0).Append("/enr").Append(isEnRoute ? 1 : 0).Append(']'); return; }
            // === PER-VEHICLE SLOT (issue #4) ===
            // Hold the bus to ITS OWN departure — the terminus slot it is running, shifted by this stop's cumulative
            // travel+dwell offset — NOT to "the next slot after now". A bus a few minutes behind therefore rides its
            // own slot LATE (departs immediately) instead of being bumped to the next cycle and stranded a whole
            // interval. The slot is a monotonic sim FRAME (no midnight-wrap ambiguity), recorded when the bus boards
            // the terminus and read as-is at every downstream stop of the same run.
            int maxInterval = ScheduleMath.MaxInterval(sch, customSch, sched);
            bool haveSlot = m_RunSlotFrame.TryGetValue(veh, out uint slotFrame);
            string slotSrc;
            if (isTerminus)
            {
                // The terminus is the anchor. (Re)assign the next scheduled departure when the bus has no slot yet, or
                // has COMPLETED a lap (it is in lapServed) AND its old slot is already past — i.e. it has come round
                // for its next run. A bus still on its FIRST slot but merely late to LEAVE (past its slot, never
                // lapped) KEEPS that slot and departs late; it must not grab a fresh one — that would be the very
                // "wait a whole cycle" bug we are fixing.
                bool lapped = lapServed != null && lapServed.Contains(veh);
                if (!haveSlot || (lapped && frame >= slotFrame))
                {
                    int untilNext = ScheduleMath.NextDeparture(s, sch, customSch, sched, nowMin) - nowMin; // minutes to next slot
                    if (untilNext >= 0 && untilNext <= maxInterval)
                    {
                        slotFrame = frame + (uint)(untilNext * m_Fpm);
                        m_RunSlotFrame[veh] = slotFrame;
                        slotSrc = "asg";
                    }
                    else
                    {
                        // No usable slot soon (operating-window edge): don't latch a far/garbage slot — release now.
                        m_RunSlotFrame.Remove(veh);
                        slotFrame = frame;
                        slotSrc = "edge";
                    }
                }
                else slotSrc = "keep";
                haveSlot = true;
            }
            else if (haveSlot)
            {
                // Same run: this stop's scheduled departure is the terminus slot pushed forward by the stop's offset.
                slotFrame += (uint)(offMin * m_Fpm);
                slotSrc = "run";
            }
            else
            {
                // Downstream bus with no slot yet (spawned mid-line, or the first tick after enabling): fall back to
                // the old next-slot-after-now guess. Self-corrects the next time this bus boards the terminus.
                int g = ScheduleMath.NextDeparture(s, sch, customSch, sched, nowMin - offMin) + offMin - nowMin;
                slotFrame = frame + (uint)((g > 0 ? g : 0) * m_Fpm);
                slotSrc = "guess";
            }

            // DEPARTURE TARGET. A bus departs at:
            //   - its SLOT, if it arrived EARLY (arrival < slot): it boarded during the hold, so leave ON TIME and
            //     don't wait for a straggler (design A); OR
            //   - ARRIVAL + a minimum dwell, if it arrived ON its slot or LATE (arrival >= slot): it has had no
            //     boarding time, so give it a minimum stop to board/offload, then leave (user request).
            // i.e. depart = max(slot, arrival + minDwell), branched so "early" is strictly arrival < slot. `arrived`
            // is the frame the bus started boarding this stop (m_ArrivedFrame, stamped in the drain); fall back to now
            // for the first tick before the stamp lands. Dwell is capped at one headway so a sub-2-min line can't jam.
            uint arrived = m_ArrivedFrame.TryGetValue(veh, out uint af) ? af : frame;
            int dwellMin = System.Math.Min(kMinDwellMinutes, maxInterval);
            uint target = arrived < slotFrame ? slotFrame : arrived + (uint)(dwellMin * m_Fpm);
            bool dwelling = arrived >= slotFrame; // holding for the min-dwell, not for an early slot (diagnostics only)

            long dframes = (long)target - frame;                                    // >0 -> hold/dwell; <=0 -> depart
            int until = (int)System.Math.Round((double)dframes / m_Fpm);

            // Safety net: a hold should never exceed one headway. With per-vehicle slots this rarely fires (a bus is
            // measured against its OWN near departure, not a distant clock slot), but it still catches a window-edge
            // terminus assignment or a schedule-math regression — release rather than freeze (the 6-16h kerb-freeze,
            // v0.2.1). The dwell branch is capped at one headway above, so only the slot branch can overrun.
            bool overrun = until > maxInterval;
            bool hold = dframes > 0 && !overrun;
            if (overrun && frame - m_LastClampWarn >= 16384u)
            {
                m_LastClampWarn = frame;
                Mod.log.Warn($"[SelfTest] hold clamped: until={until}m exceeds max headway={maxInterval}m at off={offMin} " +
                             $"(src={slotSrc}; window edge or a schedule-math regression) — releasing instead of freezing");
            }
            diag?.Append(" [off").Append(offMin).Append(tag).Append(':').Append(slotSrc).Append(" until").Append(until)
                .Append(hold ? (dwelling ? " DWELL]" : " HOLD]") : (overrun ? " GO-clamped]" : " GO]"));
            if (hold)
            {
                // Mark an EARLY-arrival hold at an INTERMEDIATE stop: this bus's arrival times downstream are pushed
                // later by the wait, so exclude its whole loop from the per-stop / loop measurement (breaks feedback).
                // The min-dwell case (dwelling) and the terminus timing-point hold are natural and stay measurable.
                if (!isTerminus && !dwelling) m_VehHeld.Add(veh);
                // EARLY -> hold to slot; ON-SLOT/LATE -> hold through the min-dwell. Either way write the target frame
                // AUTHORITATIVELY (overrides vanilla's unbunching-inflated value); this cannot cut a boarding short —
                // while held, StopBoarding keeps the bus for a cim walking up (m_MaxBoardingDistance != MaxValue,
                // TransportCarAISystem:1263-1265) and for a not-yet-Ready passenger (:1269-1278). Those guards are
                // bypassed only by the frame-1800 cutoff, which the GO branch below uses on purpose once time is up.
                if (pt.m_DepartureFrame != target) { pt.m_DepartureFrame = target; EntityManager.SetComponentData(veh, pt); }
            }
            else
            {
                // AT/PAST SCHEDULE: DEPART NOW. Do not wait for anyone.
                //
                // *** DESIGN DECISION — deliberate. This is NOT a bug; do not "fix" it. ***
                // A timetabled vehicle leaves on its posted minute and never holds for a straggler. Waiting would push
                // the whole line off its schedule, which is the single thing this mod exists to prevent — a real
                // timetable does not hold the 08:15 for someone jogging up the platform. Whoever misses it takes the
                // next slot. That is the point: the buses stay ON the timetable.
                // History: v0.2 changed this to frame-1 (a "graceful" release) because an audit read the dropped cim
                // as a defect. It isn't — the audit could not know the intent. That change silently reintroduced
                // schedule slip on every departure and was reverted in v0.2.3.
                //
                // Mechanism: writing m_DepartureFrame >= 1800 frames into the PAST trips StopBoarding's cutoff
                // (flag2, TransportCarAISystem:1262). That is the ONLY lever that clears BOTH guards which would
                // otherwise delay us: it forces m_MaxBoardingDistance = float.MaxValue (:1263, so an approaching cim
                // no longer holds the bus) AND skips the passenger-Ready wait (:1269-1278). frame-1 opens only the
                // m_DepartureFrame gate and leaves both guards armed — hence the slip. 1800 is vanilla's own
                // ~10-minute anti-softlock threshold; we borrow it because it is the only way to reach that branch.
                uint force = frame > 1800u ? frame - 1800u : 1u;
                if (pt.m_DepartureFrame > force) { pt.m_DepartureFrame = force; EntityManager.SetComponentData(veh, pt); }
            }
        }

        // Release any bus this line was holding (future m_DepartureFrame) so it departs once the timetable is switched
        // off, instead of idling at the platform until the stale scheduled frame arrives (#8).
        //
        // Deliberately frame-1 (GRACEFUL), NOT the frame-1800 cutoff the GO branch uses — do not "harmonize" them.
        // They serve opposite purposes: the GO branch is ENFORCING a timetable, so it must depart over stragglers
        // (design decision A). This path is HANDING THE LINE BACK to vanilla, so it must only undo our own hold and
        // then let normal boarding behave exactly as vanilla would. Forcing a departure here would be us overriding
        // the game on a line we no longer manage.
        private void ReleaseHeldVehicles(Entity line, uint frame)
        {
            if (!EntityManager.HasBuffer<RouteVehicle>(line))
                return;
            DynamicBuffer<RouteVehicle> vehicles = EntityManager.GetBuffer<RouteVehicle>(line, isReadOnly: true);
            uint past = frame > 1u ? frame - 1u : 1u;
            for (int v = 0; v < vehicles.Length; v++)
            {
                Entity veh = vehicles[v].m_Vehicle;
                if (veh == Entity.Null || !EntityManager.HasComponent<PublicTransport>(veh))
                    continue;
                PublicTransport pt = EntityManager.GetComponentData<PublicTransport>(veh);
                bool boarding = (pt.m_State & PublicTransportFlags.Boarding) != 0;
                bool enroute = (pt.m_State & PublicTransportFlags.EnRoute) != 0;
                if (boarding && enroute && pt.m_DepartureFrame > past) // only lower an active future hold
                {
                    pt.m_DepartureFrame = past;
                    EntityManager.SetComponentData(veh, pt);
                }
            }
        }

        // Force our line's buses to actually STOP at a stop instead of letting vanilla skip it when nobody boards or
        // alights. Vanilla only pulls a bus into a stop when PublicTransportFlags.RequireStop is set — raised by
        // ResidentAISystem when a passenger wants on/off, and read in TransportCarAISystem.CheckNavigationLanes to decide
        // skip-vs-stop. With no demand the flag stays clear and the bus rolls through, so it never enters Boarding and
        // the timetable hold can't act on it (HoldStop early-returns unless Boarding|EnRoute) — the bus then leaves
        // early. We simply OR the flag in ourselves:
        //   - the TERMINUS is forced UNCONDITIONALLY (a skipped terminus strands the schedule — it is the timing anchor);
        //   - every other stop only when `everyStop` (the player's opt-in), which trades a short dwell at empty stops for
        //     an honoured posted time at each one.
        // RequireStop is a TRANSIENT runtime flag: BeginTesting clears it at the start of each boarding test, then the
        // resident AI re-sets it if there is demand (TransportBoardingHelpers). PublicTransport.m_State IS serialized, but
        // a saved RequireStop bit self-clears at the very next BeginTesting — so forcing it is save/uninstall-safe (at
        // worst one extra stop right after an uninstall), unlike m_UnbunchingFactor which nothing ever restores. We
        // re-assert it every tick (this system runs every 8 frames vs the car AI's 16, so the set reliably lands between
        // the BeginTesting clear and the skip read), and we ONLY OR it in — never clear it — so we can never suppress a
        // stop the game genuinely wants. Scoped to THIS line's own RouteVehicles, so buses of other lines sharing a stop
        // are untouched. (The write also lands on any non-road vehicle on the line, but it is inert there — only
        // TransportCarAISystem reads RequireStop for the skip; trains/ships/planes never skip.) Returns count forced (diag).
        private int ForceStops(Entity line, Entity terminusWaypoint, bool everyStop)
        {
            if (!EntityManager.HasBuffer<RouteVehicle>(line))
                return 0;
            DynamicBuffer<RouteVehicle> vehicles = EntityManager.GetBuffer<RouteVehicle>(line, isReadOnly: true);
            int forced = 0;
            for (int v = 0; v < vehicles.Length; v++)
            {
                Entity veh = vehicles[v].m_Vehicle;
                if (veh == Entity.Null || !EntityManager.HasComponent<PublicTransport>(veh))
                    continue;
                PublicTransport pt = EntityManager.GetComponentData<PublicTransport>(veh);
                // Only a bus that is IN SERVICE (EnRoute) and currently DRIVING (not already boarding) can skip an
                // upcoming stop; leave depot-bound / boarding buses alone.
                if ((pt.m_State & PublicTransportFlags.EnRoute) == 0 || (pt.m_State & PublicTransportFlags.Boarding) != 0)
                    continue;
                // Terminus: forced whenever this bus is heading to it (its next waypoint IS the terminus). Same
                // Target==terminusWaypoint test the drain uses for lap-eligibility, so "waypoint" comparison is correct.
                bool approachingTerminus = terminusWaypoint != Entity.Null
                    && EntityManager.HasComponent<Target>(veh)
                    && EntityManager.GetComponentData<Target>(veh).m_Target == terminusWaypoint;
                if (!(everyStop || approachingTerminus))
                    continue;
                forced++;
                if ((pt.m_State & PublicTransportFlags.RequireStop) == 0)
                {
                    pt.m_State |= PublicTransportFlags.RequireStop;
                    EntityManager.SetComponentData(veh, pt);
                }
            }
            return forced;
        }

        // Drop dictionary entries whose line is no longer in the live query (deleted while enabled). Reuses `scratch`
        // to gather stale keys so removal doesn't mutate the dictionary mid-enumeration.
        private static void PruneToLive<T>(Dictionary<Entity, T> dict, HashSet<Entity> live, List<Entity> scratch)
        {
            if (dict.Count == 0)
                return;
            scratch.Clear();
            foreach (Entity key in dict.Keys)
                if (!live.Contains(key)) scratch.Add(key);
            for (int i = 0; i < scratch.Count; i++)
                dict.Remove(scratch[i]);
        }

        // Measure a line's real loop time and EMA it (this now FEEDS the real-travel-time correction, not just the
        // diagnostic). Watches the vehicle occupying the terminus boarding slot; when that front vehicle CHANGES we stamp
        // the outgoing bus's departure frame, and when a bus we previously saw depart returns to the terminus we fold its
        // span (departure -> arrival = travel + intermediate dwells, EXCLUDING the terminus hold) into the line's EMA.
        // Comparable apples-to-apples with the pathfinder estimate (ComputeStableDuration), which also excludes the
        // terminus hold. Reads the world only; the correction it feeds is applied elsewhere. See AcceptLoopSample.
        private void MeasureLap(Entity line, Entity terminusStop, uint frame, float durUnits)
        {
            Entity curFront = Entity.Null;
            if (terminusStop != Entity.Null && EntityManager.HasComponent<BoardingVehicle>(terminusStop))
            {
                Entity f = EntityManager.GetComponentData<BoardingVehicle>(terminusStop).m_Vehicle;
                if (f != Entity.Null && EntityManager.HasComponent<PublicTransport>(f))
                {
                    PublicTransport pt = EntityManager.GetComponentData<PublicTransport>(f);
                    if ((pt.m_State & PublicTransportFlags.Boarding) != 0 && (pt.m_State & PublicTransportFlags.EnRoute) != 0)
                        curFront = f; // a serving bus is boarding the terminus right now
                }
            }

            m_LapFront.TryGetValue(line, out Entity prevFront);
            if (curFront == prevFront)
                return; // no change at the terminus slot this tick — nothing to measure

            if (prevFront != Entity.Null)
            {
                m_VehTerminusDepart[prevFront] = frame; // the previous front just vacated the terminus — a fresh loop begins
                m_VehHeld.Remove(prevFront);            // clear the early-held flag; measure this new loop clean
                m_VehLastRecordedStop.Remove(prevFront);
            }

            if (curFront != Entity.Null
                && m_VehTerminusDepart.TryGetValue(curFront, out uint dep) && frame > dep
                && !m_VehHeld.Contains(curFront))       // FEEDBACK GUARD: only trust a loop the bus ran WITHOUT an early hold
            {
                uint loop = frame - dep; // this bus's own departure -> return span (one full serving loop)
                if (loop >= kMinLoopFrames && loop <= kMaxLoopFrames && AcceptLoopSample(line, loop, durUnits))
                {
                    if (m_LineLoopSamples.TryGetValue(line, out int n) && n > 0)
                        m_LineLoopEma[line] += kLoopAlpha * (loop - m_LineLoopEma[line]);
                    else
                        m_LineLoopEma[line] = loop;
                    m_LineLoopSamples[line] = (m_LineLoopSamples.TryGetValue(line, out int nn) ? nn : 0) + 1;
                }
            }

            m_LapFront[line] = curFront;
        }

        // Reject implausible loop samples so the measurement survives BUNCHING. On a busy line a bus can roll through the
        // terminus while another occupies the boarding slot, so its pass is missed and the NEXT detected span is a
        // DOUBLE (~2x the real loop — this is what made #991323 read up to 4.75x live). Key insight: a missed pass makes
        // a span a MULTIPLE of the truth, never a fraction, so the TRUE single loop is the MINIMUM of the spans. We gate
        // against a running MIN rather than the EMA: the min drops freely toward the truth (a genuine lower value always
        // lowers the anchor), and anything well above it (a double-count or a stall) is rejected. Unlike an EMA-keyed
        // band this cannot be self-poisoned. A RUN of rejections means the anchor itself is stale — a glitch-low first
        // sample pinned it, or a route edit lengthened the loop — so we re-anchor upward and recalibrate (heals both).
        private bool AcceptLoopSample(Entity line, uint loop, float durUnits)
        {
            float estFrames = durUnits * 60f; // a route "duration unit" is 60 sim frames
            if (estFrames > 1f && (loop < 0.40f * estFrames || loop > 4.5f * estFrames))
                return false; // physically absurd vs the estimate — never trust it

            if (!m_LineLoopMin.TryGetValue(line, out float min))
            {
                m_LineLoopMin[line] = loop;      // first candidate seeds the anchor
                m_LineRejectStreak.Remove(line);
                return true;
            }
            if (loop <= min)
            {
                m_LineLoopMin[line] = loop;      // a lower true value — follow the anchor down
                m_LineRejectStreak.Remove(line);
                return true;
            }
            if (loop <= 1.6f * min)
            {
                m_LineRejectStreak.Remove(line); // a normal single near the min
                return true;
            }
            // loop > 1.6x min: a double-count or stall. Reject — unless it keeps happening, in which case the anchor is
            // stale (route lengthened, or the min was a glitch): re-anchor to this sample and recalibrate the value.
            int streak = (m_LineRejectStreak.TryGetValue(line, out int rs) ? rs : 0) + 1;
            if (streak >= kResetAfterRejects)
            {
                m_LineLoopMin[line] = loop;
                m_LineRejectStreak.Remove(line);
                m_LineLoopEma.Remove(line);      // old baseline was stale — re-measure the value from scratch
                m_LineLoopSamples.Remove(line);
                return true;
            }
            m_LineRejectStreak[line] = streak;
            return false;
        }

        // The per-line real-travel-time correction factor (dimensionless, RT-invariant): (real loop) / (estimated loop).
        // Uses the LIVE measurement once the line has logged enough clean loops; until then, the stop-density prior as a
        // cold-start seed. Clamped for safety (grow-only for fleet). durUnits is the line's estimated loop in route units.
        // Public so the panel/board (TransitParamsUISystem) can post the same corrected times the holds use.
        public float LineCorrection(Entity line, float durUnits, bool forFleet)
        {
            float estFrames = durUnits * 60f;
            float factor;
            if (m_LineLoopSamples.TryGetValue(line, out int n) && n >= kMinTrustSamples
                && m_LineLoopEma.TryGetValue(line, out float ema) && estFrames > 1f)
                factor = ema / estFrames;                                            // measured (frames/frames)
            else
                factor = ScheduleMath.DensityPriorRatio(CountStops(line), durUnits); // bootstrap from stop density
            return ScheduleMath.ClampCorrection(factor, forFleet);
        }

        // True once the line's correction is driven by LIVE measurement (>= kMinTrustSamples clean loops) rather than the
        // density prior — used by the panel to label the real-loop figure "measured" vs "estimated".
        public bool LineCorrectionMeasured(Entity line)
            => m_LineLoopSamples.TryGetValue(line, out int n) && n >= kMinTrustSamples;

        // Count the line's boarding stops (route waypoints connected to a stop platform), for the density prior. Matches
        // how FindTerminus / HoldAllStops identify a "stop" (a Connected waypoint whose target has a BoardingVehicle).
        private int CountStops(Entity line)
        {
            if (!EntityManager.HasBuffer<RouteWaypoint>(line))
                return 0;
            DynamicBuffer<RouteWaypoint> wps = EntityManager.GetBuffer<RouteWaypoint>(line, isReadOnly: true);
            int count = 0;
            for (int i = 0; i < wps.Length; i++)
            {
                Entity wp = wps[i].m_Waypoint;
                if (EntityManager.HasComponent<Connected>(wp))
                {
                    Entity st = EntityManager.GetComponentData<Connected>(wp).m_Connected;
                    if (st != Entity.Null && EntityManager.HasComponent<BoardingVehicle>(st))
                        count++;
                }
            }
            return count;
        }

        // Learn a stop's real arrival offset (frames from the terminus departure) from a boarding bus — but ONLY our own
        // line's bus, and ONLY if it ran this loop UNHELD (m_VehHeld). An unheld bus's arrival reflects real travel +
        // natural dwell, never the mod's holds, so this cannot feed back. Recorded once per arrival (m_VehLastRecordedStop).
        private void RecordStopOffset(Entity line, Entity wp, Entity veh, HashSet<Entity> lineVehicles, uint frame)
        {
            if (veh == Entity.Null || (lineVehicles != null && !lineVehicles.Contains(veh)))
                return;                                                              // foreign / no bus
            if (m_VehHeld.Contains(veh))
                return;                                                              // early-held this loop -> arrival inflated
            if (!m_VehTerminusDepart.TryGetValue(veh, out uint term) || frame <= term)
                return;                                                              // need a known terminus departure this run
            if (!m_ArrivedFrame.TryGetValue(veh, out uint arrived) || arrived <= term)
                return;                                                              // need its arrival time at this stop
            if (m_VehLastRecordedStop.TryGetValue(veh, out Entity last) && last == wp)
                return;                                                              // already recorded this arrival
            m_VehLastRecordedStop[veh] = wp;
            float offset = arrived - term;                                           // pure arrival offset from terminus (frames)
            if (offset < 1f)
                return;
            // Plausibility: a missed upstream-arrival detection could make one "offset" span extra ground; reject an
            // offset larger than the whole measured loop (with margin) so a glitch can't poison a stop.
            if (m_LineLoopEma.TryGetValue(line, out float loopF) && loopF > 1f && offset > 1.25f * loopF)
                return;
            if (m_StopOffsetSamples.TryGetValue(wp, out int n) && n > 0)
                m_StopOffsetEma[wp] += kLoopAlpha * (offset - m_StopOffsetEma[wp]);
            else
                m_StopOffsetEma[wp] = offset;
            m_StopOffsetSamples[wp] = (m_StopOffsetSamples.TryGetValue(wp, out int nn) ? nn : 0) + 1;
        }

        // Measured posted offset (minutes from the terminus) for a stop waypoint, once it has enough clean samples.
        // Public so the UI board posts the SAME per-stop times the holds use. False -> caller uses the estimate.
        public bool TryStopOffsetMinutes(Entity wp, out int minutes)
        {
            minutes = 0;
            if (m_StopOffsetSamples.TryGetValue(wp, out int n) && n >= kMinTrustSamples
                && m_StopOffsetEma.TryGetValue(wp, out float f) && m_Fpm > 0.01f)
            {
                minutes = (int)System.Math.Round(f / m_Fpm);
                return true;
            }
            return false;
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

        // Put the line's spacing behaviour back to the prefab default. Called for EVERY timetabled line (enabled or
        // not), so it doubles as the one-time repair for a save damaged by a pre-v0.2.3 version that zeroed the field.
        // Only writes when the value actually differs, so it is a no-op on a healthy line.
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
                // Logged because this is otherwise INVISIBLE: the game exposes no UI for unbunching, so without a line
                // in the log there is no way to confirm a damaged save was repaired. Fires once per line, then never.
                Mod.log.Info($"[SelfTest] unbunching restored on line#{line.Index}: {tl.m_UnbunchingFactor} -> {def} " +
                             $"(repairing a value written by an older version of this mod)");
                tl.m_UnbunchingFactor = def;
                EntityManager.SetComponentData(line, tl);
            }
        }
    }
}
