using Unity.Entities;

namespace TransitTimetables
{
    // Pure configured-terminal comparisons shared by dispatch and UI-facing systems. Route
    // membership and boarding-stop validity are deliberately left to the resolver that owns an
    // EntityManager; these helpers answer only what the saved line configuration says.
    public static class LineTerminalSelection
    {
        public static Entity PrimaryTerminal(in TimetableSchedule schedule)
            => schedule.m_TerminusStop;

        public static Entity SecondaryTerminal(in LineTerminalB terminalB)
            => terminalB.m_Stop;

        public static bool HasSecondTerminal(in LineTerminalB terminalB)
            => SecondaryTerminal(terminalB) != Entity.Null;

        // `primaryTerminal` is the EFFECTIVE Terminal A (including the first-stop fallback), not merely the stored
        // m_TerminusStop. Keeping this comparison here gives dispatch and UI one tested duplicate-rejection rule.
        public static bool CanConfigureSecondary(Entity primaryTerminal, Entity candidate)
            => candidate != Entity.Null && candidate != primaryTerminal;

        public static bool IsPrimaryTerminal(in TimetableSchedule schedule, Entity stop)
            => stop != Entity.Null && stop == PrimaryTerminal(schedule);

        public static bool IsSecondaryTerminal(in LineTerminalB terminalB, Entity stop)
            => stop != Entity.Null && stop == SecondaryTerminal(terminalB);

        public static bool IsConfiguredTerminal(in TimetableSchedule schedule, in LineTerminalB terminalB, Entity stop)
            => IsPrimaryTerminal(schedule, stop) || IsSecondaryTerminal(terminalB, stop);
    }
}
