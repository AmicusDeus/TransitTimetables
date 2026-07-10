using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TransitTimetables
{
    // T2 per-line fixed-departure timetable. Separate component from TimetableLine so adding it doesn't change the
    // TimetableLine save format (existing saves stay compatible). Added to a line when the player opts it in.
    //
    //   m_Enabled          — this line runs a fixed timetable (departs the terminus at clock times, holds if early,
    //                        goes immediately if on-time/late). Overrides the hourly % model for this line.
    //   m_FirstDeparture   — first departure of the day, minutes since midnight (0..1439).
    //   m_Peak/OffPeak/NightInterval — headway in MINUTES for each time-of-day window (windows are the global ones).
    //
    // The fleet size is DERIVED (round-trip time / current interval) — the player sets departures, not vehicle
    // count. Per-stop scheduled times are computed from measured travel; the mod enforces the hold only at the
    // terminus (a timing point) so it never blocks other lines mid-route.
    public struct TimetableSchedule : IComponentData, ISerializable
    {
        public bool m_Enabled;
        public ushort m_FirstDeparture;
        public ushort m_PeakInterval;
        public ushort m_OffPeakInterval;
        public ushort m_NightInterval;
        // The stop the player designated as this line's terminus: the schedule anchor, the hold point, and where a
        // retiring vehicle finishes its loop before returning to the depot. Entity.Null = fall back to the first stop.
        public Entity m_TerminusStop;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Enabled);
            writer.Write(m_FirstDeparture);
            writer.Write(m_PeakInterval);
            writer.Write(m_OffPeakInterval);
            writer.Write(m_NightInterval);
            writer.Write(m_TerminusStop);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Enabled);
            reader.Read(out m_FirstDeparture);
            reader.Read(out m_PeakInterval);
            reader.Read(out m_OffPeakInterval);
            reader.Read(out m_NightInterval);
            reader.Read(out m_TerminusStop);
        }

        // Sensible starting schedule; m_Enabled is false so the component only becomes active when the player
        // explicitly toggles it on (a stray interval edit can't silently enable a line).
        public static TimetableSchedule Default() => new TimetableSchedule
        {
            m_Enabled = false,
            m_FirstDeparture = 300, // 05:00
            m_PeakInterval = 8,
            m_OffPeakInterval = 12,
            m_NightInterval = 30,
        };
    }
}
