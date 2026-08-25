using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TransitTimetables
{
    // A scheduled LAYOVER at one player-chosen mid-route stop (called "Terminus B" by the legacy UI): the vehicle
    // departs that stop at its
    // SCHEDULED arrival plus m_HoldMinutes, and every later stop's posted time carries the same shift. Because the
    // departure is anchored to the scheduled arrival (not the actual one), the wait itself is variable: an on-time
    // vehicle waits the full X, a late one only the remainder, and a vehicle more than X late leaves as soon as
    // boarding is done — the stop absorbs delay instead of compounding it, which is what a real timing point does.
    //
    // This is deliberately NOT a second departure grid. One closed loop with a fixed vehicle set must depart both
    // ends at the same rate or vehicles pile up at one end without bound, so B inherits A's headway by construction
    // and only the phase (the arrival offset + X) differs. Layover identity alone does not make the stop a schedule
    // origin; every non-A hold still goes through m_VehStopHold banking and stays OUT of the measured travel time
    // (a layover is a chosen wait, not the route getting slower). The fleet math is the one place that must ADD it:
    // the cycle genuinely is X minutes longer.
    //
    // This is dwell configuration, not the optional LineTerminalB timing-point identity. A SEPARATE sibling component
    // — never grow the shipped TimetableSchedule (adding a field to a shipped
    // ISerializable breaks every save). Leading version byte so THIS one can gain fields later behind a version
    // gate, the same growth path LineMeasuredTravel/CustomPeakSchedule follow. Lives on the LINE entity, not the
    // stop: a stop's boarding slot is shared across lines, and the layover is one line's choice.
    public struct LineLayover : IComponentData, ISerializable
    {
        public Entity m_Stop;        // the chosen layover stop (a stop entity, like TimetableSchedule.m_TerminusStop)
        public ushort m_HoldMinutes; // X — scheduled minutes of layover past the scheduled arrival

        private const byte kVersion = 1;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Stop);
            writer.Write(m_HoldMinutes);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out m_Stop);
            reader.Read(out m_HoldMinutes);
        }
    }
}
