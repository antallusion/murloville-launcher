using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MurloLauncher;

/// <summary>
/// Выбор папки, когда на дисках нашлось несколько копий игры.
///
/// Раньше поиск брал первую попавшуюся, а первая — это просто та, что раньше
/// по алфавиту. На машине с двумя клиентами лаунчер молча переключался на
/// чужой и собирался качать туда шестнадцать гигабайт. Решать, какая копия
/// нужна, должен человек: программа этого знать не может.
///
/// Окно собрано кодом, а не разметкой, намеренно: оно показывается редко и
/// состоит из списка и двух кнопок — отдельный файл разметки ради этого
/// заводить незачем.
/// </summary>
internal static class ClientChoice
{
    public static string? Ask(Window owner, IReadOnlyList<string> found)
    {
        var res = Application.Current.Resources;
        var gold = (Brush)res["Gold"];
        var text = (Brush)res["Text"];
        var muted = (Brush)res["TextMuted"];

        var list = new ListBox
        {
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0xE6, 0xC3, 0x6A)),
            BorderThickness = new Thickness(1),
            Foreground = text,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 16),
            Height = 150,
        };

        foreach (var dir in found)
        {
            var mark = ClientFinder.HasOurPatch(dir) ? "  ·  с нашими патчами" : "";
            list.Items.Add(new ListBoxItem
            {
                Content = dir + mark,
                Tag = dir,
                Foreground = text,
                Padding = new Thickness(10, 7, 10, 7),
            });
        }
        list.SelectedIndex = 0;

        var ok = new Button
        {
            Content = "ВЫБРАТЬ",
            Style = (Style)res["GoldButton"],
            Width = 150,
            Height = 42,
            IsDefault = true,
        };
        var cancel = new Button
        {
            Content = "ОТМЕНА",
            Style = (Style)res["GhostButton"],
            Width = 130,
            Height = 42,
            Margin = new Thickness(0, 0, 10, 0),
            IsCancel = true,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var body = new StackPanel { Margin = new Thickness(26) };
        body.Children.Add(new TextBlock
        {
            Text = "Нашёл несколько копий игры",
            Foreground = gold,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 6),
        });
        body.Children.Add(new TextBlock
        {
            Text = "Выбери ту, в которую ставить обновления. Остальные лаунчер не тронет.",
            Foreground = muted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        });
        body.Children.Add(list);
        body.Children.Add(buttons);

        var win = new Window
        {
            Title = "MurloVille",
            Width = 620,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Background = (Brush)res["Ink"],
            Content = body,
        };

        string? chosen = null;
        ok.Click += (_, _) =>
        {
            chosen = (list.SelectedItem as ListBoxItem)?.Tag as string;
            win.DialogResult = true;
        };

        // Двойной щелчок по строке — то же, что «Выбрать»: так этот список
        // ведёт себя везде, и ждать этого будут и здесь.
        list.MouseDoubleClick += (_, _) =>
        {
            chosen = (list.SelectedItem as ListBoxItem)?.Tag as string;
            win.DialogResult = true;
        };

        return win.ShowDialog() == true ? chosen : null;
    }
}
