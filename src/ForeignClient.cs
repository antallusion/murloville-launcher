using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace MurloLauncher;

/// <summary>
/// Окно «эта папка — не наш клиент».
///
/// Появилось после форума t/64 (27.09.2026): игрок указал лаунчеру клиент
/// другого сервера со своими HD-моделями, лаунчер честно положил сверху наши
/// патчи — и из всех рас остались видны только дренеи, ворген и гоблин.
/// Лаунчер не ошибся, он просто не знал, что основа под нашими патчами у нас
/// своя. Теперь он сверяет её и, если она чужая, спрашивает, что делать, —
/// до начала загрузки, а не после.
///
/// Решать должен человек: папка может быть ему дорога (прежний сервер, свои
/// аддоны), а может быть мусором из торрента. Поэтому три пути, и первый —
/// рекомендуемый: поставить наш клиент рядом и ничего здесь не трогать.
/// </summary>
internal static class ForeignClient
{
    public enum Choice { Cancel, NewFolder, CleanUp, AsIs }

    /// <summary>Базовый архив, который есть, но не наш.</summary>
    public sealed record Differ(string Path, long Here, long Ours);

    public static Choice Ask(Window owner, IReadOnlyList<Differ> differs,
                             IReadOnlyList<ClientState.Extra> foreign,
                             long newFolderBytes, long cleanUpBytes, long asIsBytes)
    {
        var res = Application.Current.Resources;
        var gold = (Brush)res["Gold"];
        var text = (Brush)res["Text"];
        var muted = (Brush)res["TextMuted"];

        var body = new StackPanel { Margin = new Thickness(26, 22, 26, 24) };

        body.Children.Add(new TextBlock
        {
            Text = "Эта папка не совпадает с нашим клиентом",
            Foreground = gold,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 10),
        });

        var intro = new TextBlock
        {
            Foreground = text,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
            Margin = new Thickness(0, 0, 0, 14),
        };
        intro.Inlines.Add(new Run("Играть на MurloVille можно только нашим клиентом. ") { FontWeight = FontWeights.SemiBold });
        intro.Inlines.Add(new Run(
            "Наши таблицы рас собраны под наши модели персонажей: поверх клиента другого сервера " +
            "персонажи становятся невидимыми, а экран выбора — тёмным. " +
            "Чистый оригинальный 3.3.5a подойдёт — лаунчер доведёт его до нашего."));
        body.Children.Add(intro);

        // Что именно не так — списком. Человеку, который помнит, что ставил
        // «HD-модели с такого-то сервера», это сразу всё объясняет.
        var findings = new StackPanel();
        if (differs.Count > 0)
        {
            findings.Children.Add(Heading("Не наши базовые архивы:", muted));
            foreach (var d in differs)
            {
                var what = d.Here == d.Ours
                    ? "того же размера, но другое содержимое"
                    : $"здесь {MainWindow.Gb(d.Here)}, у нас {MainWindow.Gb(d.Ours)}";
                findings.Children.Add(Item($"{d.Path.Replace('/', '\\')} — {what}", text));
            }
        }
        if (foreign.Count > 0)
        {
            findings.Children.Add(Heading("Лишние архивы — в нашем клиенте их нет:", muted,
                                          top: differs.Count > 0 ? 10 : 0));
            foreach (var f in foreign)
                findings.Children.Add(Item($"{f.Path.Replace('/', '\\')} · {MainWindow.Gb(f.Size)}", text));
        }
        body.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xE0, 0x7A, 0x5F)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 18),
            Child = new ScrollViewer
            {
                MaxHeight = 130,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = findings,
            },
        });

        var choice = Choice.Cancel;
        Window? win = null;

        Button Option(string style, string title, string sub, Choice pick, double opacity = 1)
        {
            var content = new StackPanel { Margin = new Thickness(14, 9, 14, 10) };
            content.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Text = sub,
                FontSize = 11.5,
                FontWeight = FontWeights.Normal,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.85,
                Margin = new Thickness(0, 3, 0, 0),
            });
            var b = new Button
            {
                Style = (Style)res[style],
                Content = content,
                Margin = new Thickness(0, 0, 0, 10),
                Opacity = opacity,
            };
            // Внутри кнопки панель, а не строка, и без явного имени экранный
            // диктор и UI Automation видят безымянную кнопку.
            System.Windows.Automation.AutomationProperties.SetName(b, title);
            b.Click += (_, _) =>
            {
                choice = pick;
                win!.DialogResult = true;
            };
            return b;
        }

        body.Children.Add(Option("GoldButton",
            "ПОСТАВИТЬ НАШ КЛИЕНТ В НОВУЮ ПАПКУ (РЕКОМЕНДУЕМ)",
            $"Скачать {MainWindow.Gb(newFolderBytes)}. Эта папка останется как была — "
            + "на прежнем сервере ей можно играть и дальше.",
            Choice.NewFolder));

        var moves = new List<string>();
        if (foreign.Count > 0)
            moves.Add($"лишние архивы ({foreign.Count}) переедут в папку «{ClientState.AsideFolder}» внутри игры");
        if (differs.Count > 0)
            moves.Add(foreign.Count > 0
                ? "не наши базовые заменю нашими, прежние отложу туда же"
                : $"не наши базовые заменю нашими, прежние отложу в папку «{ClientState.AsideFolder}» внутри игры");
        body.Children.Add(Option("GhostButton",
            foreign.Count > 0 ? "ОТЛОЖИТЬ ЧУЖИЕ ФАЙЛЫ И ПРОДОЛЖИТЬ" : "ЗАМЕНИТЬ НА НАШИ И ПРОДОЛЖИТЬ",
            Capitalize(string.Join("; ", moves)) + $". Ничего не удаляю. Скачать: {MainWindow.Gb(cleanUpBytes)}.",
            Choice.CleanUp));

        body.Children.Add(Option("GhostButton",
            "ПРОДОЛЖИТЬ КАК ЕСТЬ",
            (asIsBytes > 0 ? $"Докачаю только наши патчи и недостающее ({MainWindow.Gb(asIsBytes)}), чужое оставлю. "
                           : "Ничего не трогаю. ")
            + "Скорее всего, персонажи будут невидимы — не рекомендуем.",
            Choice.AsIs, opacity: 0.75));

        body.Children.Add(new TextBlock
        {
            Text = "Закрыть окно — ничего не менять: обновление подождёт решения.",
            Foreground = muted,
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });

        win = new Window
        {
            Title = "MurloVille — это не наш клиент",
            Width = 640,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Background = (Brush)res["Ink"],
            Content = body,
        };
        win.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) win.DialogResult = false;
        };

        return win.ShowDialog() == true ? choice : Choice.Cancel;
    }

    private static TextBlock Heading(string s, Brush brush, double top = 0) => new()
    {
        Text = s,
        Foreground = brush,
        FontSize = 11.5,
        Margin = new Thickness(0, top, 0, 4),
    };

    private static TextBlock Item(string s, Brush brush) => new()
    {
        Text = "•  " + s,
        Foreground = brush,
        FontSize = 12.5,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 1, 0, 1),
    };

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];
}
