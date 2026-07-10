using System.Collections.Generic;
using System.Text;
using Colossal.UI.Binding;
using Game.Common;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.Tools;
using Game.UI;
using Unity.Entities;

namespace TransitTimetables
{
    // Backs both timetable UIs, driven by the current tool selection:
    //   * A transport LINE is selected  -> the timetable editor (injected into the native line info panel).
    //   * A STOP is selected            -> the departure board (every line's next departures from this stop), shown
    //                                      in the floating panel, which auto-opens; plus "set as terminus".
    public partial class TransitParamsUISystem : UISystemBase
    {
        private const string Group = "TransitParams";

        private TimeSystem m_TimeSystem;
        private HourlyFleetSystem m_Fleet;
        private ToolSystem m_ToolSystem;

        // Selected LINE timetable cache.
        private bool m_SelHas;
        private bool m_SelTtEnabled;
        private int m_SelTtFirst, m_SelTtPeak, m_SelTtOffPeak, m_SelTtNight, m_SelTtInterval, m_SelTtFleet;
        private string m_SelTtNext = "";
        private int m_SelSchedule = 2;              // RouteSchedule: 0=Day, 1=Night, 2=DayAndNight (which intervals apply)
        private string m_PeakHours = "", m_NightHours = "";
        private GetterValueBinding<bool> m_SelHasB, m_SelTtEnabledB;
        private GetterValueBinding<int> m_SelTtFirstB, m_SelTtPeakB, m_SelTtOffPeakB, m_SelTtNightB, m_SelTtIntervalB, m_SelTtFleetB, m_SelScheduleB;
        private GetterValueBinding<string> m_SelTtNextB, m_PeakHoursB, m_NightHoursB;

        // Selected STOP departure board.
        private bool m_SelStopHas;
        private string m_SelStopBoard = "[]";
        private int m_AutoOpen;
        private Entity m_LastSel = Entity.Null;
        private GetterValueBinding<bool> m_SelStopHasB;
        private GetterValueBinding<string> m_SelStopBoardB;
        private GetterValueBinding<int> m_AutoOpenB;

        // "Selected line" context for the stop board's per-line terminus button: the last TransportLine the player
        // opened (the one whose route shows in the left panel), so "terminus for Line X" targets exactly that line.
        private Entity m_LastLine = Entity.Null;
        private int m_SelStopLineNum;
        private bool m_SelStopLineServes;          // does m_LastLine serve the selected stop AND carry a timetable?
        private GetterValueBinding<int> m_SelStopLineNumB;
        private GetterValueBinding<bool> m_SelStopLineServesB;

        private static Setting S => Mod.ActiveSetting;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_TimeSystem = World.GetOrCreateSystemManaged<TimeSystem>();
            m_Fleet = World.GetOrCreateSystemManaged<HourlyFleetSystem>();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();

            m_SelHasB = new GetterValueBinding<bool>(Group, "selHas", () => m_SelHas);
            m_SelTtEnabledB = new GetterValueBinding<bool>(Group, "selTtEnabled", () => m_SelTtEnabled);
            m_SelTtFirstB = new GetterValueBinding<int>(Group, "selTtFirst", () => m_SelTtFirst);
            m_SelTtPeakB = new GetterValueBinding<int>(Group, "selTtPeak", () => m_SelTtPeak);
            m_SelTtOffPeakB = new GetterValueBinding<int>(Group, "selTtOffPeak", () => m_SelTtOffPeak);
            m_SelTtNightB = new GetterValueBinding<int>(Group, "selTtNight", () => m_SelTtNight);
            m_SelTtIntervalB = new GetterValueBinding<int>(Group, "selTtInterval", () => m_SelTtInterval);
            m_SelTtFleetB = new GetterValueBinding<int>(Group, "selTtFleet", () => m_SelTtFleet);
            m_SelTtNextB = new GetterValueBinding<string>(Group, "selTtNext", () => m_SelTtNext ?? "");
            m_SelScheduleB = new GetterValueBinding<int>(Group, "selSchedule", () => m_SelSchedule);
            m_PeakHoursB = new GetterValueBinding<string>(Group, "peakHours", () => m_PeakHours ?? "");
            m_NightHoursB = new GetterValueBinding<string>(Group, "nightHours", () => m_NightHours ?? "");
            m_SelStopHasB = new GetterValueBinding<bool>(Group, "selStopHas", () => m_SelStopHas);
            m_SelStopBoardB = new GetterValueBinding<string>(Group, "selStopBoard", () => m_SelStopBoard ?? "[]");
            m_AutoOpenB = new GetterValueBinding<int>(Group, "autoOpen", () => m_AutoOpen);
            m_SelStopLineNumB = new GetterValueBinding<int>(Group, "selStopLineNum", () => m_SelStopLineNum);
            m_SelStopLineServesB = new GetterValueBinding<bool>(Group, "selStopLineServes", () => m_SelStopLineServes);
            AddBinding(m_SelHasB);
            AddBinding(m_SelTtEnabledB);
            AddBinding(m_SelTtFirstB);
            AddBinding(m_SelTtPeakB);
            AddBinding(m_SelTtOffPeakB);
            AddBinding(m_SelTtNightB);
            AddBinding(m_SelTtIntervalB);
            AddBinding(m_SelTtFleetB);
            AddBinding(m_SelTtNextB);
            AddBinding(m_SelScheduleB);
            AddBinding(m_PeakHoursB);
            AddBinding(m_NightHoursB);
            AddBinding(m_SelStopHasB);
            AddBinding(m_SelStopBoardB);
            AddBinding(m_AutoOpenB);
            AddBinding(m_SelStopLineNumB);
            AddBinding(m_SelStopLineServesB);

