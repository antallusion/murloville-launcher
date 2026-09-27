using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    private enum Mode { Play, Install, Retry, Elevate }

    private readonly bool _autostart;
    private string? _root;
    private Manifest? _manifest;
    private Mode _mode = Mode.Play;
    private bool _busy;
    /// <summary>Игра у человека уже есть — кладём сверху только наше.</summary>
    private bool _patchOnly;
    /// <summary>Разовая полная сверка по кнопке: считаем хеши заново, память не в счёт.</summary>
    private bool _forceVerify;
    /// <summary>Сколько байт клиенту ещё не хватает. Пока не ноль — в игру не пускаем.</summary>
    private long _pending;
    private readonly Options _options = Options.Load();
    private readonly Throttle _throttle = new();
    private CancellationTokenSource? _scan;
    /// <summary>
    /// Не наши базовые архивы, которые человек разрешил заменить. Прежний файл
    /// не затираем, а откладываем в сторону в момент замены — не раньше: если
    /// загрузка оборвётся, игра останется с прежним файлом, а не без него.
    /// </summary>
    private readonly HashSet<string> _aside = new(StringComparer.OrdinalIgnoreCase);

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

    private sealed record Entry(string path, long size, string sha256, string src, string? remote, string? url);

    private sealed record Manifest(
        string? launcherVersion, string? launcherSha256, string? launcherUrl,
        string? publicKey, string? baseUrl, long totalBytes, List<Entry>? files,
        List<string>? ours);

    // --- запуск --------------------------------------------------------------

    private async Task Startup()
    {
        _ = ShowOnline();
        _ = LoadArt();

        AutoStartBox.IsChecked = Setup.AutoStart;
        ShowSetupState();
        ShowOptions();

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
            _pending = 0;
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
            ? "Игра не найдена. Нажми «Установить» и выбери пустую папку. Наш клиент уже стоит? Впиши путь, выбери его кнопкой «Обзор» или нажми «Найти»."
            : "Игра на месте.";
    }

    private const string NoteHead = "Играть можно только нашим клиентом.";
    private const string NoteBody =
        "Пустую папку лаунчер заполнит сам, чистый 3.3.5a доведёт до нашего. " +
        "Клиент другого сервера не указывай: поверх него персонажи становятся невидимыми.";

    /// <summary>
    /// Плашка под путём. Обычно — спокойное правило золотом. Если человек
    /// оставил чужой клиент как есть — красная, с тем, как это исправить:
    /// невидимые персонажи через неделю не должны стать загадкой.
    /// </summary>
    private void ShowClientNote(bool foreign)
    {
        if (!foreign)
        {
            ClientNoteHead.Text = NoteHead;
            ClientNoteBody.Text = NoteBody;
            ClientNoteHead.Foreground = (System.Windows.Media.Brush)FindResource("Gold");
            ClientNoteBox.BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x99, 0xE6, 0xC3, 0x6A));
            ClientNoteBox.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x14, 0xE6, 0xC3, 0x6A));
            return;
        }
        ClientNoteHead.Text = "Эта папка не совпадает с нашим клиентом.";
        ClientNoteBody.Text =
            "Невидимые персонажи или тёмный экран — отсюда. Нажми «Проверить файлы» " +
            "и выбери «Поставить наш клиент в новую папку».";
        ClientNoteHead.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xF0, 0xA0, 0x8A));
        ClientNoteBox.BorderBrush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xE0, 0x7A, 0x5F));
        ClientNoteBox.Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(0x1F, 0xE0, 0x7A, 0x5F));
    }

    /// <summary>Современный WoW вместо 3.3.5a — объяснить, почему папка не годится.</summary>
    private const string ModernHint =
        "Это современный World of Warcraft, а не 3.3.5a — для MurloVille он не подойдёт, и трогать его я не буду. "
        + "Выбери пустую папку: поставлю наш клиент.";

    /// <summary>Принять путь, введённый руками или выбранный в проводнике.</summary>
    private async Task ApplyPath(string raw)
    {
        var dir = raw.Trim().Trim('"');
        if (dir.Length == 0 || _busy) return;
        if (string.Equals(dir.TrimEnd('\\'), _root?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;

        if (!ClientFinder.IsClient(dir))
        {
            PathHint.Text = ClientFinder.IsModernClient(dir) ? ModernHint
                : Directory.Exists(dir) ? "В этой папке нет Wow.exe — нужна папка с самой игрой."
                : "Такой папки нет.";
            return;
        }

        _root = dir;
        RememberRoot(dir);
        ShowPath();
        ShowClientNote(false);
        PathHint.Text = "Проверю, наш ли это клиент, — до того как что-то качать.";
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
                ? "Пустая папка — поставлю наш клиент. Или папка с нашим клиентом либо чистым 3.3.5a"
                : "Наш клиент или чистый 3.3.5a (папка с Wow.exe). Клиент другого сервера не подойдёт",
        };
        if (dlg.ShowDialog() != true) return;

        var target = dlg.FolderName;

        if (ClientFinder.IsClient(target))
        {
            await ApplyPath(target);
            return;
        }

        if (ClientFinder.IsModernClient(target))
        {
            PathHint.Text = ModernHint;
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
        ShowClientNote(false);
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
        // Чужие архивы видны по одному списку каталога, без чтения файлов, —
        // можно пометить сразу, чтобы человек выбирал не вслепую.
        var known = _manifest?.files?.Select(f => f.path).ToList();
        string? Describe(string dir)
        {
            if (known is null) return null;
            var n = ClientState.ForeignArchives(dir, known).Count;
            return n > 0 ? $"чужих архивов: {n}" : null;
        }

        var chosen = found.Count == 1 ? found[0] : ClientChoice.Ask(this, found, Describe);
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
        // Круг повторяется, только если человек выбрал «поставить наш клиент в
        // новую папку»: тогда сверка начинается заново уже для неё.
        while (await SyncOnce()) { }
    }

    /// <returns>true — папка игры сменилась, надо пройти сверку ещё раз.</returns>
    private async Task<bool> SyncOnce()
    {
        if (_manifest?.files is null || _root is null || _busy) return false;

        // Права на запись выясняем до сверки: узнать об этом после получаса
        // загрузки — худшее, что можно сделать с человеком.
        if (!ClientState.CanWrite(_root))
        {
            Say("В эту папку нельзя писать без прав администратора.",
                "Игра лежит в защищённом месте (обычно Program Files). "
                + "Нажми кнопку — перезапущусь с правами и обновлю.");
            _mode = Mode.Elevate;
            PlayBtn.Content = "ОТ АДМИНИСТРАТОРА";
            PlayBtn.IsEnabled = true;
            return false;
        }

        // Игра уже стоит — значит, обновляем, а не ставим заново. Человек может
        // потребовать сверить и стоковые архивы: тогда идём по всему списку.
        _patchOnly = ClientState.HasBaseGame(_root) && !_options.CheckStock;
        _throttle.SetLimit(_options.SpeedMbps);
        _aside.Clear();

        _busy = true;
        PlayBtn.IsEnabled = false;
        PlayBtn.Content = "ИГРАТЬ";   // не «Повторить»: пока идёт работа, повторять нечего
        SetPathControls(false);
        try
        {
            Say("Сверяю файлы…",
                _patchOnly ? "Сверяю основу клиента и наши патчи. Остальные стоковые архивы не трогаю." : null);

            // Сверка читает файлы и считает хеши, поэтому уходит с потока
            // интерфейса целиком: на шестнадцати гигабайтах окно иначе
            // замирает на минуты и выглядит зависшим.
            var progress = new Progress<(int Done, int Total, string Path)>(p =>
            {
                Bar.Value = (double)p.Done / p.Total * 100;
                DetailText.Text = p.Path;
            });

            var plan = await Task.Run(() => Check(progress));

            // --- чужой клиент ---------------------------------------------
            // Решаем до загрузки: что именно качать, зависит от ответа.
            var todo = plan.Todo;
            var foreignKept = false;
            if (plan.Differs.Count > 0 || plan.Foreign.Count > 0)
            {
                var sig = Signature(plan);
                ForeignClient.Choice choice;
                if (!_forceVerify && AcceptedAsIs(sig))
                {
                    choice = ForeignClient.Choice.AsIs;   // уже решено раньше — не спрашиваем при каждом запуске
                }
                else if (WindowState == WindowState.Minimized)
                {
                    // Автозапуск: окно свёрнуто, а вопрос без ответа качать не
                    // даёт. Ждём, пока человек откроет лаунчер сам.
                    _pending = Math.Max(1, plan.Bytes);
                    OfferRetry("Нужно решение: папка игры не совпадает с нашим клиентом.",
                        "Нажми «Повторить» — покажу, что не так, и предложу варианты.");
                    return false;
                }
                else
                {
                    Say("Эта папка не совпадает с нашим клиентом.", "Жду решения.");
                    Bar.Value = 0;
                    choice = ForeignClient.Ask(this,
                        plan.Differs.Select(f => new ForeignClient.Differ(f.path, LocalSize(f), f.size)).ToList(),
                        plan.Foreign,
                        newFolderBytes: _manifest.totalBytes > 0 ? _manifest.totalBytes : _manifest.files.Sum(f => f.size),
                        cleanUpBytes: plan.Bytes + plan.Differs.Sum(f => f.size),
                        asIsBytes: plan.Bytes);
                }

                switch (choice)
                {
                    case ForeignClient.Choice.Cancel:
                        _pending = Math.Max(1, plan.Bytes);
                        OfferRetry("Обновление остановлено: папка не совпадает с нашим клиентом.",
                            "Нажми «Повторить», чтобы выбрать, что делать, — или укажи другую папку.");
                        return false;

                    case ForeignClient.Choice.NewFolder:
                        var fresh = PickNewFolder();
                        if (fresh is null)
                        {
                            _pending = Math.Max(1, plan.Bytes);
                            OfferRetry("Новая папка не выбрана — ничего не качаю.",
                                "Нажми «Повторить», чтобы выбрать снова.");
                            return false;
                        }
                        _root = fresh;
                        RememberRoot(fresh);
                        ShowPath();
                        PathHint.Text = "Сюда поставлю наш клиент. Прежняя папка осталась как была.";
                        ShowClientNote(false);
                        return true;

                    case ForeignClient.Choice.CleanUp:
                        if (GameRunning())
                        {
                            OfferRetry("Игра запущена — отложить файлы не смогу.",
                                "Закрой World of Warcraft и нажми «Повторить».");
                            return false;
                        }
                        var stuck = await Task.Run(() => MoveForeignAside(plan.Foreign));
                        if (stuck is not null)
                        {
                            OfferRetry("Не смог отложить чужие файлы.", stuck);
                            return false;
                        }
                        todo = todo.Concat(plan.Differs).ToList();
                        foreach (var f in plan.Differs) _aside.Add(f.path);
                        ForgetAsIs();
                        ShowClientNote(false);
                        PathHint.Text = plan.Foreign.Count > 0
                            ? $"Чужие архивы отложены в «{ClientState.AsideFolder}» внутри игры — ничего не удалено."
                            : $"Прежние архивы отложу в «{ClientState.AsideFolder}» внутри игры, когда заменю.";
                        break;

                    case ForeignClient.Choice.AsIs:
                        RememberAsIs(sig);
                        foreignKept = true;
                        ShowClientNote(true);
                        PathHint.Text = "Игра на месте, но это не наш клиент — оставлено как есть.";
                        break;
                }
            }
            else
            {
                ForgetAsIs();
                ShowClientNote(false);
                if (ClientFinder.IsClient(_root)) PathHint.Text = "Игра на месте.";
            }

            var bytes = todo.Sum(f => f.size);
            _pending = bytes;

            if (todo.Count == 0)
            {
                Say("Клиент обновлён.", foreignKept
                    ? "Папка оставлена как есть — если персонажи невидимы, смотри плашку выше."
                    : "Всё на месте.");
                Bar.Value = 100;
                _mode = Mode.Play;
                PlayBtn.Content = "ИГРАТЬ";
                PlayBtn.IsEnabled = true;
                return false;
            }

            // Игру надо закрыть до начала, а не узнавать об этом на первом же
            // файле после получаса загрузки.
            if (GameRunning())
            {
                OfferRetry("Игра запущена — обновить не смогу.",
                    "Закрой World of Warcraft и нажми «Повторить». Пока игра работает, "
                    + "она держит файлы клиента и заменить их нельзя.");
                return false;
            }

            // Место проверяем до начала, а не на двенадцатом гигабайте.
            if (!EnoughSpace(bytes, out var freeGb))
            {
                Bar.Value = 0;
                OfferRetry("Не хватает места на диске.",
                    $"Нужно {Gb(bytes)}, свободно {freeGb:0.#} ГБ. Освободи место и нажми «Повторить».");
                return false;
            }

            var big = bytes > 1073741824;   // больше гигабайта — это установка
            Say(big ? $"Устанавливаю игру: {Gb(bytes)}" : $"Качаю обновление: {Gb(bytes)}",
                big ? "Первый раз это долго. Можно свернуть окно." : null);
            Bar.Value = 0;

            // Хвосты прошлой прерванной загрузки засчитываем сразу, иначе
            // полоска начнёт с нуля там, где половина уже скачана.
            _bytesDone = 0;
            foreach (var f in todo)
            {
                var part = Local(f) + ".part";
                if (File.Exists(part)) _bytesDone += new FileInfo(part).Length;
            }

            var started = DateTime.UtcNow;
            var failed = new System.Collections.Concurrent.ConcurrentBag<string>();

            // Несколько файлов разом: один поток редко забирает всю полосу, а
            // на установке в семнадцать гигабайт это разница между часом и
            // тремя. Сколько именно — задаёт человек в настройках.
            var lanes = Math.Clamp(_options.Parallel, 1, Options.MaxParallel);
            var gate = new SemaphoreSlim(lanes);
            var stop = false;

            await Task.WhenAll(todo.Select(async f =>
            {
                await gate.WaitAsync();
                try
                {
                    if (Volatile.Read(ref stop)) return;
                    await Download(f, bytes, started);
                }
                catch (Exception ex)
                {
                    // Один упавший файл не повод бросать остальные: чаще всего
                    // это единственный занятый MPQ, а не общая беда. А вот
                    // запущенная игра — беда общая, и дальше идти незачем.
                    failed.Add($"{f.path}: {Short(ex.Message)}");
                    if (GameRunning()) Volatile.Write(ref stop, true);
                }
                finally
                {
                    gate.Release();
                }
            }));

            if (failed.Count > 0)
            {
                var tail = failed.Count > 1 ? $" (и ещё {failed.Count - 1})" : "";
                if (GameRunning())
                {
                    OfferRetry("Игра запущена — обновление не доставить.",
                        "Закрой World of Warcraft и нажми «Повторить».");
                }
                else if (!ClientState.CanWrite(_root))
                {
                    // Права могли отобрать и посреди работы — например,
                    // антивирус запер папку.
                    Say("Не хватило прав на запись.", "Перезапущусь с правами администратора и доделаю.");
                    _mode = Mode.Elevate;
                    PlayBtn.Content = "ОТ АДМИНИСТРАТОРА";
                    PlayBtn.IsEnabled = true;
                }
                else
                {
                    OfferRetry("Часть файлов не обновилась.", failed.First() + tail);
                }
                return false;
            }

            _pending = 0;
            Say(big ? "Игра установлена." : "Обновление установлено.",
                $"Файлов: {todo.Count}, {Gb(bytes)}");
            Bar.Value = 100;
            _mode = Mode.Play;
            PlayBtn.Content = "ИГРАТЬ";
            PlayBtn.IsEnabled = true;
            return false;
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
        RefreshBtn.IsEnabled = on;
        VerifyBtn.IsEnabled = on;
    }

    // --- настройки загрузки --------------------------------------------------

    private void ShowOptions()
    {
        SpeedBox.Text = _options.SpeedMbps > 0
            ? _options.SpeedMbps.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
            : "0";
        if (ParallelBox.Items.Count == 0)
            for (var i = 1; i <= Options.MaxParallel; i++) ParallelBox.Items.Add(i);
        ParallelBox.SelectedItem = _options.Parallel;
        StockBox.IsChecked = _options.CheckStock;
        _throttle.SetLimit(_options.SpeedMbps);
    }

    private void Options_Click(object sender, RoutedEventArgs e) =>
        OptionsPanel.Visibility = OptionsPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;

    private void Speed_Changed(object sender, RoutedEventArgs e)
    {
        var text = (SpeedBox.Text ?? "").Replace(',', '.').Trim();
        _options.SpeedMbps = double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 0;
        _options.Save();
        ShowOptions();
    }

    private void Speed_Key(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Speed_Changed(sender, e);
    }

    private void Parallel_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ParallelBox.SelectedItem is int n && n != _options.Parallel)
        {
            _options.Parallel = n;
            _options.Save();
        }
    }

    private void Stock_Click(object sender, RoutedEventArgs e)
    {
        _options.CheckStock = StockBox.IsChecked == true;
        _options.Save();
    }

    /// <summary>Перечитать манифест и догнать клиент — по кнопке, без перезапуска.</summary>
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Say("Спрашиваю сервер обновлений…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var json = await Http.GetStringAsync($"{Base}/manifest.json?t={DateTime.UtcNow.Ticks}", cts.Token);
            _manifest = JsonSerializer.Deserialize<Manifest>(json);
        }
        catch (Exception ex)
        {
            Say("Сервер обновлений не отвечает.", Short(ex.Message));
            return;
        }
        if (await SelfUpdate()) return;
        if (_root is null) { Say("Сначала укажи папку с игрой."); return; }
        await Sync();
    }

    /// <summary>
    /// Полная сверка по кнопке: считаем хеши заново, не веря прошлым записям.
    /// Это лечение для случая «файлы вроде на месте, а игра ведёт себя странно»:
    /// битый файл того же размера иначе не найти.
    /// </summary>
    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _root is null) return;
        _forceVerify = true;
        try { await Sync(); }
        finally { _forceVerify = false; }
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

    /// <summary>
    /// Уйти вместе с игрой.
    ///
    /// После запуска лаунчер остаётся в памяти только ради просьб из игры, и
    /// висеть после её закрытия ему незачем. Ждём, пока игра появится, и
    /// выходим, когда она пропала. Если она так и не появилась за минуту —
    /// не запустилась, путь не тот, антивирус, — выходим тоже: невидимый
    /// процесс, висящий вечно, хуже любой ошибки.
    /// </summary>
    private void WatchGameExit()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        var appeared = false;
        var waited = 0;
        timer.Tick += (_, _) =>
        {
            if (GameRunning())
            {
                appeared = true;
                return;
            }
            waited += 5;
            if (!appeared && waited < 60)
                return;
            timer.Stop();
            ShopWatch.Stop();
            Application.Current.Shutdown();
        };
        timer.Start();
    }

    // --- сверка --------------------------------------------------------------

    /// <param name="Todo">Что докачать в любом случае.</param>
    /// <param name="Differs">Базовые патчи, которые есть, но не наши. Качаются, только если человек разрешил.</param>
    /// <param name="Foreign">Архивы, которых в нашем клиенте нет вовсе.</param>
    private sealed record Plan(List<Entry> Todo, long Bytes, List<Entry> Differs, List<ClientState.Extra> Foreign);

    /// <summary>Перезапуск с правами администратора — тем же путём и ключами.</summary>
    private void Elevate()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = _autostart ? "--autostart" : "",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            });
            Application.Current.Shutdown();
        }
        catch
        {
            // Отказался в окне UAC — остаёмся как были.
            Say("Без прав администратора обновить эту папку не выйдет.",
                "Либо разреши запуск от администратора, либо перенеси игру в обычную папку — например C:\\Games\\WoW.");
        }
    }

    /// <summary>Считает, что надо докачать. Работает не в потоке интерфейса.</summary>
    private Plan Check(IProgress<(int, int, string)> progress)
    {
        var all = _manifest!.files!;
        var files = all;
        // В готовой папке проверяем только своё: стоковые архивы не наше
        // дело, и перекачивать их незачем. Три исключения. Базовые патчи
        // сверяем всегда — у нас они не стоковые, и чужие под нашими таблицами
        // дают невидимых персонажей. Недостающие архивы докачиваем — без них
        // игра не запустится вовсе (так бывает после прерванной установки).
        if (_patchOnly)
            files = all.Where(f => ClientState.IsOurs(f.path, _manifest!.ours)
                                   || ClientState.IsBasePatch(f.path)
                                   || (f.path.EndsWith(".MPQ", StringComparison.OrdinalIgnoreCase)
                                       && !File.Exists(Local(f))))
                       .ToList();
        // По кнопке «проверить файлы» память о прошлых сверках не в счёт.
        var verified = _forceVerify ? new Dictionary<string, string>() : LoadVerified();
        var fresh = new Dictionary<string, string>();

        var todo = new List<Entry>();
        var differs = new List<Entry>();
        long bytes = 0;

        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            var local = Local(f);
            progress.Report((i, files.Count,
                WillHash(local, f, verified) && f.size > 256L * 1048576
                    ? $"{f.path} — считаю отпечаток, большой архив, до минуты"
                    : f.path));

            if (IsGood(local, f, verified, out var stamp))
            {
                fresh[f.path] = stamp;
                // Хвост от прошлой прерванной загрузки этому файлу уже не нужен.
                TryDelete(local + ".part");
            }
            else if (_patchOnly && !ClientState.IsOurs(f.path, _manifest!.ours) && File.Exists(local))
            {
                // Базовый патч есть, но не наш: молча затирать нельзя — это
                // может быть чужая сборка, дорогая человеку. Решает он.
                differs.Add(f);
            }
            else
            {
                todo.Add(f);
                bytes += f.size;
            }
        }

        // Посторонние архивы ищем, только если в папке уже что-то лежит: в
        // пустой папке под установку искать нечего.
        var foreign = Directory.Exists(Path.Combine(_root!, "Data"))
            ? ClientState.ForeignArchives(_root!, all.Select(f => f.path))
            : new List<ClientState.Extra>();

        SaveVerified(fresh);
        return new Plan(todo, bytes, differs, foreign);
    }

    /// <summary>Придётся ли считать хеш: размер совпал, а в памяти прошлых сверок файла нет.</summary>
    private static bool WillHash(string local, Entry f, IReadOnlyDictionary<string, string> verified)
    {
        var info = new FileInfo(local);
        if (!info.Exists || info.Length != f.size || string.IsNullOrEmpty(f.sha256)) return false;
        return !(verified.TryGetValue(f.path, out var known)
                 && known == $"{info.Length}|{info.LastWriteTimeUtc.Ticks}|{f.sha256}");
    }

    private long LocalSize(Entry f)
    {
        try { return new FileInfo(Local(f)).Length; }
        catch { return 0; }
    }

    // --- чужой клиент: решения -----------------------------------------------

    /// <summary>
    /// «Продолжить как есть» запоминаем, иначе окно выскакивало бы при каждом
    /// запуске. Но запоминаем именно этот набор отличий: появился новый чужой
    /// архив — спросим заново. Кнопка «Проверить файлы» спрашивает всегда.
    /// </summary>
    private string AsIsPath => Path.Combine(_root!, "murlo-launcher.asis");

    private static string Signature(Plan plan) => string.Join("|",
        plan.Differs.Select(f => f.path)
            .Concat(plan.Foreign.Select(x => $"{x.Path}:{x.Size}"))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

    private bool AcceptedAsIs(string sig)
    {
        try { return File.Exists(AsIsPath) && File.ReadAllText(AsIsPath).Trim() == sig; }
        catch { return false; }
    }

    private void RememberAsIs(string sig)
    {
        try { File.WriteAllText(AsIsPath, sig); } catch { }
    }

    private void ForgetAsIs() => TryDelete(AsIsPath);

    /// <summary>
    /// Откладывает посторонние архивы. Возвращает текст ошибки или null.
    /// Работает не в потоке интерфейса: переименование мгновенное, но
    /// антивирус может придержать файл на секунду.
    /// </summary>
    private string? MoveForeignAside(IEnumerable<ClientState.Extra> foreign)
    {
        foreach (var x in foreign)
        {
            try
            {
                ClientState.MoveAside(_root!, x.Path, "нет в клиенте MurloVille");
            }
            catch (Exception ex)
            {
                return $"{x.Path}: {Short(ex.Message)}";
            }
        }
        return null;
    }

    /// <summary>
    /// Новая папка под наш клиент. Пустая — ставим прямо в неё. Не пустая —
    /// предлагаем подпапку MurloVille: разложить восемнадцать гигабайт поверх
    /// чьих-то «Загрузок» — не то, чего человек ждал.
    /// </summary>
    private string? PickNewFolder()
    {
        string? start = null;
        try { start = Path.GetDirectoryName(_root!.TrimEnd('\\')); } catch { }

        while (true)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Новая папка для клиента MurloVille — лучше пустая",
            };
            if (!string.IsNullOrEmpty(start) && Directory.Exists(start)) dlg.InitialDirectory = start;
            if (dlg.ShowDialog(this) != true) return null;
            var target = dlg.FolderName;

            if (ClientFinder.IsClient(target) || ClientFinder.IsModernClient(target) ||
                string.Equals(target.TrimEnd('\\'), _root!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this,
                    "Здесь уже лежит игра. Для нашего клиента нужна другая, лучше пустая папка — " +
                    "её можно создать прямо в окне выбора.",
                    "MurloVille", MessageBoxButton.OK, MessageBoxImage.Information);
                start = target;
                continue;
            }

            bool empty;
            try { empty = !Directory.Exists(target) || Directory.GetFileSystemEntries(target).Length == 0; }
            catch { empty = false; }
            if (empty) return target;

            var sub = Path.Combine(target, "MurloVille");
            try
            {
                for (var n = 2; Directory.Exists(sub) && Directory.GetFileSystemEntries(sub).Length > 0; n++)
                    sub = Path.Combine(target, $"MurloVille {n}");
            }
            catch { }

            var answer = MessageBox.Show(this,
                $"Папка не пустая. Поставить клиент в «{sub}»?" + Environment.NewLine + Environment.NewLine +
                "«Нет» — ставить прямо сюда, рядом с тем, что уже лежит.",
                "MurloVille", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
            {
                try { Directory.CreateDirectory(sub); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Не смог создать папку:" + Environment.NewLine + ex.Message,
                        "MurloVille", MessageBoxButton.OK, MessageBoxImage.Warning);
                    continue;
                }
                return sub;
            }
            if (answer == MessageBoxResult.No) return target;
            start = target;
        }
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
        // Прямой адрес из манифеста — главный путь: с 2026-09-06 весь клиент
        // лежит в одном хранилище S3, ссылки постоянные и с докачкой.
        if (!string.IsNullOrEmpty(f.url))
            return f.url!;

        if (!string.IsNullOrEmpty(_manifest?.baseUrl) && f.src == "s3")
            return $"{_manifest!.baseUrl!.TrimEnd('/')}/{EncodePath(f.path)}";

        if (f.src != "yandex")
            return $"{Base}/files/{f.path}";

        var url = $"{YandexApi}?public_key={Uri.EscapeDataString(_manifest!.publicKey!)}" +
                  $"&path={Uri.EscapeDataString(f.remote ?? "/" + f.path)}";
        var json = await Http.GetStringAsync(url, token);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("href").GetString()
               ?? throw new IOException("хранилище не дало ссылку");
    }

    /// <summary>Путь файла в адрес: каждый кусок кодируется отдельно, косые остаются.</summary>
    private static string EncodePath(string path) =>
        string.Join("/", path.Replace(Path.DirectorySeparatorChar, '/').Split('/').Select(Uri.EscapeDataString));

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
    private async Task Download(Entry f, long totalBytes, DateTime started)
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
                have = await Pull(f, part, have, totalBytes, started);
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

        // Не наш базовый архив, который человек разрешил заменить: прежний
        // откладываем в сторону, а не затираем. Именно сейчас, когда наш уже
        // скачан и проверен, — оборвись загрузка раньше, игра осталась бы
        // вовсе без файла.
        if (_aside.Contains(f.path) && File.Exists(local))
            ClientState.MoveAside(_root!, f.path, "заменён архивом MurloVille");

        ReplaceFile(part, local);
    }

    /// <summary>
    /// Сколько байт уже легло на диск за эту загрузку. Общий счётчик на все
    /// потоки: при нескольких файлах разом «сколько осталось» иначе не
    /// посчитать.
    /// </summary>
    private long _bytesDone;

    private void ShowProgress(string path, long totalBytes, DateTime started)
    {
        var done = Interlocked.Read(ref _bytesDone);
        var show = () =>
        {
            Bar.Value = totalBytes > 0 ? Math.Min(100, (double)done / totalBytes * 100) : 0;
            var secs = (DateTime.UtcNow - started).TotalSeconds;
            var speed = secs > 1 ? done / secs : 0;
            DetailText.Text = speed > 0
                ? $"{path} — {Gb(done)} из {Gb(totalBytes)}, {speed / 1048576:0.#} МБ/с, осталось {Remaining(totalBytes - done, speed)}"
                : path;
        };
        if (Dispatcher.CheckAccess()) show();
        else Dispatcher.BeginInvoke(show);
    }

    /// <summary>Одна попытка: с текущего места до конца файла или до обрыва.</summary>
    private async Task<long> Pull(Entry f, string part, long have, long totalBytes, DateTime started)
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

        // Кусок поменьше, когда скорость ограничена: с мегабайтным буфером
        // ограничитель дёргал бы загрузку рывками по секунде.
        var chunk = _options.SpeedMbps > 0 ? 1 << 16 : 1 << 20;
        var buf = new byte[chunk];
        var lastShown = DateTime.UtcNow;
        while (true)
        {
            var read = await ReadOrStall(net, buf);
            if (read <= 0) break;
            await file.WriteAsync(buf.AsMemory(0, read));
            have += read;
            Interlocked.Add(ref _bytesDone, read);

            var wait = _throttle.Take(read);
            if (wait > 0) await Task.Delay(wait);

            if ((DateTime.UtcNow - lastShown).TotalMilliseconds > 250)
            {
                lastShown = DateTime.UtcNow;
                ShowProgress(f.path, totalBytes, started);
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

        if (_mode == Mode.Elevate)
        {
            Elevate();
            return;
        }

        if (_root is null)
        {
            Browse_Click(sender, e);
            return;
        }

        // В игру с недокачанным клиентом пускать нельзя. Человек попадёт в мир
        // со старыми таблицами, увидит «вы не можете войти» или чужие названия
        // предметов и решит, что сломан сервер. Поэтому сначала догоняем.
        if (_pending > 0)
        {
            Say("Клиент ещё не обновлён — играть рано.",
                $"Осталось скачать {Gb(_pending)}. Догоняю прямо сейчас.");
            await Sync();
            return;
        }

        FixRealmlist();
        VideoFix.Apply(_root);
        ClearWdbCache();
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(_root, "Wow.exe"))
            {
                WorkingDirectory = _root,
                UseShellExecute = true,
            });
            // Раньше здесь стоял Close(), и лаунчер уходил вместе с окном.
            // Теперь он прячется и остаётся сторожить просьбы из игры —
            // «открой страницу товара». Показать её может только он: в
            // клиенте 3.3.5 браузера нет вовсе, а LaunchURL доступна лишь на
            // экране входа. Для игрока ничего не меняется: окно исчезает, как
            // и раньше, а сам лаунчер уходит вместе с игрой.
            Hide();
            ShopWatch.Start(() => _root, GameRunning);
            WatchGameExit();
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

    internal static string Gb(long bytes) => bytes >= 1073741824
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
