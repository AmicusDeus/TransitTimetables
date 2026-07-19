using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TransitTimetables
{
    // Per-line peak-time override (community contribution, PR #5). Stored as a SEPARATE component rather than fields on
    // TimetableSchedule so it can't break existing save games — ECS deserialization safely ignores a component that is
    // missing on an old save. When m_Enabled is true, the scheduler uses these two local peak windows + interval instead
    // of the global peak settings for THIS line only.
    //
    // A version byte is written FIRST (and read/discarded first) so this component can gain fields later WITHOUT breaking
    // saves that already contain it — the same sibling-component + version-byte growth path the mod's other components
    // must follow (you can never add a field to a shipped ISerializable without a version gate).
    public struct CustomPeakSchedule : IComponentData, ISerializable
    {
        public bool m_Enabled;
        public ushort m_Interval;
        public ushort m_Start1;
        public ushort m_End1;
        public ushort m_Start2;
        public ushort m_End2;

        private const byte kVersion = 1;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Enabled);
            writer.Write(m_Interval);
            writer.Write(m_Start1);
            writer.Write(m_End1);
            writer.Write(m_Start2);
            writer.Write(m_End2);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte _);   // version (reserved for future field growth)
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
            m_End2 = 18,
        };
    }
}
