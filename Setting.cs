using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace TransitTimetables
{
    [FileLocation(nameof(TransitTimetables))]
    public class Setting : ModSetting
    {
        public const string Section = "Main";
        public const string GroupWindows = "Peak windows";
        public const string GroupRealism = "Realistic travel time";
        public const string GroupCompat = "Compatibility";
        public const string GroupGeneral = "General";

        public Setting(IMod mod) : base(mod) { }

        // NOTE: initializers double as the settings-migration failsafe (missing keys in an old .coc keep these
        // values instead of defaulting to 0/false).

        // Peak windows (hour of day, 0-23). A line's per-window timetable intervals switch by these: hours inside a
        // morning or evening window are "peak", night hours are "night", everything else is "off-peak". The night
        // window may wrap past midnight (start > end).
        //
        // The NIGHT window defaults to vanilla's own transport night, 22:00-06:00 (TransportLineSystem hardcodes
        // isNight = normalizedTime < 0.25f || normalizedTime >= 11f/12f). Matching it matters: for a day-only or
        // night-only line VANILLA decides whether the line runs at all — it forces the vehicle count to 0 outside its
        // own window — so any disagreement means this mod posts departures for buses vanilla has already retired, or
        // silently stops holding while the line is still running. Existing players keep whatever their .coc already
        // holds (the loader overwrites these initializers), so changing this default never moves anyone's setting.
        [SettingsUISlider(min = 0f, max = 23f, step = 1f, unit = "integer")]
        [SettingsUISection(Section, GroupWindows)]
        public int MorningPeakStart { get; set; } = 6;

        [SettingsUISlider(min = 0f, max = 23f, step = 1f, unit = "integer")]
        [SettingsUISection(Section, GroupWindows)]
        public int MorningPeakEnd { get; set; } = 9;

        [SettingsUISlider(min = 0f, max = 23f, step = 1f, unit = "integer")]
        [SettingsUISection(Section, GroupWindows)]
        public int EveningPeakStart { get; set; } = 15;

        [SettingsUISlider(min = 0f, max = 23f, step = 1f, unit = "integer")]
        [SettingsUISection(Section, GroupWindows)]
        public int EveningPeakEnd { get; set; } = 19;

        [SettingsUISlider(min = 0f, max = 23f, step = 1f, unit = "integer")]
        [SettingsUISection(Section, GroupWindows)]
        public int NightStart { get; set; } = 22;

        [SettingsUISlider(min = 0f, max = 23f, step = 1f, unit = "integer")]
        [SettingsUISection(Section, GroupWindows)]
        public int NightEnd { get; set; } = 6; // 06:00 = vanilla's transport day start (RouteUtils.TRANSPORT_DAY_START_TIME 0.25f)

        // Realistic travel time: CS2's own pathfinder estimate of how long a line takes systematically UNDERSHOOTS the
        // real, simulated loop time (live-measured ~1.7x on sparse lines to ~2.5x on stop-dense ones — acceleration and
        // braking at every stop that the free-flow estimate ignores). The mod measures each line's real loop live and
        // corrects for it. Both toggles default OFF so existing timetables are unchanged until the player opts in; the
        // correction is RT-invariant (frame-based), so it composes with the slow-time compatibility above.
        //
        // Master toggle — apply the correction to POSTED TIMES and stop holds so the board matches reality (no cost).
        [SettingsUISection(Section, GroupRealism)]
        public bool RealisticTravelTime { get; set; } = false;

        // Also size the FLEET to the real loop. This is the one that COSTS MONEY: holding a tight headway on a line whose
        // real loop is ~2x the estimate needs ~2x the vehicles (and upkeep). OFF = keep the estimate-based count; ON =
        // provision for the real loop (grow-only; never cuts a line below the estimate; capped).
        [SettingsUISection(Section, GroupRealism)]
        public bool ProvisionRealFleet { get; set; } = false;

        // Compatibility: adapt the timetable's frame<->minute math to slow-time mods (Time2Work / "Realistic Trips")
        // that lengthen the in-game day. Default OFF, so the base mod runs its pure vanilla-clock timing for the vast
        // majority who use no such mod. Turn it ON only under a slow-time mod: it then AUTO-DETECTS the real day length
        // at runtime so departures/stop times/fleet stay correct instead of running early. OFF pins it to the vanilla
        // 262144 frames/day (exact original behaviour). TimebaseSystem nudges (logs) if it detects a slow-time mod
        // while this is OFF. See TimebaseSystem.
        [SettingsUISection(Section, GroupCompat)]
        public bool RealisticTripsCompat { get; set; } = false;

        // Keep platform achievements enabled while this mod is active (the game otherwise disables them for any mod).
        [SettingsUISection(Section, GroupGeneral)]
        public bool EnableAchievements { get; set; } = true;

        // Which time-of-day window an hour falls in (the timetable interval switches on these).
        public bool InNightWindow(int hour) => InWindow(hour, NightStart, NightEnd);
        public bool InPeakWindow(int hour) => InWindow(hour, MorningPeakStart, MorningPeakEnd) || InWindow(hour, EveningPeakStart, EveningPeakEnd);

        // Half-open [start, end); wraps past midnight when start > end (e.g. night 22..5).
        private static bool InWindow(int hour, int start, int end)
        {
            if (start == end) return false;
            return start < end ? (hour >= start && hour < end) : (hour >= start || hour < end);
        }

        public override void SetDefaults()
        {
            MorningPeakStart = 6;
            MorningPeakEnd = 9;
            EveningPeakStart = 15;
            EveningPeakEnd = 19;
            NightStart = 22;
            NightEnd = 6; // keep in lockstep with the initializer above (this runs on an explicit "reset to defaults")
            RealisticTravelTime = false;
            ProvisionRealFleet = false;
            RealisticTripsCompat = false;
            EnableAchievements = true;
        }
    }
}
