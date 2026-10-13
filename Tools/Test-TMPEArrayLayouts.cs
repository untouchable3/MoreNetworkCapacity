using System;
using MoreNetworkCapacity.Compatibility;

namespace MoreNetworkCapacity
{
    internal static class Logging
    {
        internal static int Errors;
        internal static void Info(string text) { }
        internal static void Warning(string text) { }
        internal static void Error(string text) { ++Errors; }
    }
}
public sealed class EndSlot
{
    public int Id, Config;
    public bool Start;
    public EndSlot(int id, bool start) { Id = id; Start = start; }
}
public sealed class TMPELayoutFixture
{
    public EndSlot[] Split = new EndSlot[73728];
    public EndSlot[] Interleaved = new EndSlot[73728];
    public ulong[] SegmentBuckets = new ulong[576];
    public ulong[] NodeBuckets = new ulong[512];
}
public static class TMPEArrayLayoutTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static int Main()
    {
        try
        {
            TMPELayoutFixture value = new TMPELayoutFixture();
            EndSlot start = new EndSlot(100, true); start.Config = 7;
            EndSlot end = new EndSlot(100, false); end.Config = 11;
            value.Split[100] = start; value.Split[36864 + 100] = end;
            ArrayCompatibility.ExpandSplit(typeof(TMPELayoutFixture).GetField("Split"), value, 36864, 65024,
                delegate(int id, bool first) { return new EndSlot(id, first); }, "split layout fixture");
            Check(value.Split.Length == 130048, "Split capacity incorrect");
            Check(object.ReferenceEquals(value.Split[100], start) && object.ReferenceEquals(value.Split[65024 + 100], end), "Saved start/end entries moved incorrectly");
            Check(start.Config == 7 && end.Config == 11, "Existing configuration changed");
            for (int id = 36864; id < 65024; ++id)
                Check(value.Split[id].Id == id && value.Split[id].Start
                    && value.Split[65024 + id].Id == id && !value.Split[65024 + id].Start, "New split slots wrong");
            EndSlot[] once = value.Split;
            ArrayCompatibility.ExpandSplit(typeof(TMPELayoutFixture).GetField("Split"), value, 36864, 65024, null, "already migrated");
            Check(object.ReferenceEquals(value.Split, once), "Repeated split pass moved data again");

            value.Interleaved[200] = start; value.Interleaved[201] = end;
            ArrayCompatibility.ExpandDirect(typeof(TMPELayoutFixture).GetField("Interleaved"), value, 73728, 130048,
                delegate(int index) { return new EndSlot(index / 2, (index & 1) == 0); }, false, "interleaved fixture");
            Check(object.ReferenceEquals(value.Interleaved[200], start) && object.ReferenceEquals(value.Interleaved[201], end), "Interleaved ends incorrectly split");
            for (int id = 36864; id < 65024; ++id)
                Check(value.Interleaved[id * 2].Id == id && value.Interleaved[id * 2].Start
                    && value.Interleaved[id * 2 + 1].Id == id && !value.Interleaved[id * 2 + 1].Start, "High interleaved ends uninitialized");

            value.SegmentBuckets[575] = 123; value.NodeBuckets[511] = 456;
            ArrayCompatibility.ExpandDirect(typeof(TMPELayoutFixture).GetField("SegmentBuckets"), value, 576, 1016, null, false, "segment buckets");
            ArrayCompatibility.ExpandDirect(typeof(TMPELayoutFixture).GetField("NodeBuckets"), value, 512, 1016, null, false, "node buckets");
            Check(value.SegmentBuckets[575] == 123 && value.NodeBuckets[511] == 456, "Pending bucket data lost");
            value.SegmentBuckets[64512 >> 6] |= 1UL << (64512 & 63);
            Check(value.SegmentBuckets.Length == 1016 && value.NodeBuckets.Length == 1016 && value.SegmentBuckets[64512 >> 6] == 1, "High-ID bucket coverage incorrect");
            value.Split = new EndSlot[73800]; EndSlot[] unexpected = value.Split;
            ArrayCompatibility.ExpandSplit(typeof(TMPELayoutFixture).GetField("Split"), value, 36864, 65024, null, "unexpected layout");
            Check(object.ReferenceEquals(value.Split, unexpected) && MoreNetworkCapacity.Logging.Errors == 1, "Unknown split layout was rewritten");
            Console.WriteLine("PASS: actual array helper preserves split/interleaved configurations, initializes all high ends, keeps pending buckets and rejects unknown layouts.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
