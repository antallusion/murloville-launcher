using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MurloLauncher;

/// <summary>
/// Что мы знаем о папке с игрой до того, как начали качать.
///
/// Два вопроса решаются здесь, и оба до сих пор стоили людям нервов.
///
/// Первый: игра у человека уже есть. Тогда шестнадцать гигабайт стоковых
/// архивов Blizzard качать незачем — надо положить сверху только наше. Раньше
/// лаунчер этого не различал: у чужой сборки базовые MPQ отличаются от наших
/// по отпечатку, сверка честно решала, что всё надо перекачать, и человек
/// видел «обновление на 17 ГБ» вместо пятнадцати мегабайт. Отсюда и жалобы,
/// что существующий клиент обновить нельзя.
///
/// Второй: в папку может быть просто нельзя писать — игра стоит в
/// Program Files, и без прав администратора замена файла кончается отказом.
/// Узнать об этом надо до загрузки, а не на первом же файле после получаса.
///
/// Третий (с 1.5.0): игра в папке может оказаться клиентом другого сервера.
/// «Докладываем только своё» предполагало, что основа под нашими патчами
/// стоковая, — а она наша, и чужая основа с нашими таблицами даёт невидимых
/// персонажей. Поэтому базовые патчи сверяем, а посторонние архивы находим.
/// </summary>
internal static class ClientState
{
    /// <summary>
    /// Наши файлы в клиенте — запасное правило на случай старого манифеста,
    /// где списка ours ещё нет.
    ///
    /// Границу надо знать точно. У Blizzard в «тройке» свои патчи с номерами
    /// до третьего включительно (patch-2.MPQ, patch-ruRU-3.MPQ и такие же),
    /// и трогать их нельзя: припиши их к своим — и человек с готовой игрой
    /// получит два гигабайта лишней загрузки. Наши начинаются с четвёртого и
    /// уходят в буквы: patch-ruRU-4, -5, -6, -8, -9, -A.
    /// </summary>
    private static readonly Regex OursRule = new(
        @"^(Wow\.exe|Interface/AddOns/Murlo|Data/(ruRU|enUS)/patch-(ruRU|enUS)-[4-9A-Za-z]\.MPQ)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Файл из манифеста — наш или стоковый?</summary>
    public static bool IsOurs(string path, IEnumerable<string>? prefixes = null)
    {
        var p = path.Replace('\\', '/');
        var list = prefixes as IReadOnlyCollection<string> ?? prefixes?.ToList();
        if (list is { Count: > 0 })
            return list.Any(x => p.StartsWith(x, StringComparison.OrdinalIgnoreCase));
        return OursRule.IsMatch(p);
    }

    /// <summary>
    /// Базовые патчи в корне Data: patch.MPQ, patch-2.MPQ, patch-3.MPQ.
    ///
    /// По имени они стоковые, а по сути нет: наш patch.MPQ — это HD-сборка с
    /// моделями всех рас на четыре гигабайта, и наши таблицы рас в
    /// patch-ruRU-A собраны ровно под неё. Поставь наши таблицы поверх чужого
    /// patch.MPQ — и персонажи станут невидимыми, а экран выбора тёмным
    /// (форум t/64, 27.09.2026). Поэтому в готовой игре их сверяем всегда,
    /// даже когда остальные стоковые архивы не трогаем.
    /// </summary>
    private static readonly Regex BasePatchRule = new(
        @"^Data/patch(-[^/.]+)?\.MPQ$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsBasePatch(string path) => BasePatchRule.IsMatch(path.Replace('\\', '/'));

    /// <summary>Архив в папке игры, которого нет в нашем клиенте.</summary>
    public sealed record Extra(string Path, long Size);

    /// <summary>
    /// Стоковые архивы, которых нет в манифесте, но которые ничему не мешают:
    /// игра их не читает. backup-ruRU.MPQ лежит у всех, кто ставил до
    /// 05.09.2026, — назвать его чужим значит напугать своих же игроков.
    /// </summary>
    private static readonly string[] Harmless = { "Data/ruRU/backup-ruRU.MPQ" };

    /// <summary>
    /// Архивы в Data и Data\ruRU, которых нет в нашем клиенте: патчи других
    /// серверов (patch-4.MPQ, patch-ruRU-Z.MPQ и подобные). Игра читает их
    /// вместе с нашими, и чужие модели или таблицы перебивают наши. Только
    /// список каталога, без чтения файлов — мгновенно.
    /// </summary>
    public static List<Extra> ForeignArchives(string root, IEnumerable<string> known)
    {
        var set = new HashSet<string>(known.Select(p => p.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
        foreach (var h in Harmless) set.Add(h);

        var found = new List<Extra>();
        foreach (var rel in new[] { "Data", "Data/ruRU" })
        {
            var dir = Path.Combine(root, rel.Replace('/', '\\'));
            List<string> files;
            try
            {
                if (!Directory.Exists(dir)) continue;
                files = Directory.EnumerateFiles(dir).ToList();
            }
            catch { continue; }

            foreach (var full in files)
            {
                var name = Path.GetFileName(full);
                // Только настоящие архивы: patch-ruRU-9.MPQ.bak и прочие
                // хвосты игра не читает, и чужими они не считаются.
                if (!string.Equals(Path.GetExtension(name), ".MPQ", StringComparison.OrdinalIgnoreCase)) continue;
                var p = rel + "/" + name;
                if (set.Contains(p)) continue;
                long size = 0;
                try { size = new FileInfo(full).Length; } catch { }
                found.Add(new Extra(p, size));
            }
        }
        return found;
    }

    /// <summary>
    /// Куда откладываем чужое. Папка внутри игры, но вне Data: игра читает
    /// архивы только из Data, а человеку не надо искать свои файлы по диску.
    /// </summary>
    public const string AsideFolder = "Отложено MurloVille";

    /// <summary>
    /// Убрать файл из игры, не удаляя: перенести в «Отложено MurloVille» с
    /// тем же путём внутри. На том же диске это переименование — мгновенно
    /// даже для четырёх гигабайт. Удалять чужое мы не вправе: человек может
    /// захотеть вернуться на прежний сервер.
    /// </summary>
    public static void MoveAside(string root, string rel, string why)
    {
        var src = Path.Combine(root, rel.Replace('/', '\\'));
        if (!File.Exists(src)) return;

        var dst = Path.Combine(root, AsideFolder, rel.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        if (File.Exists(dst)) dst += "." + DateTime.Now.ToString("yyyyMMdd-HHmmss");

        var attrs = File.GetAttributes(src);
        if (attrs.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(src, attrs & ~FileAttributes.ReadOnly);
        File.Move(src, dst);

        NoteAside(root, $"{DateTime.Now:dd.MM.yyyy HH:mm}  {Path.GetRelativePath(Path.Combine(root, AsideFolder), dst)}  — {why}");
    }

    /// <summary>Записка в отложенной папке: что это, откуда и как вернуть.</summary>
    private static void NoteAside(string root, string line)
    {
        try
        {
            var note = Path.Combine(root, AsideFolder, "Что это.txt");
            if (!File.Exists(note))
            {
                File.WriteAllText(note,
                    "Эти файлы убрал из папки игры лаунчер MurloVille. Ничего не удалено." + Environment.NewLine +
                    Environment.NewLine +
                    "В клиенте MurloVille их нет, а поверх наших патчей они ломают персонажей: " +
                    "расы становятся невидимыми, экран выбора — тёмным. Отсюда игра их не читает." + Environment.NewLine +
                    Environment.NewLine +
                    "Вернуть файл — перенести его обратно в папку игры по тому же пути " +
                    "(Data\\patch-4.MPQ отсюда → Data\\patch-4.MPQ в игре). Играть на MurloVille " +
                    "с ним уже не получится. Не нужны — удалите эту папку целиком." + Environment.NewLine +
                    Environment.NewLine,
                    new System.Text.UTF8Encoding(true));
            }
            File.AppendAllText(note, line + Environment.NewLine, new System.Text.UTF8Encoding(true));
        }
        catch { }
    }

    /// <summary>
    /// Похоже ли, что в папке уже стоит рабочая «тройка». Опорных архивов
    /// достаточно двух: без них игра всё равно не запустится, а перебирать
    /// весь список — лишняя работа диска.
    /// </summary>
    public static bool HasBaseGame(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        var data = Path.Combine(root, "Data");
        if (!File.Exists(Path.Combine(root, "Wow.exe")) || !Directory.Exists(data)) return false;

        foreach (var name in new[] { "common.MPQ", "lichking.MPQ" })
        {
            var f = new FileInfo(Path.Combine(data, name));
            if (!f.Exists || f.Length < 100L * 1024 * 1024) return false;
        }
        return true;
    }

    /// <summary>Можно ли писать в папку игры обычными правами.</summary>
    public static bool CanWrite(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        foreach (var dir in new[] { root, Path.Combine(root, "Data") })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                var probe = Path.Combine(dir, ".murlo-write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch { return false; }
        }
        return true;
    }
}
