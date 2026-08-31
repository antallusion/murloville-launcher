using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace MurloLauncher;

/// <summary>
/// Лаунчер MurloVille: ставит клиент с нуля и догоняет его до текущего
/// состояния при каждом запуске.
///
/// Файлы приезжают из двух мест, и это не случайность. Шестнадцать гигабайт
/// базовых MPQ — стоковые файлы Blizzard, они не меняются никогда и лежат на
/// Яндекс.Диске: там есть докачка и приличная скорость, а главное — этот
/// трафик не идёт через игровой сервер. Одна установка равна пяти дням всего
/// его исходящего трафика, и раздавать такое со своего канала значит лагать
/// всем, кто в это время играет. Наши патчи и аддоны, вместе пять мегабайт,
/// приезжают с игрового сервера: они меняются часто.
///
/// Откуда что брать — написано в манифесте, а не в коде: хранилище можно
/// переносить, не пересобирая лаунчер.
/// </summary>
public partial class MainWindow : Window
{
    private const string Base = "https://play.murloville.ru/client";
    private const string YandexApi = "https://cloud-api.yandex.net/v1/disk/public/resources/download";
    private const string Realm = "play.murloville.ru";

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        // Гигабайтные файлы: таймаут на весь запрос тут только вредит.
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    private string? _root;
    private Manifest? _manifest;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await Startup();
    }

    // --- окно ----------------------------------------------------------------

    private void Window_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Say(string status, string? detail = null)
    {
        StatusText.Text = status;
        if (detail is not null) DetailText.Text = detail;
    }

    // --- модель манифеста ----------------------------------------------------

    private sealed record Entry(string path, long size, string sha256, string src, string? remote);

    private sealed record Manifest(
        string? launcherVersion, string? publicKey, long totalBytes, List<Entry>? files);

    // --- запуск --------------------------------------------------------------

    private async Task Startup()
    {
        _ = ShowOnline();
        _ = LoadArt();

        Say("Проверяю обновления…");
        try
        {
            var json = await Http.GetStringAsync($"{Base}/manifest.json");
            _manifest = JsonSerializer.Deserialize<Manifest>(json);
        }
        catch (Exception ex)
        {
            _manifest = null;
            Say("Сервер обновлений не отвечает.", Short(ex.Message));
        }

        _root = FindClient();

        if (_root is null)
        {
            var gb = (_manifest?.totalBytes ?? 0) / 1073741824.0;
            Say("Игра не найдена — нужна установка.",
                gb > 0 ? $"Скачать предстоит {gb:0.#} ГБ. Место можно выбрать любое."
                       : "Укажи, куда ставить.");
            PlayBtn.Content = "УСТАНОВИТЬ";
            PlayBtn.IsEnabled = true;
            return;
        }

        if (_manifest is null)
        {
            Say("Сервер обновлений не отвечает — играть можно.", "Клиент на месте.");
            Bar.Value = 100;
            PlayBtn.IsEnabled = true;
            return;
        }

        await Sync();
    }

    /// <summary>Ищем клиент рядом с собой, на уровень выше и там, где ставили прошлый раз.</summary>
    private static string? FindClient()
    {
        var here = AppContext.BaseDirectory;
        foreach (var dir in new[] { here, Path.GetDirectoryName(here.TrimEnd('\\')), SavedRoot() })
        {
            if (dir is not null && File.Exists(Path.Combine(dir, "Wow.exe")))
                return dir;
        }
        return null;
    }

    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "murlo-launcher.txt");

    private static string? SavedRoot()
    {
        try { return File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath).Trim() : null; }
        catch { return null; }
    }

    private static void RememberRoot(string root)
    {
        try { File.WriteAllText(ConfigPath, root); } catch { }
    }

    // --- установка и обновление одной дорогой --------------------------------

    /// <summary>
    /// Установка и обновление — одно и то же: сверяем список и качаем
    /// недостающее. Разница лишь в том, что при установке недостающее — это всё.
    /// </summary>
    private async Task Sync()
    {
        if (_manifest?.files is null || _root is null) return;

        PlayBtn.IsEnabled = false;

        Say("Сверяю файлы…");
        var todo = new List<Entry>();
        long todoBytes = 0, haveBytes = 0;

        for (var i = 0; i < _manifest.files.Count; i++)
        {
            var f = _manifest.files[i];
            Bar.Value = (double)i / _manifest.files.Count * 100;
            DetailText.Text = f.path;

            if (NeedsDownload(Local(f), f))
            {
                todo.Add(f);
                todoBytes += f.size;
            }
            else
            {
                haveBytes += f.size;
            }
            await Task.Yield();
        }

        if (todo.Count == 0)
        {
            Say("Клиент обновлён.", "Всё на месте.");
            Bar.Value = 100;
            PlayBtn.Content = "ИГРАТЬ";
            PlayBtn.IsEnabled = true;
            return;
        }

        // Место проверяем до начала, а не на двенадцатом гигабайте.
        if (!EnoughSpace(todoBytes, out var freeGb))
        {
            Say("Не хватает места на диске.",
                $"Нужно {Gb(todoBytes)}, свободно {freeGb:0.#} ГБ.");
            Bar.Value = 0;
            return;
        }

        var big = todoBytes > 1073741824;   // больше гигабайта — это установка
        Say(big ? $"Устанавливаю игру: {Gb(todoBytes)}" : $"Качаю обновление: {Gb(todoBytes)}",
            big ? "Первый раз это долго. Можно свернуть окно." : null);
        Bar.Value = 0;

        long done = 0;
        var started = DateTime.UtcNow;

        foreach (var f in todo)
        {
            try
            {
                await Download(f, done, todoBytes, started);
            }
            catch (Exception ex)
            {
                Say("Загрузка прервалась. Запусти лаунчер снова — продолжит с этого места.",
                    $"{f.path}: {Short(ex.Message)}");
                return;
            }
            done += f.size;
            Bar.Value = (double)done / todoBytes * 100;
        }

        Say(big ? "Игра установлена." : "Обновление установлено.",
            $"Файлов: {todo.Count}, {Gb(todoBytes)}");
        Bar.Value = 100;
        PlayBtn.Content = "ИГРАТЬ";
        PlayBtn.IsEnabled = true;
    }

    private string Local(Entry f) => Path.Combine(_root!, f.path.Replace('/', '\\'));

    private bool EnoughSpace(long need, out double freeGb)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(_root!)!);
            freeGb = drive.AvailableFreeSpace / 1073741824.0;
            return drive.AvailableFreeSpace > need + 536870912;   // полгигабайта запаса
        }
        catch { freeGb = 0; return true; }   // не смогли узнать — не мешаем
    }

    private static bool NeedsDownload(string local, Entry f)
    {
        var info = new FileInfo(local);
        if (!info.Exists || info.Length != f.size) return true;
        if (string.IsNullOrEmpty(f.sha256)) return false;   // хеша нет — верим размеру

        try
        {
            using var stream = File.OpenRead(local);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return !hash.Equals(f.sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    /// <summary>Адрес файла. Для Диска ссылку приходится просить каждый раз: она временная.</summary>
    private async Task<string> ResolveUrl(Entry f)
    {
        if (f.src != "yandex")
            return $"{Base}/files/{f.path}";

        var url = $"{YandexApi}?public_key={Uri.EscapeDataString(_manifest!.publicKey!)}" +
                  $"&path={Uri.EscapeDataString(f.remote ?? "/" + f.path)}";
        var json = await Http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("href").GetString()
               ?? throw new IOException("хранилище не дало ссылку");
    }

    /// <summary>
    /// Качаем в файл .part и дописываем с места обрыва. На шестнадцати
    /// гигабайтах разрыв связи — не исключение, а норма, и начинать заново
    /// было бы издевательством.
    /// </summary>
    private async Task Download(Entry f, long doneBefore, long totalBytes, DateTime started)
    {
        var local = Local(f);
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        var part = local + ".part";

        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > f.size) { File.Delete(part); have = 0; }

        if (have < f.size)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, await ResolveUrl(f));
            if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            // Хранилище может не поддержать докачку — тогда начинаем сначала.
            if (have > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
            {
                File.Delete(part);
                have = 0;
            }
            resp.EnsureSuccessStatusCode();

            await using var net = await resp.Content.ReadAsStreamAsync();
            await using var file = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create,
                                                  FileAccess.Write, FileShare.None, 1 << 20);

            var buf = new byte[1 << 20];
            int read;
            var lastShown = DateTime.UtcNow;
            while ((read = await net.ReadAsync(buf)) > 0)
            {
                await file.WriteAsync(buf.AsMemory(0, read));
                have += read;

                if ((DateTime.UtcNow - lastShown).TotalMilliseconds > 250)
                {
                    lastShown = DateTime.UtcNow;
                    var doneNow = doneBefore + have;
                    Bar.Value = (double)doneNow / totalBytes * 100;
                    var secs = (DateTime.UtcNow - started).TotalSeconds;
                    var speed = secs > 1 ? doneNow / secs : 0;
                    DetailText.Text = speed > 0
                        ? $"{f.path} — {Gb(doneNow)} из {Gb(totalBytes)}, {speed / 1048576:0.#} МБ/с, осталось {Remaining(totalBytes - doneNow, speed)}"
                        : f.path;
                }
            }
        }

        if (!string.IsNullOrEmpty(f.sha256))
        {
            DetailText.Text = $"{f.path} — проверяю";
            await using (var s = File.OpenRead(part))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(s)).ToLowerInvariant();
                if (!hash.Equals(f.sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(part);
                    throw new IOException("файл скачался повреждённым");
                }
            }
        }

        File.Move(part, local, overwrite: true);
    }

    // --- кнопка --------------------------------------------------------------

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_root is null)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Куда установить игру",
            };
            if (dlg.ShowDialog() != true) return;

            var target = dlg.FolderName;
            // В непустую чужую папку не льём: перепутать с «Загрузками» легко.
            if (Directory.Exists(target) && Directory.GetFileSystemEntries(target).Length > 0
                && !File.Exists(Path.Combine(target, "Wow.exe")))
            {
                var ok = MessageBox.Show(
                    "Папка не пустая, и игры в ней нет.\nВсё равно ставить сюда?",
                    "MurloVille", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (ok != MessageBoxResult.Yes) return;
            }

            _root = target;
            RememberRoot(_root);
            await Sync();
            return;
        }

        FixRealmlist();
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(_root, "Wow.exe"))
            {
                WorkingDirectory = _root,
                UseShellExecute = true,
            });
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не смог запустить игру:\n" + ex.Message, "MurloVille",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Адрес сервера прописываем сами: забытый realmlist — половина обращений в поддержку.</summary>
    private void FixRealmlist()
    {
        foreach (var rel in new[] { @"Data\ruRU\realmlist.wtf", @"Data\enUS\realmlist.wtf", "realmlist.wtf" })
        {
            var path = Path.Combine(_root!, rel);
            var dir = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(dir)) continue;
            try
            {
                if (!File.Exists(path) ||
                    !File.ReadAllText(path).Contains(Realm, StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllText(path, $"set realmlist {Realm}" + Environment.NewLine, Encoding.ASCII);
                }
            }
            catch { }
        }
    }

    // --- мелочи --------------------------------------------------------------

    /// <summary>
    /// Оформление приезжает с сервера, внутри программы его нет: исходники
    /// лаунчера открыты, а чужой графике в открытом репозитории не место.
    /// Не загрузилось — окно останется тёмным, и это никому не мешает.
    /// </summary>
    private async Task LoadArt()
    {
        await SetImage(BgImage, $"{Base}/bg.jpg");
        await SetImage(MarkImage, $"{Base}/mark.png");
    }

    private static async Task SetImage(System.Windows.Controls.Image target, string url)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            var img = new BitmapImage();
            img.BeginInit();
            img.StreamSource = new MemoryStream(bytes);
            // Читаем сразу и замораживаем: иначе поток закроется раньше, чем
            // картинку нарисуют, и она останется пустой.
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            target.Source = img;
        }
        catch
        {
            // Нет картинки — не беда, фон и так тёмный.
        }
    }

    private async Task ShowOnline()
    {
        try
        {
            var json = await Http.GetStringAsync("https://play.murloville.ru/api/status");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("online", out var online))
                OnlineText.Text = $"Сейчас в игре: {online.GetInt32()}";
        }
        catch { }
    }

    private static string Gb(long bytes) => bytes >= 1073741824
        ? $"{bytes / 1073741824.0:0.##} ГБ"
        : $"{bytes / 1048576.0:0.#} МБ";

    private static string Remaining(long bytes, double speed)
    {
        if (speed <= 0) return "?";
        var s = TimeSpan.FromSeconds(bytes / speed);
        return s.TotalHours >= 1 ? $"{(int)s.TotalHours} ч {s.Minutes} мин" : $"{s.Minutes} мин";
    }

    private static string Short(string s) => s.Length > 120 ? s[..120] + "…" : s;
}
