using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32;

namespace MurloLauncher;

/// <summary>
/// Поиск установленной игры.
///
/// Ищем в три захода, от дешёвого к дорогому. Сначала места, где игра почти
/// наверняка и лежит: рядом с лаунчером, там же, где в прошлый раз, в реестре
/// Blizzard. Потом обычные места вроде C:\World of Warcraft. И только если не
/// нашли — обходим диски целиком.
///
/// Обход ограничен четырьмя уровнями вложенности намеренно. Игру ставят
/// неглубоко: C:\wow, D:\Games\WoW, C:\Program Files\World of Warcraft. Лезть
/// глубже — это минуты работы диска ради случая, которого почти не бывает, а
/// человек в это время смотрит на застывшее окно.
/// </summary>
internal static class ClientFinder
{
    /// <summary>Папка считается игрой, если в ней лежит Wow.exe — и это не современный WoW.</summary>
    public static bool IsClient(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "Wow.exe")) && !IsModernClient(dir);

    /// <summary>
    /// Современный World of Warcraft, а не 3.3.5a. У него тоже есть Wow.exe, и
    /// установщик Battle.net пишет путь к нему в тот же ключ реестра, что читает
    /// быстрый поиск. Принять его за нашу игру значит налить восемнадцать
    /// гигабайт поверх чужой установки и сломать её. Узнаём по .build.info
    /// рядом (так раскладывает файлы Battle.net) и по версии самого Wow.exe.
    /// </summary>
    public static bool IsModernClient(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        try
        {
            if (File.Exists(Path.Combine(dir, ".build.info"))) return true;
            var exe = Path.Combine(dir, "Wow.exe");
            return File.Exists(exe) && FileVersionInfo.GetVersionInfo(exe).FileMajorPart >= 4;
        }
        catch { return false; }
    }

    /// <summary>Быстрый поиск по вероятным местам. Мгновенный.</summary>
    public static string? Quick(string? savedRoot)
    {
        foreach (var dir in QuickCandidates(savedRoot))
            if (IsClient(dir))
                return dir;
        return null;
    }

    private static IEnumerable<string?> QuickCandidates(string? savedRoot)
    {
        yield return savedRoot;

        // Рядом с собой и на уровень выше: так лежит лаунчер, положенный прямо
        // в папку игры или в подпапку рядом с ней.
        var here = AppContext.BaseDirectory;
        yield return here;
        yield return Path.GetDirectoryName(here.TrimEnd('\\'));

        // Реестр Blizzard. Ключ остаётся от официального установщика, и у тех,
        // кто когда-то ставил игру нормально, он есть.
        foreach (var key in new[]
                 {
                     @"SOFTWARE\Blizzard Entertainment\World of Warcraft",
                     @"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft",
                 })
        {
            yield return ReadRegistry(RegistryHive.LocalMachine, key, "InstallPath");
            yield return ReadRegistry(RegistryHive.CurrentUser, key, "InstallPath");
        }

        // Обычные места на каждом диске. Имена с «3.3.5» здесь не для красоты:
        // так подписывают папку почти все, кто скачивал сборку с торрента или
        // с чужого сервера, а это и есть главный наш случай — игра уже есть.
        string[] names =
        {
            "wow", "WoW", "World of Warcraft",
            "WoW 3.3.5", "WoW 3.3.5a", "wow 3.3.5a",
            "World of Warcraft 3.3.5", "World of Warcraft 3.3.5a",
            "WoW_3.3.5a", "WotLK", "Wrath of the Lich King",
        };

        foreach (var drive in FixedDrives())
        {
            var root = drive.RootDirectory.FullName;
            foreach (var name in names)
            {
                yield return Path.Combine(root, name);
                yield return Path.Combine(root, "Games", name);
                yield return Path.Combine(root, "Игры", name);
            }
            yield return Path.Combine(root, "Program Files", "World of Warcraft");
            yield return Path.Combine(root, "Program Files (x86)", "World of Warcraft");
        }

        // Папки пользователя: рабочий стол и загрузки — куда чаще всего и
        // распаковывают скачанный архив с игрой.
        foreach (var special in new[]
                 {
                     Environment.SpecialFolder.DesktopDirectory,
                     Environment.SpecialFolder.UserProfile,
                 })
        {
            string dir;
            try { dir = Environment.GetFolderPath(special); }
            catch { continue; }
            if (string.IsNullOrEmpty(dir)) continue;
            foreach (var name in names) yield return Path.Combine(dir, name);
            yield return Path.Combine(dir, "Downloads", "World of Warcraft");
            yield return Path.Combine(dir, "Downloads", "WoW 3.3.5a");
        }
    }

    private static string? ReadRegistry(RegistryHive hive, string path, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch { return null; }
    }

    private static IEnumerable<DriveInfo> FixedDrives()
    {
        DriveInfo[] all;
        try { all = DriveInfo.GetDrives(); }
        catch { yield break; }

        foreach (var d in all)
        {
            bool ok;
            try { ok = d.DriveType == DriveType.Fixed && d.IsReady; }
            catch { ok = false; }
            if (ok) yield return d;
        }
    }

    /// <summary>
    /// Полный обход дисков. Долгий, поэтому его запускают отдельной кнопкой и
    /// в стороне от потока окна, показывая, где сейчас смотрим.
    ///
    /// Собираем ВСЕ найденные копии, а не первую попавшуюся. Первая — это
    /// просто та, что раньше по алфавиту: на машине с двумя клиентами поиск
    /// молча подсунул бы не тот, и лаунчер полез бы качать шестнадцать
    /// гигабайт в чужую папку. Выбор оставляем человеку.
    ///
    /// Впереди списка идут копии с нашим патчем: если игрок уже играл у нас,
    /// почти наверняка нужна именно она.
    /// </summary>
    public static List<string> DeepScan(IProgress<string> where, CancellationToken ct, int limit = 8)
    {
        var found = new List<string>();

        foreach (var drive in FixedDrives())
        {
            Walk(drive.RootDirectory.FullName, 0, where, ct, found, limit);
            if (found.Count >= limit) break;
        }

        found.Sort((a, b) =>
        {
            var byPatch = HasOurPatch(b).CompareTo(HasOurPatch(a));
            return byPatch != 0 ? byPatch : a.Length.CompareTo(b.Length);
        });
        return found;
    }

    /// <summary>Наш ли это клиент: в нём лежит патч MurloVille.</summary>
    public static bool HasOurPatch(string dir)
    {
        try { return File.Exists(Path.Combine(dir, "Data", "ruRU", "patch-ruRU-9.MPQ")); }
        catch { return false; }
    }

    // Сюда лезть незачем: системные папки, корзина, служебные хранилища.
    // Игры там не бывает, а времени они съедают много.
    private static readonly string[] Skip =
    {
        "windows", "$recycle.bin", "system volume information", "recovery",
        "programdata", "appdata", "node_modules", "$windows.~bt", "$windows.~ws",
        "perflogs", "msocache", "config.msi",
    };

    private const int MaxDepth = 4;

    private static void Walk(string dir, int depth, IProgress<string> where, CancellationToken ct,
                             List<string> found, int limit)
    {
        ct.ThrowIfCancellationRequested();
        if (found.Count >= limit) return;

        // Внутрь самой игры не лезем: там сотни папок и второго клиента быть
        // не может.
        if (IsClient(dir)) { found.Add(dir); return; }
        if (depth >= MaxDepth) return;

        string[] subs;
        try { subs = Directory.GetDirectories(dir); }
        catch { return; }   // нет доступа — просто идём дальше

        foreach (var sub in subs)
        {
            ct.ThrowIfCancellationRequested();
            if (found.Count >= limit) return;

            var name = Path.GetFileName(sub).ToLowerInvariant();
            if (Array.IndexOf(Skip, name) >= 0) continue;
            if (name.StartsWith('.')) continue;

            // Показываем только верхние уровни: иначе строка мельтешит так,
            // что прочитать её всё равно нельзя.
            if (depth <= 1) where.Report(sub);

            Walk(sub, depth + 1, where, ct, found, limit);
        }
    }
}
