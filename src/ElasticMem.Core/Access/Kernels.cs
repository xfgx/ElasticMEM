using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ElasticMem.Core.Access;

/// <summary>Векторные ядра декодирования/агрегации (AVX2/AVX-512 через System.Numerics.Vector).</summary>
public static class Kernels
{
    public static ulong SumU64(ReadOnlySpan<ulong> s)
    {
        var acc = Vector<ulong>.Zero;
        int i = 0, w = Vector<ulong>.Count;
        for (; i + w <= s.Length; i += w) acc += new Vector<ulong>(s[i..]);
        ulong r = Vector.Sum(acc);
        for (; i < s.Length; i++) r += s[i];
        return r;
    }

    public static ulong SumU32(ReadOnlySpan<uint> s)
    {
        var acc = Vector<ulong>.Zero;
        int i = 0, w = Vector<uint>.Count;
        for (; i + w <= s.Length; i += w)
        {
            Vector.Widen(new Vector<uint>(s[i..]), out var a, out var b);
            acc += a + b;
        }
        ulong r = Vector.Sum(acc);
        for (; i < s.Length; i++) r += s[i];
        return r;
    }

    public static ulong SumU16(ReadOnlySpan<ushort> s)
    {
        // ≤ 65536 элементов на вызов: накопление в uint без переполнения
        var acc = Vector<uint>.Zero;
        int i = 0, w = Vector<ushort>.Count;
        for (; i + w <= s.Length; i += w)
        {
            Vector.Widen(new Vector<ushort>(s[i..]), out var a, out var b);
            acc += a + b;
        }
        ulong r = 0;
        for (int k = 0; k < Vector<uint>.Count; k++) r += acc[k];
        for (; i < s.Length; i++) r += s[i];
        return r;
    }

    public static ulong SumU8(ReadOnlySpan<byte> s)
    {
        var acc = Vector<uint>.Zero;
        int i = 0, w = Vector<byte>.Count;
        for (; i + w <= s.Length; i += w)
        {
            Vector.Widen(new Vector<byte>(s[i..]), out var a, out var b);
            Vector.Widen(a + b, out var c, out var d); // a+b ≤ 510 — помещается в ushort
            acc += c + d;
        }
        ulong r = 0;
        for (int k = 0; k < Vector<uint>.Count; k++) r += acc[k];
        for (; i < s.Length; i++) r += s[i];
        return r;
    }

    public static ulong SumPacked(byte[] pay, int bits, int n)
    {
        ulong mask = BitPack.MaxValue(bits), r = 0;
        for (int j = 0; j < n; j++)
        {
            int bit = j * bits;
            r += (BinaryPrimitives.ReadUInt64LittleEndian(pay.AsSpan(bit >> 3)) >> (bit & 7)) & mask;
        }
        return r;
    }

    public static void WidenU8(ReadOnlySpan<byte> s, ulong b, Span<ulong> d) { for (int i = 0; i < s.Length; i++) d[i] = b + s[i]; }
    public static void WidenU16(ReadOnlySpan<ushort> s, ulong b, Span<ulong> d)
    {
        int i = 0, w = Vector<ushort>.Count;
        var vb = new Vector<ulong>(b);
        for (; i + w <= s.Length; i += w)
        {
            Vector.Widen(new Vector<ushort>(s[i..]), out var a0, out var a1);
            Vector.Widen(a0, out var q0, out var q1); Vector.Widen(a1, out var q2, out var q3);
            int c = Vector<ulong>.Count;
            (q0 + vb).CopyTo(d[i..]); (q1 + vb).CopyTo(d[(i + c)..]); (q2 + vb).CopyTo(d[(i + 2 * c)..]); (q3 + vb).CopyTo(d[(i + 3 * c)..]);
        }
        for (; i < s.Length; i++) d[i] = b + s[i];
    }
    public static void WidenU32(ReadOnlySpan<uint> s, ulong b, Span<ulong> d)
    {
        int i = 0, w = Vector<uint>.Count, c = Vector<ulong>.Count;
        var vb = new Vector<ulong>(b);
        for (; i + w <= s.Length; i += w)
        {
            Vector.Widen(new Vector<uint>(s[i..]), out var a0, out var a1);
            (a0 + vb).CopyTo(d[i..]); (a1 + vb).CopyTo(d[(i + c)..]);
        }
        for (; i < s.Length; i++) d[i] = b + s[i];
    }
}
