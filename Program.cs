using System.IO;
using System.Windows;
using Path = System.IO.Path;

namespace VocalTuner;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--selftest")
            return SelfTest.Run(args[1]);
        if (args.Length >= 2 && args[0] == "--mictest")
            return SelfTest.MicTest(args[1]);
        if (args.Length >= 2 && args[0] == "--pianotest")
            return SelfTest.PianoTest(args[1]);
        if (args.Length >= 2 && args[0] == "--outtest")
            return SelfTest.OutTest(args[1], args.Length > 2 ? args[2] : null);
        if (args.Length >= 2 && args[0] == "--pianolive")
            return SelfTest.PianoLive(args[1], args.Length > 2 ? args[2] : null);
        if (args.Length >= 2 && args[0] == "--livetest")
            return SelfTest.LiveTest(args[1]);

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        // Safety net: log unexpected UI errors and keep running instead of vanishing.
        app.DispatcherUnhandledException += (_, e) =>
        {
            try
            {
                Directory.CreateDirectory(AudioEngine.DataDir);
                File.AppendAllText(Path.Combine(AudioEngine.DataDir, "error.log"), $"[{DateTime.Now:s}] {e.Exception}\n\n");
            }
            catch { }
            e.Handled = true;
            MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message + "\n\nDetails were saved to error.log in %LOCALAPPDATA%\\VocalTuner.",
                "Vocal Tuner", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        app.Resources.MergedDictionaries.Add(
            (ResourceDictionary)Application.LoadComponent(new Uri("/VocalTuner;component/Styles.xaml", UriKind.Relative)));
        var settings = AppSettings.Load();
        Theme.Apply(settings.Dark);
        return app.Run(new MainWindow(settings));
    }
}
