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
        public const string GroupLimit = "Vehicle limit";
        public const string GroupExperimental = "Experimental";

        public Setting(IMod mod) : base(mod) { }

        // NOTE: initializers double as the settings-migration failsafe (missing keys in an old .coc keep these
        // values instead of defaulting to 0/false).

        // Peak windows (hour of day, 0-23). A line's per-window timetable intervals switch by these: hours inside a
        // morning or evening window are "peak", night hours are "night", everything else is "off-peak". The night
        // window may wrap past midnight (start > end).
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
        public int NightEnd { get; set; } = 5;

        // Raise the maximum number of vehicles allowed on a line, above the game's length/stops-derived cap. 1 =
        // untouched (pure vanilla ceiling); N = up to about N times the normal maximum per line. Also widens the
        // game's own vehicle-count slider. (Auto-raised while any line is timetabled so derived fleets aren't clamped.)
        [SettingsUISlider(min = 1f, max = 6f, step = 1f, unit = "integer")]
        [SettingsUISection(Section, GroupLimit)]
        public int VehicleLimitMultiplier { get; set; } = 1;

        // Experimental: log a per-line stop-sharing analysis (which stops several lines share). Read-only report.
        [SettingsUISection(Section, GroupExperimental)]
        public bool AnalyzeSharedStops { get; set; } = false;

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
            NightEnd = 5;
            VehicleLimitMultiplier = 1;
            AnalyzeSharedStops = false;
        }
    }
}
