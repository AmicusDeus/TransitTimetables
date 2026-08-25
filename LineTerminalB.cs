using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TransitTimetables
{
    // Optional second timing point for a line. Terminal A remains
    // TimetableSchedule.m_TerminusStop; keeping B in a sibling component avoids changing the
    // already-shipped, unversioned TimetableSchedule serialization sequence.
    //
    // The component is only added when B is configured. A missing component and m_Stop ==
    // Entity.Null both mean "no Terminal B". Its own version marker keeps this new layout explicit;
    // compatibility with old saves comes from leaving TimetableSchedule itself byte-for-byte unchanged.
    public struct LineTerminalB : IComponentData, ISerializable
    {
        public Entity m_Stop;

        private const byte kVersion = 1;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Stop);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            if (version >= 1)
                reader.Read(out m_Stop);
            else
                m_Stop = Entity.Null;
        }

        public static LineTerminalB Default() => new LineTerminalB
        {
            m_Stop = Entity.Null,
        };
    }
}
