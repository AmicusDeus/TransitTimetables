using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TransitTimetables
{
    // CustomPeakSchedule stores per-line peak-time overrides. Adding these fields to a separate component instead of
    // TimetableSchedule prevents breaking player save games, as the game's ECS deserialization safely ignores missing
    // components on load. When m_Enabled is true, the scheduling system uses these local peak windows and intervals
    // instead of the global mod settings.
    public struct CustomPeakSchedule : IComponentData, ISerializable
    {
        public bool m_Enabled;
        public ushort m_Interval;
        public ushort m_Start1;
        public ushort m_End1;
        public ushort m_Start2;
        public ushort m_End2;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Enabled);
            writer.Write(m_Interval);
            writer.Write(m_Start1);
            writer.Write(m_End1);
            writer.Write(m_Start2);
            writer.Write(m_End2);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Enabled);
            reader.Read(out m_Interval);
            reader.Read(out m_Start1);
            reader.Read(out m_End1);
            reader.Read(out m_Start2);
            reader.Read(out m_End2);
        }

        public static CustomPeakSchedule Default() => new CustomPeakSchedule
        {
            m_Enabled = false,
            m_Interval = 5,
            m_Start1 = 7,
            m_End1 = 9,
            m_Start2 = 16,
            m_End2 = 18
        };
    }
}