            AddBinding(new TriggerBinding<bool>(Group, "setSelTtEnabled", v => MutateSchedule(v, (ref TimetableSchedule sch, bool on) => sch.m_Enabled = on)));
            AddBinding(new TriggerBinding<int>(Group, "setSelTtFirst", v => MutateSchedule(v, (ref TimetableSchedule sch, int x) => sch.m_FirstDeparture = (ushort)Clamp(x, 0, 1439))));
            AddBinding(new TriggerBinding<int>(Group, "setSelTtPeak", v => MutateSchedule(v, (ref TimetableSchedule sch, int x) => sch.m_PeakInterval = (ushort)Clamp(x, 1, 240))));
            AddBinding(new TriggerBinding<int>(Group, "setSelTtOffPeak", v => MutateSchedule(v, (ref TimetableSchedule sch, int x) => sch.m_OffPeakInterval = (ushort)Clamp(x, 1, 240))));
            AddBinding(new TriggerBinding<int>(Group, "setSelTtNight", v => MutateSchedule(v, (ref TimetableSchedule sch, int x) => sch.m_NightInterval = (ushort)Clamp(x, 1, 240))));
            // Two terminus scopes: every timetabled line at this stop, or only the line the player has open on the left.
            AddBinding(new TriggerBinding(Group, "setSelTerminusAll", () => SetSelectedStopAsTerminus(Entity.Null)));
            AddBinding(new TriggerBinding(Group, "setSelTerminusLine", () => { if (m_LastLine != Entity.Null) SetSelectedStopAsTerminus(m_LastLine); }));
        }

        private delegate void RefSchedAction<T>(ref TimetableSchedule sch, T value);

        // Read-modify-write the selected line's timetable, creating the component on first touch.
        private void MutateSchedule<T>(T value, RefSchedAction<T> action)
        {
            Entity sel = m_ToolSystem != null ? m_ToolSystem.selected : Entity.Null;
            if (sel == Entity.Null || !EntityManager.HasComponent<TransportLine>(sel))
                return;
            bool had = EntityManager.HasComponent<TimetableSchedule>(sel);
            TimetableSchedule sch = had ? EntityManager.GetComponentData<TimetableSchedule>(sel) : TimetableSchedule.Default();
            action(ref sch, value);
            if (!had)
                EntityManager.AddComponent<TimetableSchedule>(sel);
            EntityManager.SetComponentData(sel, sch);
        }

