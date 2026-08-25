using System;
using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Unity.Entities;

internal sealed class RecordingWriter : IWriter
{
    public List<object> Values { get; } = new List<object>();

    public void Write(bool value) => Values.Add(value);

    public void Write(ushort value) => Values.Add(value);

    public void Write(byte value) => Values.Add(value);

    public void Write(Entity value) => Values.Add(value);
}

internal sealed class RecordingReader : IReader
{
    private readonly IReadOnlyList<object> m_Values;
    private int m_Index;

    public RecordingReader(IReadOnlyList<object> values)
    {
        m_Values = values ?? throw new ArgumentNullException(nameof(values));
    }

    public void Read(out bool value) => value = Read<bool>();

    public void Read(out ushort value) => value = Read<ushort>();

    public void Read(out byte value) => value = Read<byte>();

    public void Read(out Entity value) => value = Read<Entity>();

    private T Read<T>()
    {
        if (m_Index >= m_Values.Count)
            throw new InvalidOperationException("Attempted to read beyond the recorded serialization payload.");

        object value = m_Values[m_Index++];
        if (!(value is T typed))
        {
            throw new InvalidOperationException(
                $"Serialization item {m_Index - 1} has type {value?.GetType().Name ?? "null"}; expected {typeof(T).Name}.");
        }

        return typed;
    }
}
