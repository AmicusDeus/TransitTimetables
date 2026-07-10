using Game.Routes;
using Unity.Entities;

namespace TransitTimetables
{
    // The day/night operating schedule of a transport line, read from the same source the game's own ScheduleSection
    // uses (the Route option mask). Shared by the UI and dispatch systems so timetable math respects when a line runs.
    public static class LineSchedule
    {
        public const int Day = 0;         // day-only
        public const int Night = 1;       // night-only
        public const int DayAndNight = 2; // both (default)

        // Mirrors Game.UI.InGame.ScheduleSection: Day option => Day-only; else Night option => Night-only; else both.
        public static int Of(EntityManager em, Entity line)
        {
            if (!em.HasComponent<Route>(line))
                return DayAndNight;
            Route r = em.GetComponentData<Route>(line);
            if (RouteUtils.CheckOption(r, RouteOption.Day)) return Day;
            if (RouteUtils.CheckOption(r, RouteOption.Night)) return Night;
            return DayAndNight;
        }
    }
}
