using System;

namespace Unity.Entities
{
    public interface IComponentData
    {
    }

    public struct Entity : IEquatable<Entity>
    {
        public int Index;

        public Entity(int index)
        {
            Index = index;
        }

        public static Entity Null => default;

        public bool Equals(Entity other) => Index == other.Index;

        public override bool Equals(object obj) => obj is Entity other && Equals(other);

        public override int GetHashCode() => Index;

        public static bool operator ==(Entity left, Entity right) => left.Equals(right);

        public static bool operator !=(Entity left, Entity right) => !left.Equals(right);

        public override string ToString() => $"Entity({Index})";
    }
}

namespace Colossal.Serialization.Entities
{
    public interface IWriter
    {
        void Write(bool value);
        void Write(ushort value);
        void Write(byte value);
        void Write(Unity.Entities.Entity value);
    }

    public interface IReader
    {
        void Read(out bool value);
        void Read(out ushort value);
        void Read(out byte value);
        void Read(out Unity.Entities.Entity value);
    }

    public interface ISerializable
    {
        void Serialize<TWriter>(TWriter writer) where TWriter : IWriter;
        void Deserialize<TReader>(TReader reader) where TReader : IReader;
    }
}

namespace TransitTimetables
{
    public static class LineSchedule
    {
        public const int Day = 0;
        public const int Night = 1;
        public const int DayAndNight = 2;
    }

    public struct CustomPeakSchedule
    {
        public bool m_Enabled;
        public ushort m_Interval;
        public ushort m_Start1;
        public ushort m_End1;
        public ushort m_Start2;
        public ushort m_End2;
    }

    public sealed class TransitTimetablesSetting
    {
        public int MorningPeakStart = 7;
        public int MorningPeakEnd = 9;
        public int EveningPeakStart = 16;
        public int EveningPeakEnd = 18;
        public int NightStart = 22;
        public int NightEnd = 6;

        public bool InPeakWindow(int hour)
        {
            return ScheduleMath.InWindow(hour, MorningPeakStart, MorningPeakEnd)
                || ScheduleMath.InWindow(hour, EveningPeakStart, EveningPeakEnd);
        }

        public bool InNightWindow(int hour)
        {
            return ScheduleMath.InWindow(hour, NightStart, NightEnd);
        }
    }
}
