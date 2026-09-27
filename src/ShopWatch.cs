using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MurloLauncher;

/// <summary>
/// Сторож просьб из игры: «открой мне страницу товара».
///
/// Как узнаём, кто играет. Игра пишет последний введённый логин в
/// WTF\Config.wtf (SET accountName). Лаунчер этот файл и так читает и правит
/// (см. VideoFix), поэтому лишнего знания не заводим. Если логин не сохранён
/// — «запоминать имя учётной записи» выключено, — просить нам не за кого, и
/// сторож просто молчит: в игре на этот случай остаётся окно с кодом.
///
/// Спрашиваем только пока игра запущена: в остальное время дёргать сервер
/// незачем.
/// </summary>
public static class ShopWatch
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private const string Api = "https://play.murloville.ru/api/openpage";
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(3);
    private static CancellationTokenSource? _cts;

    public static void Start(Func<string?> gameRoot, Func<bool> gameRunning)
    {
        Stop();
        _cts = new CancellationTokenSource();
        _ = Loop(gameRoot, gameRunning, _cts.Token);
    }

    public static void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private static async Task Loop(Func<string?> gameRoot, Func<bool> gameRunning, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (gameRunning())
                {
                    var account = AccountName(gameRoot());
                    if (!string.IsNullOrEmpty(account))
                    {
                        var page = await Ask(account!, ct);
                        if (!string.IsNullOrEmpty(page))
                            ShopPage.Show(page!);
                    }
                }
            }
            catch
            {
                // Сеть отвалилась или сервер молчит — не наше дело шуметь:
                // в игре у человека осталось окно со ссылкой и кодом.
            }

            try { await Task.Delay(Every, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static async Task<string?> Ask(string account, CancellationToken ct)
    {
        var url = $"{Api}?account={Uri.EscapeDataString(account)}";
        var json = await Http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("page", out var p) || p.ValueKind != JsonValueKind.String)
            return null;
        return p.GetString();
    }

    /// <summary>Последний логин из WTF\Config.wtf, если игра его запомнила.</summary>
    private static string? AccountName(string? root)
    {
        if (string.IsNullOrEmpty(root)) return null;
        var path = Path.Combine(root!, "WTF", "Config.wtf");
        if (!File.Exists(path)) return null;
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var m = Regex.Match(line, "^SET\\s+accountName\\s+\"([^\"]*)\"",
                                    RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var name = m.Groups[1].Value.Trim();
                    // Только латиница и цифры: ровно то, что принимает сервер.
                    return Regex.IsMatch(name, "^[A-Za-z0-9]{3,16}$") ? name : null;
                }
            }
        }
        catch (IOException)
        {
            // Игра держит файл открытым — попробуем в следующий заход.
        }
        return null;
    }
}
