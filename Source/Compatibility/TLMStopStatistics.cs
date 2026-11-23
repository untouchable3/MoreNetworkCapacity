using System;
using System.Collections.Generic;
using System.IO;

namespace MoreNetworkCapacity.Compatibility
{
    // TLM's legacy tables and SaveIds remain 32768 * 17. Expanding them changes
    // implicit field boundaries in its unversioned table layout.
    internal static class TLMStopStatistics
    {
        private const int Slots = 17;
        private const int RecordBytes = 2 + Slots * 20;
        private const int Magic = 0x53434E4D;
        private static readonly object Sync = new object();
        private static Dictionary<ushort, Record> _records = new Dictionary<ushort, Record>();
        private static object _owner;

        private sealed class Record
        {
            internal readonly long[] Income = new long[Slots];
            internal readonly int[] Passengers = new int[Slots * 3];
        }

        internal static bool IsHigh(ushort id)
        { return id >= Capacity.VanillaNodeCapacity && id < Capacity.ExpandedNodeCapacity; }

        private static void Attach(object owner)
        {
            if (_owner != null && !object.ReferenceEquals(_owner, owner)) _records.Clear();
            _owner = owner;
        }

        internal static void Clear()
        { lock (Sync) { _records.Clear(); _owner = null; } }

        internal static void Add(object owner, ushort id, long income, bool tourist, bool student)
        {
            lock (Sync)
            {
                Attach(owner);
                Record record;
                if (!_records.TryGetValue(id, out record))
                { record = new Record(); _records.Add(id, record); }
                unchecked
                {
                    record.Income[16] += income;
                    // Actual TLM IL compares the value-type Citizen to null, so
                    // every call counts a passenger, including a zero Citizen.
                    ++record.Passengers[48];
                    if (tourist) ++record.Passengers[49];
                    if (student) ++record.Passengers[50];
                }
            }
        }

        internal static long Get(object owner, ushort id, int slot)
        {
            lock (Sync)
            {
                Attach(owner);
                Record record;
                if (!_records.TryGetValue(id, out record)) return 0;
                if (slot >= 0) return record.Income[slot];
                long total = 0;
                unchecked { for (int i = 0; i < Slots; ++i) total += record.Income[i]; }
                return total;
            }
        }

        internal static void Rotate(uint slot)
        {
            if (slot >= 16) throw new ArgumentOutOfRangeException("slot");
            lock (Sync)
            {
                foreach (Record record in _records.Values)
                {
                    record.Income[slot] = record.Income[16]; record.Income[16] = 0;
                    for (int column = 0; column < 3; ++column)
                    {
                        record.Passengers[slot * 3 + column] = record.Passengers[48 + column];
                        record.Passengers[48 + column] = 0;
                    }
                }
            }
        }

        internal static byte[] Serialize()
        {
            lock (Sync)
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(Magic); writer.Write(1); writer.Write(_records.Count);
                List<ushort> ids = new List<ushort>(_records.Keys); ids.Sort();
                foreach (ushort id in ids)
                {
                    writer.Write(id); Record record = _records[id];
                    for (int slot = 0; slot < Slots; ++slot)
                    {
                        writer.Write(record.Income[slot]);
                        for (int col = 0; col < 3; ++col) writer.Write(record.Passengers[slot * 3 + col]);
                    }
                }
                return stream.ToArray();
            }
        }

        internal static void Deserialize(byte[] data)
        {
            Dictionary<ushort, Record> records = new Dictionary<ushort, Record>();
            if (data != null)
            {
                if (data.Length < 12 || data.Length > 12 + RecordBytes *
                    (Capacity.ExpandedNodeCapacity - Capacity.VanillaNodeCapacity))
                    throw new InvalidDataException("Invalid TLM high-stop statistics length.");
                using (BinaryReader reader = new BinaryReader(new MemoryStream(data, false)))
                {
                    if (reader.ReadInt32() != Magic || reader.ReadInt32() != 1)
                        throw new InvalidDataException("Unsupported TLM high-stop statistics format.");
                    int count = reader.ReadInt32();
                    if (count < 0 || 12L + (long)count * RecordBytes != data.Length)
                        throw new InvalidDataException("Invalid TLM high-stop statistics count.");
                    for (int i = 0; i < count; ++i)
                    {
                        ushort id = reader.ReadUInt16();
                        if (!IsHigh(id) || records.ContainsKey(id))
                            throw new InvalidDataException("Invalid or duplicate TLM high-stop ID.");
                        Record record = new Record();
                        for (int slot = 0; slot < Slots; ++slot)
                        {
                            record.Income[slot] = reader.ReadInt64();
                            for (int col = 0; col < 3; ++col) record.Passengers[slot * 3 + col] = reader.ReadInt32();
                        }
                        records.Add(id, record);
                    }
                }
            }
            lock (Sync) { _records = records; _owner = null; }
        }
    }
}
