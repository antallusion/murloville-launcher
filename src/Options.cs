using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MurloLauncher;

/// <summary>
/// Настройки лаунчера, которые человек задаёт сам.
///
/// Лежат рядом с путём к игре, в папке данных пользователя: настройки должны
/// пережить переустановку самого лаунчера. Формат — простые строки «ключ=значение»:
/// файл можно поправить блокнотом, если что-то пошло не так.
/// </summary>
internal sealed class Options
{
    /// <summary>Потолок скорости в мегабайтах в секунду. 0 — без ограничения.</summary>
    public double SpeedMbps { get; set; }

    /// <summary>Сколько файлов тянуть разом. Один канал редко забирает всю полосу.</summary>
    public int Parallel { get; set; } = 3;

    /// <summary>
    /// Сверять ли стоковые архивы Blizzard в уже готовой игре.
    ///
    /// Обычно не надо: у чужой сборки они другие по отпечатку, и сверка
    /// потребует перекачать шестнадцать гигабайт ради файлов, которые и так
    /// работают. Включается руками, когда игра ведёт себя странно и хочется
    /// привести её ровно к нашему состоянию.
    /// </summary>
    public bool CheckStock { get; set; }

    public const int MaxParallel = 4;

    private static string Path_ => System.IO.Path.Combine(Setup.DataDir, "options.txt");

    public static Options Load()
    {
        var o = new Options();
        try
        {
            if (!File.Exists(Path_)) return o;
            foreach (var line in File.ReadAllLines(Path_))
            {
                var cut = line.IndexOf('=');
                if (cut <= 0) continue;
                var key = line[..cut].Trim();
                var value = line[(cut + 1)..].Trim();
                switch (key)
                {
                    case "speedMbps":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                            o.SpeedMbps = Math.Max(0, s);
                        break;
                    case "parallel":
                        if (int.TryParse(value, out var p)) o.Parallel = Math.Clamp(p, 1, MaxParallel);
                        break;
                    case "checkStock":
                        o.CheckStock = value is "1" or "true";
                        break;
                }
            }
        }
        catch { }
        return o;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Setup.DataDir);
            var sb = new StringBuilder();
            sb.Append("speedMbps=").Append(SpeedMbps.ToString("0.###", CultureInfo.InvariantCulture)).AppendLine();
            sb.Append("parallel=").Append(Parallel).AppendLine();
            sb.Append("checkStock=").Append(CheckStock ? '1' : '0').AppendLine();
            File.WriteAllText(Path_, sb.ToString());
        }
        catch { }
    }
}

/// <summary>
/// Ограничитель скорости на всю загрузку сразу.
///
/// Считает «дырявым ведром»: за секунду разрешено пролить столько байт,
/// сколько задал человек. Общий на все потоки — иначе три канала по пять
/// мегабайт дали бы пятнадцать вместо пяти. Нужен затем, что закачка на
/// полную полосу выбивает из сети всех остальных в доме, и человек скорее
/// закроет лаунчер, чем станет разбираться.
/// </summary>
internal sealed class Throttle
{
    private readonly object _lock = new();
    private double _bytesPerSecond;
    private double _allowance;
    private DateTime _last = DateTime.UtcNow;

    public void SetLimit(double megabytesPerSecond)
    {
        lock (_lock)
        {
            _bytesPerSecond = Math.Max(0, megabytesPerSecond) * 1048576.0;
            _allowance = _bytesPerSecond;
            _last = DateTime.UtcNow;
        }
    }

    /// <summary>Сколько миллисекунд подождать, прежде чем брать следующий кусок.</summary>
    public int Take(int bytes)
    {
        lock (_lock)
        {
            if (_bytesPerSecond <= 0) return 0;

            var now = DateTime.UtcNow;
            _allowance += (now - _last).TotalSeconds * _bytesPerSecond;
            _last = now;
            if (_allowance > _bytesPerSecond) _allowance = _bytesPerSecond;   // про запас не копим

            _allowance -= bytes;
            if (_allowance >= 0) return 0;

            var wait = -_allowance / _bytesPerSecond;
            return (int)Math.Min(2000, Math.Ceiling(wait * 1000));
        }
    }
}
