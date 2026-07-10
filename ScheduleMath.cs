using System;

namespace TransitTimetables
{
    // Pure timetable math shared by the dispatch system and the UI. Time conversions use the game's clock:
    // 1 in-game day = 262144 sim frames (TimeSystem.kTicksPerDay), so 1 in-game minute = 182.04 frames, and the
    // route "duration units" used by the vehicle-count math are 60-frame units (RouteUtils), i.e. ~0.33 minutes.
    //
    // Everything here is SCHEDULE-AWARE via a `schedule` argument (LineSchedule.Day/Night/DayAndNight): a night-only
    // line only ever runs the night interval inside the night window (its first departure is interpreted within that
    // window); a day-only line never uses the night interval and does not run at night.
    public static class ScheduleMath
    {
        public const float FramesPerMinute = 262144f / 1440f;   // ~182.04
        public const float UnitMinutes = 60f / FramesPerMinute; // ~0.3296 in-game minutes per stop-duration unit

        // Headway (minutes) in effect at a given minute-of-day, respecting the line's operating schedule.
        public static int IntervalFor(Setting s, TimetableSchedule sch, int minuteOfDay, int schedule)
        {
            int hour = Hour(minuteOfDay);
            if (schedule == LineSchedule.Night) return Pos(sch.m_NightInterval);                          // night-only
            if (schedule == LineSchedule.Day) return s.InPeakWindow(hour) ? Pos(sch.m_PeakInterval) : Pos(sch.m_OffPeakInterval); // day-only, never night
            if (s.InNightWindow(hour)) return Pos(sch.m_NightInterval);
            if (s.InPeakWindow(hour)) return Pos(sch.m_PeakInterval);
            return Pos(sch.m_OffPeakInterval);
        }

        // The effective first-departure minute-of-day. A night-only line's first departure is interpreted within the
        // night window: a value outside [NightStart, NightEnd) is clamped to the night window start.
        public static int FirstDeparture(Setting s, TimetableSchedule sch, int schedule)
        {
            int first = sch.m_FirstDeparture;
            // Clamp a first departure that falls outside the line's operating window to the window's start, so the
            // day's schedule always begins on an in-service minute: night-only -> NightStart, day-only -> NightEnd.
            if (schedule == LineSchedule.Night && !s.InNightWindow(Hour(first)))
                return (((s.NightStart % 24) + 24) % 24) * 60;
            if (schedule == LineSchedule.Day && s.InNightWindow(Hour(first)))
                return (((s.NightEnd % 24) + 24) % 24) * 60;
            return first;
        }

        // Is a minute-of-day inside the line's operating window? (Night-only: the night window; day-only: everything
        // else; both: always.)
        public static bool InService(Setting s, int schedule, int minuteOfDay)
        {
            int hour = Hour(minuteOfDay);
            if (schedule == LineSchedule.Night) return s.InNightWindow(hour);
            if (schedule == LineSchedule.Day) return !s.InNightWindow(hour);
            return true;
        }

        // Next scheduled departure as an ABSOLUTE minute from today's midnight (may be negative when tonight's night
        // service actually started yesterday, or exceed 1439 for tomorrow) that is >= nowMinute AND in-service. Scans
        // yesterday/today/tomorrow so a night window that wraps past midnight resolves correctly. Used by the hold.
        public static int NextDeparture(Setting s, TimetableSchedule sch, int schedule, int nowMinute)
        {
            int first = FirstDeparture(s, sch, schedule);
            for (int day = -1; day <= 1; day++)
            {
                int t = first + day * 1440;
                int guard = 0;
                while (guard < 4000)
                {
                    int minute = Mod1440(t);
                    if (!InService(s, schedule, minute)) break; // left the operating window -> this block is done
                    if (t >= nowMinute) return t;               // earliest in-window departure at/after now
                    t += IntervalFor(s, sch, minute, schedule);
                    guard++;
                }
            }
            return first;
        }

        // The day's departures listed FROM the first departure (printed-timetable style), as minute-of-day 0..1439,
        // stopping at the operating-window boundary. Fills up to `count`; returns how many.
        public static int DayFromFirst(Setting s, TimetableSchedule sch, int schedule, int[] outMin, int count)
        {
            int t = FirstDeparture(s, sch, schedule);
            int n = 0, guard = 0;
            while (n < count && guard < 4000)
            {
                int minute = Mod1440(t);
                outMin[n++] = minute;
                int next = t + IntervalFor(s, sch, minute, schedule);
                if (!InService(s, schedule, Mod1440(next))) break; // reached the window boundary
                t = next;
                guard++;
            }
            return n;
        }

        // The next `count` departures at/after nowMinute (as minute-of-day 0..1439), schedule-aware. Steps via
        // NextDeparture so night/day operating windows and midnight wrap are handled. Returns how many filled.
        public static int Upcoming(Setting s, TimetableSchedule sch, int schedule, int nowMinute, int[] outMin, int count)
        {
            int t = NextDeparture(s, sch, schedule, nowMinute);
            int n = 0, guard = 0;
            while (n < count && guard < 4000)
            {
                outMin[n++] = Mod1440(t);
                int next = NextDeparture(s, sch, schedule, t + 1);
                if (next <= t) break; // safety: schedule not advancing
                t = next;
                guard++;
            }
            return n;
        }

        // Round-trip time of one vehicle over the whole line, in in-game minutes.
        public static float RoundTripMinutes(float stableDurationUnits) => stableDurationUnits * UnitMinutes;

        // Vehicles needed to sustain the given headway = ceil(round-trip / interval), at least 1.
        public static int DerivedFleet(float stableDurationUnits, int intervalMinutes)
        {
            intervalMinutes = Pos(intervalMinutes);
            int fleet = (int)Math.Ceiling(RoundTripMinutes(stableDurationUnits) / intervalMinutes);
            return fleet < 1 ? 1 : fleet;
        }

        public static string FormatHm(int minuteOfDay)
        {
            int m = Mod1440(minuteOfDay);
            int hh = m / 60, mm = m % 60;
            return (hh < 10 ? "0" : "") + hh + ":" + (mm < 10 ? "0" : "") + mm;
        }

        private static int Hour(int minuteOfDay) => Mod1440(minuteOfDay) / 60;
        private static int Mod1440(int m) => ((m % 1440) + 1440) % 1440;
        private static int Pos(int v) => v < 1 ? 1 : v;
    }
}
