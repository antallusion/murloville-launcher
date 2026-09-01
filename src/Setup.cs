using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace MurloLauncher;

/// <summary>
/// Установка лаунчера на компьютер: ярлыки, автозапуск, запись в «Установленных
/// приложениях» и удаление.
///
/// Ставим в профиль пользователя, а не в Program Files, и это осознанный выбор.
/// Программа пока без подписи: Windows и так покажет предупреждение SmartScreen.
/// Установка в системную папку потребовала бы прав администратора и добавила бы
/// к нему запрос UAC. Два страшных окна подряд — верный способ потерять
/// половину желающих поиграть.
///
/// Побочная выгода: удаление тоже не требует прав и не оставляет мусора в
/// системных папках.
/// </summary>
internal static class Setup
{
    public const string AppName = "MurloVille";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MurloVille";

    /// <summary>Куда ставим: %LOCALAPPDATA%\Programs\MurloVille.</summary>
    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", AppName);

    public static string InstalledExe => Path.Combine(InstallDir, "MurloVille.exe");

    /// <summary>
    /// Настройки лежат отдельно от программы: путь к игре должен пережить и
    /// переустановку лаунчера, и запуск его из другой папки.
    /// </summary>
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    public static string? CurrentExe => Environment.ProcessPath;

    public static bool IsInstalled => File.Exists(InstalledExe);

    /// <summary>Мы сейчас работаем из установленной копии или из папки «Загрузки»?</summary>
    public static bool RunningFromInstall
    {
        get
        {
            var dir = Path.GetDirectoryName(CurrentExe ?? "");
            return dir is not null &&
                   string.Equals(dir.TrimEnd('\\'), InstallDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
    }

    // --- установка -----------------------------------------------------------

    /// <summary>
    /// Копирует себя в профиль, делает ярлыки и запись для удаления.
    /// Возвращает путь к установленной копии.
    /// </summary>
    public static string Install(bool desktopShortcut = true)
    {
        var source = CurrentExe ?? throw new IOException("не смог определить путь к самому себе");

        Directory.CreateDirectory(InstallDir);
        Directory.CreateDirectory(DataDir);

        // Себя же и копируем. Если уже работаем из установленной копии —
        // копировать нечего, но ярлыки и запись обновить всё равно стоит.
        if (!string.Equals(source, InstalledExe, StringComparison.OrdinalIgnoreCase))
            File.Copy(source, InstalledExe, overwrite: true);

        if (desktopShortcut)
            MakeShortcut(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                AppName + ".lnk"));

        var startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName);
        Directory.CreateDirectory(startMenu);
        MakeShortcut(Path.Combine(startMenu, AppName + ".lnk"));

        RegisterForUninstall();
        return InstalledExe;
    }

    /// <summary>
    /// Удаление. Ярлыки, автозапуск и запись убираем сразу, а себя удалить не
    /// можем: файл занят, пока программа работает. Поэтому оставляем короткую
    /// команду, которая подождёт выхода и уберёт папку.
    /// </summary>
    public static void Uninstall()
    {
        Try(() => Registry.CurrentUser.OpenSubKey(RunKey, true)?.DeleteValue(AppName, false));
        Try(() => Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false));

        Try(() => File.Delete(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk")));
        Try(() => Directory.Delete(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName), true));

        // Настройки убираем тоже: путь к игре без лаунчера не нужен, а сама
        // игра остаётся на месте — её мы не трогаем никогда.
        Try(() => Directory.Delete(DataDir, true));

        var dir = InstallDir.TrimEnd('\\');
        Try(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c timeout /t 3 /nobreak >nul & rd /s /q \"{dir}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
        }));
    }

    private static void RegisterForUninstall()
    {
        Try(() =>
        {
            using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
            if (key is null) return;
            key.SetValue("DisplayName", "MurloVille — лаунчер");
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", "MurloVille");
            key.SetValue("DisplayIcon", InstalledExe);
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
            key.SetValue("URLInfoAbout", "https://play.murloville.ru");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            Try(() => key.SetValue("EstimatedSize",
                (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord));
        });
    }

    public static string Version =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    // --- автозапуск ----------------------------------------------------------

    /// <summary>
    /// Запуск вместе с Windows. Стартуем свёрнутым: лаунчер молча догоняет
    /// обновления и не лезет на глаза. Развернуть можно из панели задач.
    /// </summary>
    public static bool AutoStart
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(AppName) is string s && s.Length > 0;
            }
            catch { return false; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (key is null) return;
                if (value)
                {
                    var exe = IsInstalled ? InstalledExe : CurrentExe;
                    if (exe is null) return;
                    key.SetValue(AppName, $"\"{exe}\" --autostart");
                }
                else
                {
                    key.DeleteValue(AppName, false);
                }
            }
            catch { }
        }
    }

    private static void Try(Action a) { try { a(); } catch { } }

    // --- ярлык ---------------------------------------------------------------

    /// <summary>
    /// Ярлык делаем через IShellLink, а не через Windows Script Host: сервер
    /// сценариев отключают групповыми политиками, и на рабочих машинах ярлык
    /// тогда молча не создаётся.
    /// </summary>
    private static void MakeShortcut(string lnkPath)
    {
        Try(() =>
        {
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(InstalledExe);
            link.SetWorkingDirectory(InstallDir);
            link.SetIconLocation(InstalledExe, 0);
            link.SetDescription("Лаунчер MurloVille");
            ((IPersistFile)link).Save(lnkPath, true);
        });
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr fd, int flags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder icon, int cch, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string rel, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder fileName);
    }
}
