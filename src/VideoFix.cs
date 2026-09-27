using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace MurloLauncher;

/// <summary>
/// Починка настроек картинки перед запуском игры.
///
/// Клиент 3.3.5 старше нынешних мониторов. Если в Config.wtf записана частота,
/// которой у выбранного разрешения нет — скажем, 60 Гц при панели на 144, а
/// такие настройки достаются от прежнего компьютера или от чужого клиента, —
/// то в полноэкранном режиме он просит у Windows несуществующий режим, та
/// отказывает, и человек видит чёрный экран. Ровно то же бывает с
/// разрешением больше монитора.
///
/// Поэтому перед запуском сверяем записанное с тем, что монитор умеет на
/// самом деле, и правим только сломанное. Выбор игрока не трогаем: если он
/// сам поставил окно или разрешение поменьше — так и останется.
/// </summary>
internal static class VideoFix
{
    public static void Apply(string root)
    {
        try
        {
            var modes = Modes();
            if (modes.Count == 0) return;
            var (nativeW, nativeH, nativeHz) = Current();

            var path = Path.Combine(root, "WTF", "Config.wtf");
            var cfg = Read(path);

            // Разрешение: если его нет или монитор такого не умеет — родное.
            if (!cfg.TryGetValue("gxResolution", out var res) || !ParseRes(res, out var w, out var h)
                || !modes.ContainsKey((w, h)))
            {
                w = nativeW; h = nativeH;
                cfg["gxResolution"] = $"{w}x{h}";
            }

            // Частота. Мало того, что она должна быть из списка режимов этого
            // разрешения — она ещё не должна быть ниже той, на которой сейчас
            // работает рабочий стол. Иначе игра в полный экран заставит
            // монитор переключиться (144 -> 60), а на этой смене режима он у
            // многих и гаснет. Занижать частоту незачем: ни один игрок не
            // просит 60 Гц на панели, которая тянет больше.
            var rates = modes[(w, h)];
            var hz = 0;
            var haveHz = cfg.TryGetValue("gxRefresh", out var hzText) && int.TryParse(hzText, out hz);
            if (!haveHz || !rates.Contains(hz) || (rates.Contains(nativeHz) && hz < nativeHz))
            {
                cfg["gxRefresh"] = (rates.Contains(nativeHz) ? nativeHz : Best(rates)).ToString();
            }

            // Первый запуск: окно во весь экран. Смены режима нет вовсе,
            // поэтому и чернеть нечему, а выглядит как полный экран.
            if (!cfg.ContainsKey("gxWindow")) cfg["gxWindow"] = "1";
            if (!cfg.ContainsKey("gxMaximize")) cfg["gxMaximize"] = "1";

            Write(path, cfg);
            AllowHighDpi(Path.Combine(root, "Wow.exe"));
        }
        catch
        {
            // Не вышло — не беда: игра запустится с тем, что было.
        }
    }

    /// <summary>Windows не должна растягивать окно за клиента: на мониторе с
    /// масштабом 150–175% это даёт мыло, а в полный экран — чёрный кадр.</summary>
    private static void AllowHighDpi(string exe)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers");
            if (key?.GetValue(exe) is null) key?.SetValue(exe, "~ HIGHDPIAWARE", RegistryValueKind.String);
        }
        catch { }
    }

    private static int Best(HashSet<int> rates)
    {
        var best = 60;
        foreach (var r in rates) if (r > best) best = r;
        return best;
    }

    private static bool ParseRes(string value, out int w, out int h)
    {
        w = h = 0;
        var parts = value.Split('x');
        return parts.Length == 2 && int.TryParse(parts[0], out w) && int.TryParse(parts[1], out h);
    }

    // --- Config.wtf ----------------------------------------------------------

    private static Dictionary<string, string> Read(string path)
    {
        var cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return cfg;
        foreach (var line in File.ReadAllLines(path))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, "^SET\\s+(\\S+)\\s+\"([^\"]*)\"");
            if (m.Success) cfg[m.Groups[1].Value] = m.Groups[2].Value;
        }
        return cfg;
    }

    private static void Write(string path, Dictionary<string, string> cfg)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder();
        foreach (var pair in cfg) sb.Append("SET ").Append(pair.Key).Append(" \"").Append(pair.Value).AppendLine("\"");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    // --- что умеет монитор ---------------------------------------------------

    /// <summary>Все режимы основного монитора: разрешение -> частоты.</summary>
    private static Dictionary<(int, int), HashSet<int>> Modes()
    {
        var list = new Dictionary<(int, int), HashSet<int>>();
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        for (var i = 0; EnumDisplaySettings(null, i, ref dm); i++)
        {
            if (dm.dmBitsPerPel < 32) continue;
            var key = (dm.dmPelsWidth, dm.dmPelsHeight);
            if (!list.TryGetValue(key, out var rates)) list[key] = rates = new HashSet<int>();
            rates.Add(dm.dmDisplayFrequency);
        }
        return list;
    }

    /// <summary>Нынешний режим основного монитора.</summary>
    private static (int, int, int) Current()
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        return EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm)
            ? (dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency)
            : (1920, 1080, 60);
    }

    private const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public int dmPanningWidth, dmPanningHeight;
    }
}
