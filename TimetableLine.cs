using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TransitTimetables
{
    // Per-line schedule state, added by HourlyFleetSystem to every managed transport line (Route entity) and
    // serialized into saves so baselines and applied levels survive save/load.
    //
    //   m_BaselineCount — the line's "designed" peak fleet: the vehicle count the player set (captured the first
    //                     time the mod manages the line, or refreshed by the Recapture action). All targets are a
    //                     percentage of this.
    //   m_LastApplied   — the vehicle count we most recently asserted via the vanilla vehicle-count policy
    //                     (-1 = nothing applied yet). Lets us skip re-issuing an unchanged target every hour.
    //   m_Flags         — bit 0 (CustomFlag): this line uses its OWN peak/off-peak/night levels below instead of
    //                     the global defaults. Windows (when peak is) stay global in v1.
    //   m_PeakLevel / m_OffPeakLevel / m_NightLevel — this line's own service levels (percent), always stored
    //                     (initialised from the global defaults on capture) and edited from the panel; only APPLIED
    //                     when CustomFlag is set.
    public struct TimetableLine : IComponentData, ISerializable
    {
        public const byte CustomFlag = 1;

        public int m_BaselineCount;
        public int m_LastApplied;
        public byte m_Flags;
        public byte m_PeakLevel;
        public byte m_OffPeakLevel;
        public byte m_NightLevel;

        public bool HasCustomSchedule => (m_Flags & CustomFlag) != 0;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_BaselineCount);
            writer.Write(m_LastApplied);
            writer.Write(m_Flags);
            writer.Write(m_PeakLevel);
            writer.Write(m_OffPeakLevel);
            writer.Write(m_NightLevel);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_BaselineCount);
            reader.Read(out m_LastApplied);
            reader.Read(out m_Flags);
            reader.Read(out m_PeakLevel);
            reader.Read(out m_OffPeakLevel);
            reader.Read(out m_NightLevel);
        }
    }
}
