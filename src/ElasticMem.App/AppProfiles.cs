using System.Diagnostics;

namespace ElasticMem.App;

public sealed record AppProfile(string Title, string[] ProcessNames, string[] NameContains, string Note, bool AntiCheatRisk = false)
{
    public override string ToString() => Title;

    public bool Matches(string processName)
    {
        foreach (var n in ProcessNames) if (string.Equals(processName, n, StringComparison.OrdinalIgnoreCase)) return true;
        string low = processName.ToLowerInvariant();
        foreach (var c in NameContains)
        {
            var parts = c.ToLowerInvariant().Split('+');
            if (parts.All(p => low.Contains(p))) return true;
        }
        return false;
    }

    public List<Process> Find()
    {
        var res = new List<Process>();
        foreach (var p in Process.GetProcesses())
        {
            if (Matches(p.ProcessName)) res.Add(p); else p.Dispose();
        }
        return res;
    }

    /// <summary>Известные игры (для игрового режима и защиты от сжатия), кроме встроенных профилей.</summary>
    public static readonly HashSet<string> KnownGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dota2", "cs2", "csgo", "hl2", "r5apex", "FortniteClient-Win64-Shipping", "VALORANT-Win64-Shipping", "League of Legends",
        "GTA5", "RDR2", "eldenring", "witcher3", "Cyberpunk2077", "RustClient", "PUBG", "TslGame", "Overwatch", "destiny2", "bf2042",
        "Minecraft.Windows", "RobloxPlayerBeta", "GenshinImpact", "StarRail", "wot", "WorldOfTanks", "WorldOfWarships",
    };

    public static readonly AppProfile[] Builtin =
    {
        new("Dota 2 (Source 2)", new[] { "dota2" }, Array.Empty<string>(),
            "Игровой движок Source 2. Valve Anti-Cheat: чтение памяти игры внешней программой — то же действие, что делают читы. " +
            "Риск бана низкий, но не нулевой: анализируйте только в главном меню / офлайн-режиме, не в матчмейкинге.", AntiCheatRisk: true),
        new("Steam", new[] { "steam", "steamwebhelper", "steamservice" }, Array.Empty<string>(),
            "Клиент Steam + встроенный Chromium (steamwebhelper) — основная память обычно в нём."),
        new("Яндекс Музыка", new[] { "Яндекс Музыка", "Yandex Music", "YandexMusic", "yandexmusic" }, new[] { "yandex+music", "яндекс+музык" },
            "Приложение на Electron (Chromium + Node.js) — несколько процессов."),
        new("Telegram Desktop", new[] { "Telegram", "AyuGram", "Kotatogram" }, Array.Empty<string>(),
            "Нативное Qt-приложение; кеш медиа в памяти."),
        new("Google Chrome / Chromium", new[] { "chrome", "chromium", "msedge" }, Array.Empty<string>(),
            "Многопроцессный браузер: процесс на вкладку, GPU-процесс, сервисы."),
    };
}
