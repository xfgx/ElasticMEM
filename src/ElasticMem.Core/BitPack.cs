using System.Numerics;

namespace ElasticMem.Core;

/// <summary>Упаковка целых чисел фиксированной разрядности в плотный битовый поток.</summary>
public static class BitPack
{
    public static int BitsNeeded(ulong v) => v == 0 ? 0 : 64 - BitOperations.LeadingZeroCount(v);

    public static int PackedBytes(int count, int bits) => (int)(((long)count * bits + 7) / 8);

    public static ulong MaxValue(int bits) => bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;

    public static ulong Get(byte[] p, int index, int bits)
    {
        if (bits == 0) return 0;
        long bitPos = (long)index * bits;
        ulong r = 0;
        int got = 0;
        while (got < bits)
        {
            int byteIdx = (int)(bitPos >> 3);
            int off = (int)(bitPos & 7);
            int take = Math.Min(8 - off, bits - got);
            ulong part = (ulong)((p[byteIdx] >> off) & ((1 << take) - 1));
            r |= part << got;
            got += take;
            bitPos += take;
        }
        return r;
    }

    public static void Set(byte[] p, int index, int bits, ulong v)
    {
        if (bits == 0) return;
        long bitPos = (long)index * bits;
        int done = 0;
        while (done < bits)
        {
            int byteIdx = (int)(bitPos >> 3);
            int off = (int)(bitPos & 7);
            int take = Math.Min(8 - off, bits - done);
            int mask = ((1 << take) - 1) << off;
            int part = (int)((v >> done) & (ulong)((1 << take) - 1)) << off;
            p[byteIdx] = (byte)((p[byteIdx] & ~mask) | part);
            done += take;
            bitPos += take;
        }
    }
}
