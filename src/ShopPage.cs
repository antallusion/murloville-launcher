using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;

namespace MurloLauncher;

/// <summary>
/// Окно со страницей магазина поверх игры.
///
/// Зачем оно здесь, а не в самой игре. Владелец просил показывать страницу
/// товара модальным окном прямо в игре. В клиенте 3.3.5 это невозможно: в
/// Wow.exe нет ни libcef, ни WebView, ни IWebBrowser, ни mshtml, а функция
/// LaunchURL зарегистрирована только на экране входа (лежит рядом с
/// AcceptEULA и EnterWorld) и из игры недоступна. Вставить туда браузер можно
/// лишь своей библиотекой в процессе клиента — это недели работы и +150 МБ к
/// загрузке. Лаунчер же и так работает рядом, он на .NET, и WebView у него
/// появляется одним пакетом.
///
/// Поэтому игра лишь просит («#shop SITE crystals»), сервер держит пометку, а
/// показывает страницу это окно — без рамки, по центру, поверх игры.
/// </summary>
public sealed class ShopPage : Window
{
    // Ссылки живут здесь, а не приходят с сервера. Сервер отдаёт только ключ
    // страницы: иначе тот, кто дотянулся бы до игровой базы, открыл бы игроку
    // чужой сайт его же лаунчером — и выглядело бы это как наше окно.
    private static readonly System.Collections.Generic.Dictionary<string, string> Pages = new()
    {
        ["crystals"] = "https://murloville.ru/crystals",
        ["premium"] = "https://murloville.ru/premium",
    };

    private static ShopPage? _open;

    private ShopPage(string url)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Width = 1000;
        Height = 760;

        var shell = new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush(Color.FromRgb(7, 9, 15)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(230, 195, 106)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
        };
        var grid = new System.Windows.Controls.Grid();
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(34) });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition());

        var bar = new System.Windows.Controls.DockPanel { Background = Brushes.Transparent };
        // Заголовок заодно служит ручкой: окно без рамки иначе не подвинуть.
        bar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        var title = new System.Windows.Controls.TextBlock
        {
            Text = "  MurloVille — покупка",
            Foreground = new SolidColorBrush(Color.FromRgb(230, 195, 106)),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
        };
        var close = new System.Windows.Controls.Button
        {
            Content = "✕",
            Width = 34,
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        close.Click += (_, _) => Close();
        System.Windows.Controls.DockPanel.SetDock(close, System.Windows.Controls.Dock.Right);
        bar.Children.Add(close);
        bar.Children.Add(title);
        System.Windows.Controls.Grid.SetRow(bar, 0);
        grid.Children.Add(bar);

        var web = new WebView2 { Source = new Uri(url) };
        System.Windows.Controls.Grid.SetRow(web, 1);
        grid.Children.Add(web);

        shell.Child = grid;
        Content = shell;

        // Esc закрывает — как всякое окно в самой игре.
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => { _open = null; web.Dispose(); };
    }

    /// <summary>Показать страницу по ключу. Повторная просьба поднимает уже
    /// открытое окно, а не плодит второе.</summary>
    public static void Show(string page)
    {
        if (!Pages.TryGetValue(page, out var url)) return;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_open != null)
            {
                _open.Activate();
                return;
            }
            _open = new ShopPage(url);
            _open.Show();
            _open.Activate();
        });
    }
}
