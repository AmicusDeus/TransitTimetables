using System.Collections.Generic;
using Colossal;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Simulation;

namespace TransitTimetables
{
    public class Mod : IMod
    {
        public static ILog log = LogManager.GetLogger($"{nameof(TransitTimetables)}.{nameof(Mod)}").SetShowsErrorsInUI(false);
        public static Setting ActiveSetting;

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            ActiveSetting = new Setting(this);
            ActiveSetting.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEn(ActiveSetting));
            AssetDatabase.global.LoadSettings(nameof(TransitTimetables), ActiveSetting, new Setting(this));

            // Runtime day-length calibrator: keeps the frame<->minute math correct under slow-time mods (Time2Work).
            // Registered FIRST so it refreshes before the dispatch/UI read it within the frame.
            updateSystem.UpdateAt<TimebaseSystem>(SystemUpdatePhase.GameSimulation);
            // Fleet-control helper (per-line vehicle-count via the line's own VehicleInterval modifier).
            updateSystem.UpdateAt<HourlyFleetSystem>(SystemUpdatePhase.GameSimulation);
            // Fixed-departure timetabling: terminus hold, derived fleet, retire-at-terminus.
            updateSystem.UpdateAt<TimetableDispatchSystem>(SystemUpdatePhase.GameSimulation);
            // Line-panel editor + stop departure board bindings (floating overlay, does not pause the game).
            updateSystem.UpdateAt<TransitParamsUISystem>(SystemUpdatePhase.UIUpdate);

            log.Info("[SelfTest] TransitTimetables loaded (fixed-departure timetables).");
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
            if (ActiveSetting != null)
            {
                ActiveSetting.UnregisterInOptionsUI();
                ActiveSetting = null;
            }
        }
    }

    // Minimal English locale (full localization once mechanics are proven, same pipeline as EconomyTweaks).
    public class LocaleEn : IDictionarySource
    {
        private readonly Setting m_S;
        public LocaleEn(Setting setting) { m_S = setting; }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_S.GetSettingsLocaleID(), "Transit Timetables" },
                { m_S.GetOptionTabLocaleID(Setting.Section), "Main" },
                { m_S.GetOptionGroupLocaleID(Setting.GroupWindows), "Peak windows" },
                { m_S.GetOptionGroupLocaleID(Setting.GroupCompat), "Compatibility" },

                { m_S.GetOptionLabelLocaleID(nameof(Setting.RealisticTripsCompat)), "Adapt to slow-time mods (Realistic Trips / Time2Work) — ALPHA" },
                { m_S.GetOptionDescLocaleID(nameof(Setting.RealisticTripsCompat)), "Turn this ON if you use Time2Work / \"Realistic Trips\" or any mod that makes the in-game day longer, so departures, stop times and fleet sizes stay correct instead of running early. The mod detects the real day length automatically, so leaving it on does nothing when no such mod is installed. Turn it OFF for the exact original behaviour. NOTE: this compatibility path is an ALPHA and has NOT been tested against Realistic Trips in-game yet — please report anything odd. Under Realistic Trips your lines will correctly use fewer vehicles than before." },

                { m_S.GetOptionLabelLocaleID(nameof(Setting.MorningPeakStart)), "Morning peak start" },
                { m_S.GetOptionDescLocaleID(nameof(Setting.MorningPeakStart)), "Hour the morning peak service level begins." },
                { m_S.GetOptionLabelLocaleID(nameof(Setting.MorningPeakEnd)), "Morning peak end" },
                { m_S.GetOptionDescLocaleID(nameof(Setting.MorningPeakEnd)), "Hour the morning peak service level ends." },
                { m_S.GetOptionLabelLocaleID(nameof(Setting.EveningPeakStart)), "Evening peak start" },
                { m_S.GetOptionDescLocaleID(nameof(Setting.EveningPeakStart)), "Hour the evening peak service level begins." },
                { m_S.GetOptionLabelLocaleID(nameof(Setting.EveningPeakEnd)), "Evening peak end" },
                { m_S.GetOptionDescLocaleID(nameof(Setting.EveningPeakEnd)), "Hour the evening peak service level ends." },
                { m_S.GetOptionLabelLocaleID(nameof(Setting.NightStart)), "Night start" },
                { m_S.GetOptionDescLocaleID(nameof(Setting.NightStart)), "Hour night service begins (may wrap past midnight)." },
                { m_S.GetOptionLabelLocaleID(nameof(Setting.NightEnd)), "Night end" },
                { m_S.GetOptionDescLocaleID(nameof(Setting.NightEnd)), "Hour night service ends." },
            };
        }

        public void Unload() { }
    }
}
