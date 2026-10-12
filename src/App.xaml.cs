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

        // Любая ошибка окна раньше молча закрывала лаунчер (так было с кнопкой
        // «Настройки»): человек видел только исчезнувшее окно. Теперь ошибка
        // пишется в журнал рядом с настройками и показывается, а окно остаётся.
        DispatcherUnhandledException += (_, args) =>
        {
            Report(args.Exception);
            MessageBox.Show("Что-то пошло не так, но лаунчер продолжает работать." + Environment.NewLine +
                            args.Exception.Message + Environment.NewLine + Environment.NewLine +
                            "Подробности записаны в " + CrashLog,
                "MurloVille", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Report(ex);
        };

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

    private static string CrashLog => System.IO.Path.Combine(Setup.DataDir, "crash.log");

    private static void Report(Exception ex)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Setup.DataDir);
            System.IO.File.AppendAllText(CrashLog,
                DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture) + " — " +
                ex.GetType().FullName + ": " + ex.Message + Environment.NewLine + ex.StackTrace +
                Environment.NewLine + Environment.NewLine);
        }
        catch { }
    }
}
