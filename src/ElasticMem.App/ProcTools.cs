using System.Runtime.InteropServices;

namespace ElasticMem.App;

/// <summary>Роль процесса многопроцессного приложения (Chromium, Electron, Steam webhelper).</summary>
public enum ProcRole { Main, Renderer, Gpu, Audio, Network, Utility, Crashpad, Other }

public static class ProcRoles
{
    public static ProcRole Classify(string? cmd)
    {
        if (string.IsNullOrEmpty(cmd)) return ProcRole.Main;
        string c = cmd.ToLowerInvariant();
        if (!c.Contains("--type=")) return ProcRole.Main;
        if (c.Contains("--type=gpu-process")) return ProcRole.Gpu;
        if (c.Contains("--type=crashpad-handler")) return ProcRole.Crashpad;
        if (c.Contains("audio.mojom") || c.Contains("--utility-sub-type=audio")) return ProcRole.Audio;
        if (c.Contains("network.mojom")) return ProcRole.Network;
        if (c.Contains("--type=renderer")) return ProcRole.Renderer;
        if (c.Contains("--type=utility")) return ProcRole.Utility;
        return ProcRole.Other;
    }

    /// <summary>Эти процессы не трогаем никогда: сжатие GPU-процесса даёт рывки картинки, аудио-сервиса — заикание звука.</summary>
    public static bool NeverTrim(ProcRole r) => r is ProcRole.Gpu or ProcRole.Audio or ProcRole.Crashpad;

    public static string Ru(ProcRole r) => r switch
    {
        ProcRole.Main => "основной", ProcRole.Renderer => "вкладка/окно", ProcRole.Gpu => "GPU", ProcRole.Audio => "звук",
        ProcRole.Network => "сеть", ProcRole.Utility => "служебный", ProcRole.Crashpad => "crashpad", _ => "прочее",
    };
}

/// <summary>
/// Точный учёт возврата страниц: перед сжатием запоминаем выборку собственных (не общих) резидентных страниц,
/// после — спрашиваем систему, какие из них снова в рабочем наборе. В отличие от «рабочий набор вырос»,
/// не путает возврат со свежими выделениями (именно это дало «156%» у Chrome).
/// </summary>
public sealed class WsProbe
{
    private readonly UIntPtr[] _pairs; // VirtualAddress, Attributes, …
    public int Count => _pairs.Length / 2;
    private WsProbe(UIntPtr[] pairs) => _pairs = pairs;

    public static WsProbe? Capture(IntPtr h, int maxSample = 16384, int seed = 1)
    {
        int n = 64 * 1024;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var buf = new UIntPtr[n + 1];
            if (Native.K32QueryWorkingSet(h, buf, (uint)(buf.Length * IntPtr.Size)))
            {
                long count = (long)(ulong)buf[0];
                var priv = new List<ulong>((int)Math.Min(count, 1 << 20));
                for (long i = 1; i <= count && i < buf.Length; i++)
                {
                    ulong e = (ulong)buf[i];
                    if ((e & 0x100) != 0) continue;           // общая страница (DLL, общий буфер) — не наша
                    priv.Add(e & ~0xFFFUL);
                }
                if (priv.Count == 0) return null;
                var rnd = new Random(seed);
                int take = Math.Min(maxSample, priv.Count);
                for (int i = 0; i < take; i++) { int j = rnd.Next(i, priv.Count); (priv[i], priv[j]) = (priv[j], priv[i]); }
                var pairs = new UIntPtr[take * 2];
                for (int i = 0; i < take; i++) pairs[2 * i] = (UIntPtr)priv[i];
                return new WsProbe(pairs);
            }
            int err = Marshal.GetLastWin32Error();
            if (err != 24 /*ERROR_BAD_LENGTH*/) return null;
            long need = (long)(ulong)buf[0];
            n = (int)Math.Min(int.MaxValue / 16, Math.Max(n * 2, need + need / 8 + 1024));
        }
        return null;
    }

    /// <summary>Доля выборки, которая сейчас резидентна (0..1), или -1 при ошибке.</summary>
    public double ResidentFraction(IntPtr h)
    {
        var q = (UIntPtr[])_pairs.Clone();
        for (int i = 1; i < q.Length; i += 2) q[i] = UIntPtr.Zero;
        if (!Native.K32QueryWorkingSetEx(h, q, (uint)(q.Length * IntPtr.Size))) return -1;
        int valid = 0;
        for (int i = 1; i < q.Length; i += 2) if (((ulong)q[i] & 1) != 0) valid++;
        return Count == 0 ? -1 : (double)valid / Count;
    }
}
