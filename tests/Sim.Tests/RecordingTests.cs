using Nitrogenesis.Sim;
using Nitrogenesis.Sim.History;
using Nitrogenesis.Sim.Racing;

public class RecordingTests
{
    [Fact]
    public void RoundTripKeepsQuantizedSamples()
    {
        var rec = new Recording(4);
        rec.Add(10.123f, 20.987f, 65535);
        rec.Add(10.6f, 21.2f, 3);           // heading wraps forward past 0
        rec.Add(9.9f, 255.99f, 65000);      // and backwards
        rec.Add(4095.5f, 0f, 32768);
        rec.Add(0.001f, 3000f, 0);          // beyond the initial capacity: grows
        var back = Recording.Decode(rec.Encode());
        Assert.Equal(5, back.Count);
        Assert.Equal(SimInfo.SimVersion, back.SimVersion);
        for (int i = 0; i < rec.Count; i++)
        {
            Assert.Equal(rec.X(i), back.X(i));
            Assert.Equal(rec.Y(i), back.Y(i));
            Assert.Equal(rec.Heading(i), back.Heading(i));
        }
        Assert.Equal(2591 / 256f, rec.X(0));    // 10.123 × 256 = 2591.49 → nearest 1/256
        Assert.True(MathF.Abs(rec.Y(0) - 20.987f) <= 0.5f / 256);
        Assert.Equal(new[] { 65535, 3, 65000, 32768, 0 }, Enumerable.Range(0, 5).Select(back.Heading));
    }

    [Fact]
    public void EmptyRecordingRoundTrips()
    {
        var back = Recording.Decode(new Recording(0, simVersion: 7).Encode());
        Assert.Equal(0, back.Count);
        Assert.Equal(7, back.SimVersion);
    }

    [Fact]
    public void ReferenceRunRecordsCompactly()
    {
        var rt = new RacingTrack(TestTracks.Load("hairpins.track"), new RacingSettings());
        var path = ReferenceDriver.Run(rt).Path; // about 34 s
        var rec = new Recording(path.Count);
        foreach (var (x, y) in path) rec.Add(x, y, 0);
        byte[] data = rec.Encode();
        Assert.True(data.Length < 20_000, $"{data.Length} bytes for {path.Count} ticks");
        var back = Recording.Decode(data);
        Assert.Equal(path.Count, back.Count);
        Assert.True(MathF.Abs(back.X(1000) - path[1000].X) <= 0.5f / 256 + 1e-4f);
    }

    [Fact]
    public void AddWithinCapacityDoesNotAllocate()
    {
        var rec = new Recording(1000);
        rec.Add(1, 1, 1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1; i < 1000; i++) rec.Add(i * 0.1f, i * 0.2f, i * 7);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void DecodeRejectsBadData()
    {
        var rec = new Recording();
        rec.Add(1, 2, 3);
        rec.Add(4, 5, 6);
        byte[] good = rec.Encode();

        Assert.Throws<InvalidDataException>(() => Recording.Decode(good.AsSpan(0, 10)));
        byte[] magic = (byte[])good.Clone();
        magic[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => Recording.Decode(magic));
        byte[] version = (byte[])good.Clone();
        version[4] = 2;
        Assert.Throws<InvalidDataException>(() => Recording.Decode(version));
        byte[] moreSamples = (byte[])good.Clone();
        moreSamples[12] = 3; // claims 3 samples, holds 2
        Assert.Throws<InvalidDataException>(() => Recording.Decode(moreSamples));
        byte[] fewerSamples = (byte[])good.Clone();
        fewerSamples[12] = 1; // claims 1 sample, holds 2
        Assert.Throws<InvalidDataException>(() => Recording.Decode(fewerSamples));
        byte[] garbage = (byte[])good.Clone();
        for (int i = 16; i < garbage.Length; i++) garbage[i] = 0xFF;
        Assert.Throws<InvalidDataException>(() => Recording.Decode(garbage));
    }
}
