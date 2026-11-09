using System.Drawing;
using MapleTracker.App.Services;

namespace MapleTracker.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                Directory.CreateDirectory(SettingsStore.DataDirectory);
                File.WriteAllText(Path.Combine(SettingsStore.DataDirectory, "runtime-error.log"), args.Exception.ToString());
            }
            catch (IOException) { }
            args.Handled = true;
            MessageBox.Show(args.Exception.Message, "MapleTracker error");
            Shutdown(1);
        };
        base.OnStartup(e);
        try
        {
            if (e.Args.Length is 3 or 4 && (e.Args[0] == "--verify-screenshot" || e.Args[0] == "--probe-screenshot"))
            {
                var table = ExperienceTable.Load(Path.Combine(SettingsStore.DataDirectory, "exp-table.json"));
                using var bitmap = new Bitmap(e.Args[1]);
                using var ocr = new OcrReader(table);
                var verificationSettings = e.Args.Length == 4 ? System.Text.Json.JsonSerializer.Deserialize<TrackerSettings>(File.ReadAllText(e.Args[3])) ?? new() : new TrackerSettings();
                ocr.Read(bitmap, verificationSettings, e.Args[2]);
                if (e.Args[0] == "--probe-screenshot") ocr.Probe(bitmap, verificationSettings, e.Args[2]);
                Shutdown(0); return;
            }
            var store = new SettingsStore();
            var settings = store.Load();
            var controller = new TrackerController(settings);
            var window = new MainWindow(controller, store);
            MainWindow = window; window.Show();
            if (store.Warning != null) MessageBox.Show(window, store.Warning, "MapleTracker settings");
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            File.WriteAllText(Path.Combine(SettingsStore.DataDirectory, "startup-error.log"), ex.ToString());
            if (!e.Args.Any(x => x.EndsWith("screenshot"))) MessageBox.Show(ex.Message, "MapleTracker could not start");
            Shutdown(1);
        }
    }
}