        // Make the selected stop the terminus. onlyLine == Entity.Null → every timetabled line that serves the stop;
        // otherwise → just that one line (the line the player has open), provided it is timetabled and serves the stop.
        private void SetSelectedStopAsTerminus(Entity onlyLine)
        {
            Entity stop = m_ToolSystem != null ? m_ToolSystem.selected : Entity.Null;
            if (stop == Entity.Null || !EntityManager.HasComponent<BoardingVehicle>(stop) || !EntityManager.HasBuffer<ConnectedRoute>(stop))
                return;
            foreach (Entity line in StopLines(stop))
            {
                if (onlyLine != Entity.Null && line != onlyLine)
                    continue;
                if (!EntityManager.HasComponent<TimetableSchedule>(line))
                    continue;
                TimetableSchedule sch = EntityManager.GetComponentData<TimetableSchedule>(line);
                if (sch.m_TerminusStop != stop)
                {
                    sch.m_TerminusStop = stop;
                    EntityManager.SetComponentData(line, sch);
                }
            }
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        // RouteSchedule of a line (mirrors the game's ScheduleSection): 0=Day-only, 1=Night-only, 2=DayAndNight.
        private int ScheduleOf(Entity line) => LineSchedule.Of(EntityManager, line);

        private static string Hr(int h) => (h < 10 ? "0" : "") + h;
        private static string Range(int a, int b) => Hr(a) + "-" + Hr(b);

        protected override void OnUpdate()
        {
            base.OnUpdate();
            Refresh();
            m_SelHasB.Update();
            m_SelTtEnabledB.Update();
            m_SelTtFirstB.Update();
            m_SelTtPeakB.Update();
            m_SelTtOffPeakB.Update();
            m_SelTtNightB.Update();
            m_SelTtIntervalB.Update();
            m_SelTtFleetB.Update();
            m_SelTtNextB.Update();
            m_SelScheduleB.Update();
            m_PeakHoursB.Update();
            m_NightHoursB.Update();
            m_SelStopHasB.Update();
            m_SelStopBoardB.Update();
            m_AutoOpenB.Update();
            m_SelStopLineNumB.Update();
            m_SelStopLineServesB.Update();
        }

        private void Refresh()
        {
            Setting s = S;
            Entity sel = m_ToolSystem != null ? m_ToolSystem.selected : Entity.Null;
            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;

            // Window hours (global) so the editor can show WHEN peak/night apply.
            if (s != null)
            {
                m_PeakHours = Range(s.MorningPeakStart, s.MorningPeakEnd) + ", " + Range(s.EveningPeakStart, s.EveningPeakEnd);
                m_NightHours = Range(s.NightStart, s.NightEnd);
            }

            bool isLine = s != null && sel != Entity.Null
                && EntityManager.HasComponent<TransportLine>(sel)
                && EntityManager.HasComponent<RouteWaypoint>(sel);
            m_SelHas = isLine;
            if (isLine)
            {
                m_LastLine = sel;                 // remember the open line for the stop board's per-line terminus button
                m_SelSchedule = ScheduleOf(sel);  // which intervals apply: 0=Day, 1=Night, 2=DayAndNight
            }
            if (isLine && EntityManager.HasComponent<TimetableSchedule>(sel))
            {
                TimetableSchedule sch = EntityManager.GetComponentData<TimetableSchedule>(sel);
                m_SelTtEnabled = sch.m_Enabled;
                m_SelTtFirst = sch.m_FirstDeparture;
                m_SelTtPeak = sch.m_PeakInterval;
                m_SelTtOffPeak = sch.m_OffPeakInterval;
                m_SelTtNight = sch.m_NightInterval;
                m_SelTtInterval = ScheduleMath.IntervalFor(s, sch, nowMin, m_SelSchedule);
                float dur = m_Fleet != null ? m_Fleet.LineStableDurationUnits(sel) : 0f;
                m_SelTtFleet = dur > 1f ? ScheduleMath.DerivedFleet(dur, m_SelTtInterval) : 0;
                Entity term = TerminusWaypoint(sel, sch);
                m_SelTtNext = DeparturesAtStop(sel, sch, term, term, m_SelSchedule, nowMin); // next departures from now
            }
            else
            {
                m_SelTtEnabled = false;
                m_SelTtFirst = 300; m_SelTtPeak = 8; m_SelTtOffPeak = 12; m_SelTtNight = 30;
                m_SelTtInterval = 0; m_SelTtFleet = 0; m_SelTtNext = "";
            }

            // Stop selection -> departure board.
            bool isStop = s != null && sel != Entity.Null
                && EntityManager.HasComponent<BoardingVehicle>(sel)
                && EntityManager.HasBuffer<ConnectedRoute>(sel);
            m_SelStopHas = isStop;
            if (isStop)
            {
                m_SelStopBoard = BuildStopBoard(s, sel, nowMin);
                // Per-line terminus context: is the last-open line timetabled AND does it serve this stop?
                bool lastOk = m_LastLine != Entity.Null && EntityManager.Exists(m_LastLine);
                m_SelStopLineServes = lastOk
                    && EntityManager.HasComponent<TimetableSchedule>(m_LastLine)
                    && WaypointForStop(m_LastLine, sel) != Entity.Null;
                m_SelStopLineNum = lastOk && EntityManager.HasComponent<RouteNumber>(m_LastLine)
                    ? EntityManager.GetComponentData<RouteNumber>(m_LastLine).m_Number : 0;
            }
            else
            {
                m_SelStopBoard = "[]";
                m_SelStopLineServes = false;
                m_SelStopLineNum = 0;
            }

            // Auto-open the floating panel the first tick a (line-bearing) stop becomes the selection.
            if (isStop && sel != m_LastSel)
                m_AutoOpen++;
            m_LastSel = sel;
        }

        // JSON: [{ "n": <lineNumber>, "tt": <bool>, "term": <bool>, "d": "<HH:MM, HH:MM, ...>" }, ...]
        // term = this stop is the line's EFFECTIVE terminus (explicit m_TerminusStop, else the first-stop fallback
        // that the dispatch system actually holds/retires at) — matches TerminusWaypoint below.
        private string BuildStopBoard(Setting s, Entity stop, int nowMin)
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (Entity line in StopLines(stop))
            {
                int number = EntityManager.HasComponent<RouteNumber>(line) ? EntityManager.GetComponentData<RouteNumber>(line).m_Number : line.Index;
                bool hasSched = EntityManager.HasComponent<TimetableSchedule>(line);
                TimetableSchedule sch = hasSched ? EntityManager.GetComponentData<TimetableSchedule>(line) : default;
                bool tt = hasSched && sch.m_Enabled;
                string dep = "";
                bool term = false;
                if (tt)
                {
                    Entity terminusWp = TerminusWaypoint(line, sch);
                    Entity stopWp = WaypointForStop(line, stop);
                    dep = DeparturesAtStop(line, sch, terminusWp, stopWp, ScheduleOf(line), nowMin);
                    // Badge the EFFECTIVE terminus (explicit or first-stop fallback), i.e. the stop TerminusWaypoint
                    // resolves to — so the star lands where the dispatch system actually holds/retires vehicles.
                    Entity termStop = terminusWp != Entity.Null && EntityManager.HasComponent<Connected>(terminusWp)
                        ? EntityManager.GetComponentData<Connected>(terminusWp).m_Connected : Entity.Null;
                    term = termStop != Entity.Null && termStop == stop;
                }
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"n\":").Append(number).Append(",\"tt\":").Append(tt ? "true" : "false")
                  .Append(",\"term\":").Append(term ? "true" : "false").Append(",\"d\":\"").Append(dep).Append("\"}");
            }
            sb.Append(']');
            return sb.ToString();
        }

        // Distinct lines serving a stop (via each connected waypoint's Owner).
        private IEnumerable<Entity> StopLines(Entity stop)
        {
            var seen = new HashSet<Entity>();
            if (!EntityManager.HasBuffer<ConnectedRoute>(stop))
                yield break;
            DynamicBuffer<ConnectedRoute> routes = EntityManager.GetBuffer<ConnectedRoute>(stop, isReadOnly: true);
            for (int i = 0; i < routes.Length; i++)
            {
                Entity wp = routes[i].m_Waypoint;
                if (!EntityManager.HasComponent<Owner>(wp))
                    continue;
                Entity line = EntityManager.GetComponentData<Owner>(wp).m_Owner;
                if (line != Entity.Null && EntityManager.HasComponent<TransportLine>(line) && seen.Add(line))
                    yield return line;
            }
        }

        // The waypoint on a line connected to a given stop (Entity.Null if none).
        private Entity WaypointForStop(Entity line, Entity stop)
        {
            if (!EntityManager.HasBuffer<RouteWaypoint>(line))
                return Entity.Null;
            DynamicBuffer<RouteWaypoint> wps = EntityManager.GetBuffer<RouteWaypoint>(line, isReadOnly: true);
            for (int i = 0; i < wps.Length; i++)
            {
                Entity wp = wps[i].m_Waypoint;
                if (EntityManager.HasComponent<Connected>(wp) && EntityManager.GetComponentData<Connected>(wp).m_Connected == stop)
                    return wp;
            }
            return Entity.Null;
        }

        // The line's terminus waypoint: chosen stop's waypoint, else the first stop's waypoint.
        private Entity TerminusWaypoint(Entity line, TimetableSchedule sch)
        {
            if (sch.m_TerminusStop != Entity.Null)
            {
                Entity wp = WaypointForStop(line, sch.m_TerminusStop);
                if (wp != Entity.Null) return wp;
            }
            if (!EntityManager.HasBuffer<RouteWaypoint>(line))
                return Entity.Null;
            DynamicBuffer<RouteWaypoint> wps = EntityManager.GetBuffer<RouteWaypoint>(line, isReadOnly: true);
            for (int i = 0; i < wps.Length; i++)
            {
                Entity wp = wps[i].m_Waypoint;
                if (EntityManager.HasComponent<Connected>(wp)
                    && EntityManager.HasComponent<BoardingVehicle>(EntityManager.GetComponentData<Connected>(wp).m_Connected))
                    return wp;
            }
            return Entity.Null;
        }

        // The NEXT departures (from now) as seen AT `stopWp`: each terminus departure shifted by travel terminus ->
        // stopWp. A terminus departure D appears here as D+offset, so we list terminus departures from now-offset.
        // Up to 6, "HH:MM, ...".
        private string DeparturesAtStop(Entity line, TimetableSchedule sch, Entity terminusWp, Entity stopWp, int schedule, int nowMin)
        {
            if (terminusWp == Entity.Null || stopWp == Entity.Null)
                return "";
            int offset = (int)System.Math.Round(TravelUnitsBetween(line, terminusWp, stopWp) * ScheduleMath.UnitMinutes);
            // Clamp the seed so a stop whose first arrival is still ahead (offset > now, early morning) advertises the
            // real first bus rather than extrapolating yesterday's sequence backwards across midnight.
            int seed = nowMin - offset;
            if (seed < 0) seed = 0;
            int[] deps = new int[6];
            int n = ScheduleMath.Upcoming(S, sch, schedule, seed, deps, 6);
            var sb = new StringBuilder();
            for (int k = 0; k < n; k++)
            {
                if (k > 0) sb.Append(", ");
                sb.Append(ScheduleMath.FormatHm(deps[k] + offset));
            }
            return sb.ToString();
        }

        // Travel time (route units) from waypoint `fromWp` forward around the loop to `toWp`, including dwell at
        // intermediate stops. 0 if `fromWp == toWp` (the terminus itself).
        private float TravelUnitsBetween(Entity line, Entity fromWp, Entity toWp)
        {
            if (fromWp == toWp)
                return 0f;
            if (!EntityManager.HasBuffer<RouteWaypoint>(line) || !EntityManager.HasBuffer<RouteSegment>(line) || !EntityManager.HasComponent<PrefabRef>(line))
                return 0f;
            DynamicBuffer<RouteWaypoint> wps = EntityManager.GetBuffer<RouteWaypoint>(line, isReadOnly: true);
            DynamicBuffer<RouteSegment> segs = EntityManager.GetBuffer<RouteSegment>(line, isReadOnly: true);
            int len = wps.Length;
            if (len == 0 || segs.Length < len)
                return 0f;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            float stopDur = EntityManager.HasComponent<TransportLineData>(prefab) ? EntityManager.GetComponentData<TransportLineData>(prefab).m_StopDuration : 1f;

            int fromPos = -1, toPos = -1;
            for (int k = 0; k < len; k++)
            {
                if (wps[k].m_Waypoint == fromWp) fromPos = k;
                if (wps[k].m_Waypoint == toWp) toPos = k;
            }
            if (fromPos < 0 || toPos < 0)
                return 0f;

            float total = 0f;
            int idx = fromPos, guard = 0;
            while (idx != toPos && guard <= len)
            {
                Entity seg = segs[idx].m_Segment;
                if (EntityManager.HasComponent<PathInformation>(seg))
                    total += EntityManager.GetComponentData<PathInformation>(seg).m_Duration;
                int nextPos = (idx + 1) % len;
                Entity nextWp = wps[nextPos].m_Waypoint;
                if (nextPos != toPos && EntityManager.HasComponent<VehicleTiming>(nextWp))
                    total += stopDur;
                idx = nextPos;
                guard++;
            }
            return total;
        }
    }
}
