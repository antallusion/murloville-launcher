using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
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

    /// <summary>Что произойдёт по нажатию главной кнопки.</summary>
    private enum Mode { Play, Install, Retry }

    private readonly bool _autostart;
    private string? _root;
    private Manifest? _manifest;
    private Mode _mode = Mode.Play;
    private bool _busy;
    private CancellationTokenSource? _scan;

    public MainWindow(bool autostart = false)
    {
        _autostart = autostart;
        InitializeComponent();

        // При запуске вместе с Windows не лезем на глаза: догоняем обновления
        // свёрнутыми, развернуть можно из панели задач.
        if (autostart) WindowState = WindowState.Minimized;

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
        string? launcherVersion, string? launcherSha256, string? launcherUrl,
        string? publicKey, long totalBytes, List<Entry>? files);

    // --- запуск --------------------------------------------------------------

    private async Task Startup()
    {
        _ = ShowOnline();
        _ = LoadArt();

        AutoStartBox.IsChecked = Setup.AutoStart;
        ShowSetupState();

        // Хвост от прошлого самообновления: старый файл нельзя было удалить,
        // пока он работал. Теперь работаем мы — убираем.
        TryDelete((Environment.ProcessPath ?? "") + ".old");

        Say("Проверяю обновления…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var json = await Http.GetStringAsync($"{Base}/manifest.json", cts.Token);
            _manifest = JsonSerializer.Deserialize<Manifest>(json);
        }
        catch (Exception ex)
        {
            _manifest = null;
            Say("Сервер обновлений не отвечает.", Short(ex.Message));
        }

        // Сначала обновляем себя: новая версия может уметь то, чего не умеет
        // эта, а игра подождёт минуту. Если перезапустились — дальше не идём.
        if (await SelfUpdate()) return;

        _root = ClientFinder.Quick(SavedRoot());
        if (_root is not null) RememberRoot(_root);
        ShowPath();

        // Предложение поставить лаунчер задаём один раз и не при автозапуске:
        // спрашивать о таком в момент включения компьютера — дурной тон.
        if (!_autostart) OfferInstall();

        if (_root is null)
        {
            var gb = (_manifest?.totalBytes ?? 0) / 1073741824.0;
            Say("Игра не найдена — нужна установка.",
                gb > 0 ? $"Скачать предстоит {gb:0.#} ГБ. Место можно выбрать любое."
                       : "Укажи, куда ставить.");
            _mode = Mode.Install;
            PlayBtn.Content = "УСТАНОВИТЬ";
            PlayBtn.IsEnabled = true;
            return;
        }

        if (_manifest is null)
        {
            Say("Сервер обновлений не отвечает — играть можно.", "Клиент на месте.");
            Bar.Value = 100;
            _mode = Mode.Play;
            PlayBtn.IsEnabled = true;
            return;
        }

        await Sync();
    }

    // --- самообновление ------------------------------------------------------

    /// <summary>
    /// Лаунчер обновляет сам себя: манифест несёт номер свежей версии и
    /// отпечаток файла. Скачиваем рядом, сверяем отпечаток, переименовываем
    /// работающий файл в .old (Windows это разрешает, а перезаписать — нет),
    /// ставим новый на его место и перезапускаемся с теми же ключами.
    /// Любая осечка — остаёмся на старой версии и работаем дальше: обновление
    /// лаунчера не повод оставить игрока без игры.
    /// </summary>
    private async Task<bool> SelfUpdate()
    {
        if (_manifest?.launcherVersion is null || string.IsNullOrEmpty(_manifest.launcherSha256)) return false;
        if (!Version.TryParse(_manifest.launcherVersion, out var latest)) return false;

        var mine = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        if (Trim(latest) <= Trim(mine)) return false;

        var exe = Environment.ProcessPath;
        if (exe is null) return false;
        var fresh = exe + ".new";
        var old = exe + ".old";

        try
        {
            Say($"Обновляю лаунчер до {Trim(latest)}…", "Секунда, и перезапущусь сам.");
            var url = string.IsNullOrEmpty(_manifest.launcherUrl) ? $"{Base}/MurloVille.exe" : _manifest.launcherUrl;
            await Fetch(url, fresh);

            await using (var s = File.OpenRead(fresh))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(s)).ToLowerInvariant();
                if (!hash.Equals(_manifest.launcherSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("новый лаунчер скачался повреждённым");
            }

            TryDelete(old);
            File.Move(exe, old);
            try
            {
                File.Move(fresh, exe);
            }
            catch
            {
                File.Move(old, exe);   // вернуть как было, иначе останемся без программы
                throw;
            }

            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Arguments = _autostart ? "--autostart" : "",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            });
            Application.Current.Shutdown();
            return true;
        }
        catch (Exception ex)
        {
            TryDelete(fresh);
            Say("Лаунчер не обновился — работаю на прежней версии.", Short(ex.Message));
            return false;
        }
    }

    /// <summary>Три числа версии: у сборки их четыре, в манифесте три.</summary>
    private static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>Простая загрузка целиком в файл, с защитой от застывшего потока.</summary>
    private async Task Fetch(string url, string path)
    {
        using var headCts = new CancellationTokenSource(TimeSpan.FromSeconds(StallSeconds));
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headCts.Token);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;

        await using var net = await resp.Content.ReadAsStreamAsync(headCts.Token);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var buf = new byte[1 << 20];
        long have = 0;
        while (true)
        {
            var read = await ReadOrStall(net, buf);
            if (read <= 0) break;
            await file.WriteAsync(buf.AsMemory(0, read));
            have += read;
            if (total > 0) Bar.Value = (double)have / total * 100;
        }
    }

    // --- установка самого лаунчера -------------------------------------------

    private string AskedFlag => Path.Combine(Setup.DataDir, "install-declined");

    /// <summary>
    /// Предлагаем поставить лаунчер на компьютер. Один раз: отказ запоминаем,
    /// потому что программа, спрашивающая одно и то же при каждом запуске, —
    /// это назойливость, а не забота.
    /// </summary>
    private void OfferInstall()
    {
        if (Setup.IsInstalled || Setup.RunningFromInstall) return;
        if (File.Exists(AskedFlag)) return;

        var answer = MessageBox.Show(
            "Установить лаунчер на компьютер?" + Environment.NewLine + Environment.NewLine +
            "Появятся ярлыки на рабочем столе и в меню «Пуск», а сам лаунчер будет " +
            "запускаться вместе с Windows и держать игру обновлённой. " +
            "Автозапуск потом отключается галочкой в окне." + Environment.NewLine + Environment.NewLine +
            "Права администратора не нужны: ставим в вашу папку пользователя, " +
            "удаление — через «Установленные приложения».",
            "MurloVille", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            try
            {
                Directory.CreateDirectory(Setup.DataDir);
                File.WriteAllText(AskedFlag, "");
            }
            catch { }
            ShowSetupState();
            return;
        }

        try
        {
            Setup.Install();
            Setup.AutoStart = true;
            AutoStartBox.IsChecked = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не смог установить лаунчер:" + Environment.NewLine + ex.Message,
                "MurloVille", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        ShowSetupState();
    }

    private void ShowSetupState()
    {
        SetupText.Text = Setup.IsInstalled
            ? $"Лаунчер установлен в {Setup.InstallDir}. Удаляется через «Установленные приложения»."
            : "Лаунчер не установлен — работает из той папки, где лежит файл.";
    }

    private void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        var want = AutoStartBox.IsChecked == true;

        // Включать автозапуск для файла из «Загрузок» бессмысленно: папку
        // рано или поздно почистят, и в автозапуске останется битая ссылка.
        if (want && !Setup.IsInstalled)
        {
            var answer = MessageBox.Show(
                "Для автозапуска лаунчер лучше сначала установить на компьютер." +
                Environment.NewLine + Environment.NewLine +
                "Иначе автозапуск будет ссылаться на этот файл, и если папку почистят, " +
                "запускать станет нечего." + Environment.NewLine + Environment.NewLine +
                "Установить сейчас?",
                "MurloVille", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel)
            {
                AutoStartBox.IsChecked = false;
                return;
            }
            if (answer == MessageBoxResult.Yes)
            {
                try { Setup.Install(); }
                catch (Exception ex)
                {
                    MessageBox.Show("Не смог установить лаунчер:" + Environment.NewLine + ex.Message,
                        "MurloVille", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                ShowSetupState();
            }
        }

        Setup.AutoStart = want;
        AutoStartBox.IsChecked = Setup.AutoStart;   // показываем то, что вышло на самом деле
    }

    // --- где лежит игра ------------------------------------------------------

    /// <summary>
    /// Настройки держим отдельно от программы: путь к игре должен пережить и
    /// переустановку лаунчера, и запуск его из другой папки.
    /// </summary>
    private static string ConfigPath => Path.Combine(Setup.DataDir, "settings.txt");

    /// <summary>Старое место, рядом с программой. Читаем ради тех, кто ставил до установщика.</summary>
    private static string LegacyConfigPath => Path.Combine(AppContext.BaseDirectory, "murlo-launcher.txt");

    private static string? SavedRoot()
    {
        foreach (var p in new[] { ConfigPath, LegacyConfigPath })
        {
            try
            {
                if (!File.Exists(p)) continue;
                var s = File.ReadAllText(p).Trim();
                if (s.Length > 0) return s;
            }
            catch { }
        }
        return null;
    }

    private static void RememberRoot(string root)
    {
        try
        {
            Directory.CreateDirectory(Setup.DataDir);
            File.WriteAllText(ConfigPath, root);
        }
        catch { }
    }

    private void ShowPath()
    {
        PathBox.Text = _root ?? "";
        PathHint.Text = _root is null
            ? "Игра не найдена. Впиши путь к папке с Wow.exe, выбери её кнопкой «Обзор» или нажми «Найти» — обойду диски сам."
            : "Игра на месте.";
    }

    /// <summary>Принять путь, введённый руками или выбранный в проводнике.</summary>
    private async Task ApplyPath(string raw)
    {
        var dir = raw.Trim().Trim('"');
        if (dir.Length == 0 || _busy) return;
        if (string.Equals(dir.TrimEnd('\\'), _root?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;

        if (!ClientFinder.IsClient(dir))
        {
            PathHint.Text = Directory.Exists(dir)
                ? "В этой папке нет Wow.exe — нужна папка с самой игрой."
                : "Такой папки нет.";
            return;
        }

        _root = dir;
        RememberRoot(dir);
        ShowPath();
        _mode = Mode.Play;
        PlayBtn.Content = "ИГРАТЬ";
        if (_manifest is not null) await Sync();
    }

    private async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ApplyPath(PathBox.Text);
    }

    private async void PathBox_LostFocus(object sender, RoutedEventArgs e)
        => await ApplyPath(PathBox.Text);

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = _root is null
                ? "Укажи папку с игрой — или пустую папку, если игры ещё нет"
                : "Укажи папку с установленной игрой — ту, где лежит Wow.exe",
        };
        if (dlg.ShowDialog() != true) return;

        var target = dlg.FolderName;

        if (ClientFinder.IsClient(target))
        {
            await ApplyPath(target);
            return;
        }

        // Игры там нет — значит человек показывает, куда её поставить.
        var gb = (_manifest?.totalBytes ?? 0) / 1073741824.0;
        var notEmpty = Directory.Exists(target) && Directory.GetFileSystemEntries(target).Length > 0;

        var answer = MessageBox.Show(
            $"В этой папке нет Wow.exe." + Environment.NewLine + Environment.NewLine +
            (notEmpty ? "Папка к тому же не пустая." + Environment.NewLine + Environment.NewLine : "") +
            $"Установить игру сюда? Скачать предстоит {gb:0.#} ГБ.",
            "MurloVille", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        _root = target;
        RememberRoot(target);
        PathBox.Text = target;
        PathHint.Text = "Сюда поставим игру.";
        if (_manifest is not null) await Sync();
    }

    /// <summary>
    /// Поиск игры по дискам. Долгий, поэтому идёт в стороне от окна и его
    /// можно прервать той же кнопкой.
    /// </summary>
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_scan is not null)          // уже ищем — значит это отмена
        {
            _scan.Cancel();
            return;
        }

        _scan = new CancellationTokenSource();
        ScanBtn.Content = "ОТМЕНА";
        BrowseBtn.IsEnabled = false;
        PathHint.Text = "Ищу игру на дисках…";

        var where = new Progress<string>(dir => PathHint.Text = "Смотрю: " + dir);

        List<string>? found = null;
        var cancelled = false;
        try
        {
            found = await Task.Run(() => ClientFinder.DeepScan(where, _scan.Token), _scan.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception ex) { PathHint.Text = "Поиск не удался: " + Short(ex.Message); }
        finally
        {
            _scan.Dispose();
            _scan = null;
            ScanBtn.Content = "НАЙТИ";
            BrowseBtn.IsEnabled = true;
        }

        if (found is null || found.Count == 0)
        {
            if (cancelled)
                ShowPath();
            else
                PathHint.Text = "Игру не нашёл. Впиши путь руками или выбери папку кнопкой «Обзор» — "
                              + "поиск смотрит не глубже четырёх уровней от корня диска.";
            return;
        }

        // Одна копия — берём её. Несколько — спрашиваем: какая из них нужна,
        // программа знать не может, а ошибка стоит шестнадцати гигабайт не туда.
        var chosen = found.Count == 1 ? found[0] : ClientChoice.Ask(this, found);
        if (chosen is null) { ShowPath(); return; }

        await ApplyPath(chosen);
    }

    // --- установка и обновление одной дорогой --------------------------------

    /// <summary>
    /// Установка и обновление — одно и то же: сверяем список и качаем
    /// недостающее. Разница лишь в том, что при установке недостающее — это всё.
    /// </summary>
    private async Task Sync()
    {
        if (_manifest?.files is null || _root is null || _busy) return;

        _busy = true;
        PlayBtn.IsEnabled = false;
        SetPathControls(false);
        try
        {
            Say("Сверяю файлы…");

            // Сверка читает файлы и считает хеши, поэтому уходит с потока
            // интерфейса целиком: на шестнадцати гигабайтах окно иначе
            // замирает на минуты и выглядит зависшим.
            var progress = new Progress<(int Done, int Total, string Path)>(p =>
            {
                Bar.Value = (double)p.Done / p.Total * 100;
                DetailText.Text = p.Path;
            });

            var plan = await Task.Run(() => Check(progress));

            if (plan.Todo.Count == 0)
            {
                Say("Клиент обновлён.", "Всё на месте.");
                Bar.Value = 100;
                _mode = Mode.Play;
                PlayBtn.Content = "ИГРАТЬ";
                PlayBtn.IsEnabled = true;
                return;
            }

            // Игру надо закрыть до начала, а не узнавать об этом на первом же
            // файле после получаса загрузки.
            if (GameRunning())
            {
                OfferRetry("Игра запущена — обновить не смогу.",
                    "Закрой World of Warcraft и нажми «Повторить». Пока игра работает, "
                    + "она держит файлы клиента и заменить их нельзя.");
                return;
            }

            // Место проверяем до начала, а не на двенадцатом гигабайте.
            if (!EnoughSpace(plan.Bytes, out var freeGb))
            {
                Bar.Value = 0;
                OfferRetry("Не хватает места на диске.",
                    $"Нужно {Gb(plan.Bytes)}, свободно {freeGb:0.#} ГБ. Освободи место и нажми «Повторить».");
                return;
            }

            var big = plan.Bytes > 1073741824;   // больше гигабайта — это установка
            Say(big ? $"Устанавливаю игру: {Gb(plan.Bytes)}" : $"Качаю обновление: {Gb(plan.Bytes)}",
                big ? "Первый раз это долго. Можно свернуть окно." : null);
            Bar.Value = 0;

            long done = 0;
            var started = DateTime.UtcNow;
            var failed = new List<string>();

            foreach (var f in plan.Todo)
            {
                try
                {
                    await Download(f, done, plan.Bytes, started);
                }
                catch (Exception ex)
                {
                    // Один упавший файл не повод бросать остальные: чаще всего
                    // это единственный занятый MPQ, а не общая беда.
                    failed.Add($"{f.path}: {Short(ex.Message)}");
                    if (GameRunning()) break;   // дальше будет ровно то же самое
                }
                done += f.size;
                Bar.Value = (double)done / plan.Bytes * 100;
            }

            if (failed.Count > 0)
            {
                var tail = failed.Count > 1 ? $" (и ещё {failed.Count - 1})" : "";
                if (GameRunning())
                {
                    OfferRetry("Игра запущена — обновление не доставить.",
                        "Закрой World of Warcraft и нажми «Повторить».");
                }
                else
                {
                    OfferRetry("Часть файлов не обновилась.", failed[0] + tail);
                }
                return;
            }

            Say(big ? "Игра установлена." : "Обновление установлено.",
                $"Файлов: {plan.Todo.Count}, {Gb(plan.Bytes)}");
            Bar.Value = 100;
            _mode = Mode.Play;
            PlayBtn.Content = "ИГРАТЬ";
            PlayBtn.IsEnabled = true;
        }
        finally
        {
            _busy = false;
            SetPathControls(true);
        }
    }

    private void SetPathControls(bool on)
    {
        PathBox.IsEnabled = on;
        BrowseBtn.IsEnabled = on;
        ScanBtn.IsEnabled = on;
    }

    private void OfferRetry(string status, string detail)
    {
        Say(status, detail);
        _mode = Mode.Retry;
        PlayBtn.Content = "ПОВТОРИТЬ";
        PlayBtn.IsEnabled = true;
    }

    private string Local(Entry f) => Path.Combine(_root!, f.path.Replace('/', '\\'));

    /// <summary>
    /// Запущена ли игра. Пока Wow.exe работает, он держит MPQ открытыми, и
    /// заменить их нельзя — Windows отвечает отказом в доступе, из-за чего
    /// обновление выглядит как поломка лаунчера.
    /// </summary>
    private static bool GameRunning()
    {
        try { return Process.GetProcessesByName("Wow").Length > 0; }
        catch { return false; }
    }

    // --- сверка --------------------------------------------------------------

    private sealed record Plan(List<Entry> Todo, long Bytes);

    /// <summary>Считает, что надо докачать. Работает не в потоке интерфейса.</summary>
    private Plan Check(IProgress<(int, int, string)> progress)
    {
        var files = _manifest!.files!;
        var verified = LoadVerified();
        var fresh = new Dictionary<string, string>();

        var todo = new List<Entry>();
        long bytes = 0;

        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            progress.Report((i, files.Count, f.path));

            var local = Local(f);
            if (IsGood(local, f, verified, out var stamp))
            {
                fresh[f.path] = stamp;
                // Хвост от прошлой прерванной загрузки этому файлу уже не нужен.
                TryDelete(local + ".part");
            }
            else
            {
                todo.Add(f);
                bytes += f.size;
            }
        }

        SaveVerified(fresh);
        return new Plan(todo, bytes);
    }

    /// <summary>Файл на месте и совпадает с манифестом?</summary>
    private static bool IsGood(string local, Entry f, IReadOnlyDictionary<string, string> verified,
                               out string stamp)
    {
        stamp = "";
        var info = new FileInfo(local);
        if (!info.Exists || info.Length != f.size) return false;

        stamp = $"{info.Length}|{info.LastWriteTimeUtc.Ticks}|{f.sha256}";

        if (string.IsNullOrEmpty(f.sha256)) return true;      // хеша нет — верим размеру

        // Уже считали этот же файл в прошлый раз и с тех пор его не трогали.
        if (verified.TryGetValue(f.path, out var known) && known == stamp) return true;

        try
        {
            // Открываем с общим доступом: игра может держать MPQ открытым, но
            // прочитать его при этом никто не мешает.
            using var stream = new FileStream(local, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return hash.Equals(f.sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Помним, что уже проверяли. Пересчитывать SHA-256 по шестнадцати
    /// гигабайтам при каждом запуске — это минуты работы диска ради ответа,
    /// который почти всегда «всё на месте». Запись привязана к размеру и
    /// времени изменения: тронули файл — посчитаем заново.
    /// </summary>
    private string VerifiedPath => Path.Combine(_root!, "murlo-launcher.cache");

    private Dictionary<string, string> LoadVerified()
    {
        var map = new Dictionary<string, string>();
        try
        {
            if (!File.Exists(VerifiedPath)) return map;
            foreach (var line in File.ReadAllLines(VerifiedPath))
            {
                var cut = line.IndexOf('=');
                if (cut > 0) map[line[..cut]] = line[(cut + 1)..];
            }
        }
        catch { }
        return map;
    }

    private void SaveVerified(Dictionary<string, string> map)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var (k, v) in map) sb.Append(k).Append('=').AppendLine(v);
            File.WriteAllText(VerifiedPath, sb.ToString());
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

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

    // --- загрузка ------------------------------------------------------------

    /// <summary>
    /// Сколько секунд поток может молчать, прежде чем мы сочтём его мёртвым.
    /// Без этого лаунчер висел на застывшей ссылке Диска бесконечно и выглядел
    /// зависшим — «встаёт и не обновляет дальше».
    /// </summary>
    private const int StallSeconds = 45;

    /// <summary>Сколько раз пробуем один файл, прежде чем сдаться.</summary>
    private const int Attempts = 4;

    /// <summary>Адрес файла. Для Диска ссылку приходится просить каждый раз: она временная.</summary>
    private async Task<string> ResolveUrl(Entry f, CancellationToken token)
    {
        if (f.src != "yandex")
            return $"{Base}/files/{f.path}";

        var url = $"{YandexApi}?public_key={Uri.EscapeDataString(_manifest!.publicKey!)}" +
                  $"&path={Uri.EscapeDataString(f.remote ?? "/" + f.path)}";
        var json = await Http.GetStringAsync(url, token);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("href").GetString()
               ?? throw new IOException("хранилище не дало ссылку");
    }

    /// <summary>Чтение с таймаутом: молчание дольше StallSeconds — обрыв.</summary>
    private static async Task<int> ReadOrStall(Stream net, byte[] buf)
    {
        using var stall = new CancellationTokenSource(TimeSpan.FromSeconds(StallSeconds));
        try
        {
            return await net.ReadAsync(buf, stall.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("поток застыл");
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is IOException or HttpRequestException or TimeoutException
           or OperationCanceledException or System.Net.Sockets.SocketException;

    /// <summary>
    /// Качаем в файл .part и дописываем с места обрыва. На шестнадцати
    /// гигабайтах разрыв связи — не исключение, а норма, и начинать заново
    /// было бы издевательством. Обрыв или застывший поток — берём свежую
    /// ссылку и продолжаем с того же места, до четырёх попыток на файл.
    /// </summary>
    private async Task Download(Entry f, long doneBefore, long totalBytes, DateTime started)
    {
        var local = Local(f);
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        var part = local + ".part";

        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > f.size) { File.Delete(part); have = 0; }

        for (var attempt = 1; have < f.size; attempt++)
        {
            try
            {
                have = await Pull(f, part, have, doneBefore, totalBytes, started);
            }
            catch (Exception ex) when (attempt < Attempts && IsTransient(ex))
            {
                DetailText.Text = $"{f.path} — обрыв связи, пробую снова ({attempt} из {Attempts - 1})";
                await Task.Delay(1500 * attempt);
                have = File.Exists(part) ? new FileInfo(part).Length : 0;
            }
        }

        if (have < f.size)
            throw new IOException("файл не докачался");

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

        ReplaceFile(part, local);
    }

    /// <summary>Одна попытка: с текущего места до конца файла или до обрыва.</summary>
    private async Task<long> Pull(Entry f, string part, long have, long doneBefore, long totalBytes, DateTime started)
    {
        using var linkCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var req = new HttpRequestMessage(HttpMethod.Get, await ResolveUrl(f, linkCts.Token));
        if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);

        using var headCts = new CancellationTokenSource(TimeSpan.FromSeconds(StallSeconds));
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headCts.Token);
        // Хранилище может не поддержать докачку — тогда начинаем сначала.
        if (have > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
        {
            File.Delete(part);
            have = 0;
        }
        resp.EnsureSuccessStatusCode();

        // Диск при исчерпанном лимите отдаёт страницу с извинениями вместо
        // файла. Качать её бессмысленно: скажем сразу и по-человечески.
        var type = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (type.Contains("html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("хранилище отдало страницу вместо файла — лимит Диска, попробуй позже");

        await using var net = await resp.Content.ReadAsStreamAsync(headCts.Token);
        await using var file = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create,
                                              FileAccess.Write, FileShare.None, 1 << 20);

        var buf = new byte[1 << 20];
        var lastShown = DateTime.UtcNow;
        while (true)
        {
            var read = await ReadOrStall(net, buf);
            if (read <= 0) break;
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
        return have;
    }

    /// <summary>
    /// Ставит скачанный файл на место старого.
    ///
    /// Это самое хрупкое место всей загрузки. Файл может быть помечен «только
    /// чтение», его может секунду держать антивирус или проводник, а если
    /// открыта игра — она держит MPQ намертво. Пробуем несколько раз и только
    /// потом сдаёмся, объяснив причину по-человечески: «Access to the path is
    /// denied» игроку ничего не говорит.
    /// </summary>
    private static void ReplaceFile(string part, string local)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(local))
                {
                    var attrs = File.GetAttributes(local);
                    if (attrs.HasFlag(FileAttributes.ReadOnly))
                        File.SetAttributes(local, attrs & ~FileAttributes.ReadOnly);
                }

                File.Move(part, local, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                if (attempt >= 4)
                {
                    throw new IOException(GameRunning()
                        ? "файл занят игрой — закрой World of Warcraft"
                        : "не удалось заменить файл, он чем-то занят", ex);
                }
                Thread.Sleep(400);
            }
        }
    }

    // --- главная кнопка ------------------------------------------------------

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        // «Повторить» после занятых файлов или нехватки места.
        if (_mode == Mode.Retry)
        {
            await Sync();
            return;
        }

        if (_root is null)
        {
            Browse_Click(sender, e);
            return;
        }

        FixRealmlist();
        ClearWdbCache();
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
            MessageBox.Show("Не смог запустить игру:" + Environment.NewLine + ex.Message, "MurloVille",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Стираем кэш имён предметов и существ перед запуском. Клиент помнит
    /// ответы сервера в Cache\WDB и после наших правок показывает старые
    /// названия — «Noble Bruffalon Mount» вместо русского — пока кэш не
    /// удалить руками. Игра без кэша просто спросит сервер заново.
    /// </summary>
    private void ClearWdbCache()
    {
        try
        {
            var wdb = Path.Combine(_root!, "Cache", "WDB");
            if (Directory.Exists(wdb)) Directory.Delete(wdb, recursive: true);
        }
        catch
        {
            // Занято или нет прав — не страшно, игра запустится и так.
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
