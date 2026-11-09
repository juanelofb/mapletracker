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
            if (e.Args.Length == 2 && e.Args[0] == "--self-test")
            {
                var output = Path.GetFullPath(e.Args[1]); Directory.CreateDirectory(output);
                AppContext.SetData("MapleTracker.DataDirectory", Path.Combine(output, "app-data"));
                var table = ExperienceTable.Load(RuntimeAssets.ExperienceTablePath);
                using var reader = new OcrReader(table);
                var modules = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
                    .Where(m => m.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase) ||
                        new[] { "tesseract50.dll", "leptonica-1.82.0.dll", "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" }.Contains(m.ModuleName, StringComparer.OrdinalIgnoreCase))
                    .Select(m => new { Name = m.ModuleName, Path = m.FileName }).ToArray();
                File.WriteAllText(Path.Combine(output, "self-test.json"), System.Text.Json.JsonSerializer.Serialize(new
                {
                    Version = typeof(App).Assembly.GetName().Version!.ToString(3),
                    Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    OcrInitialized = true, ExperienceTo100 = table.Absolute(100, 0),
                    ModelBytes = new FileInfo(Path.Combine(RuntimeAssets.OcrDataDirectory, "eng.traineddata")).Length,
                    GuideCharacters = RuntimeAssets.ReadGuide().Length, Modules = modules
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0); return;
            }
            if (e.Args.Length is 3 or 4 && (e.Args[0] == "--verify-screenshot" || e.Args[0] == "--probe-screenshot"))
            {
                AppContext.SetData("MapleTracker.DataDirectory", Path.Combine(Path.GetFullPath(e.Args[2]), "app-data"));
                var table = ExperienceTable.Load(RuntimeAssets.ExperienceTablePath);
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
            try { Directory.CreateDirectory(SettingsStore.DataDirectory); File.WriteAllText(Path.Combine(SettingsStore.DataDirectory, "startup-error.log"), ex.ToString()); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            if (!e.Args.Any(x => x.EndsWith("screenshot") || x == "--self-test")) MessageBox.Show(ex.Message, "MapleTracker could not start");
            Shutdown(1);
        }
    }
}
