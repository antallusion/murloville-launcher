using System;
using System.Linq;
using System.Windows;

namespace MurloLauncher;

/// <summary>
/// Точка входа. Лаунчер умеет три вещи помимо обычного запуска: поставить себя
/// на компьютер, стартовать свёрнутым вместе с Windows и удалиться.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool Has(string flag) => e.Args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

        // Удаление вызывается из «Установленных приложений» и окна не открывает.
        if (Has("--uninstall"))
        {
            Setup.Uninstall();
            Shutdown();
            return;
        }

        // Тихая установка без вопросов — для тех, кто разворачивает лаунчер
        // скриптом, и для проверки самой установки.
        if (Has("--install"))
        {
            try
            {
                Setup.Install();
                Setup.AutoStart = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не смог установить лаунчер:" + Environment.NewLine + ex.Message,
                    "MurloVille", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Shutdown();
            return;
        }

        var window = new MainWindow(autostart: Has("--autostart"));
        MainWindow = window;
        window.Show();
    }
}
