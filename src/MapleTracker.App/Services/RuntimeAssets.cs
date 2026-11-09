using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MapleTracker.App.Services;

public static class RuntimeAssets
{
    private static readonly Assembly Resources = typeof(RuntimeAssets).Assembly;
    private static readonly object Gate = new();
    private static bool nativeReady;
    public static string CacheDirectory => Path.Combine(SettingsStore.DataDirectory, "cache", Resources.GetName().Version!.ToString(3));
    public static string ExperienceTablePath => Extract("exp-table.json", "exp-table.json");
    public static string OcrDataDirectory => Path.GetDirectoryName(Extract("eng.traineddata", "tessdata/eng.traineddata"))!;
    public static string ReadGuide() => ReadText("README.md");
    public static string ReadNotices() => ReadText("THIRD-PARTY-NOTICES.txt");

    private static byte[] Read(string name)
    {
        using var resource = Resources.GetManifestResourceStream("MapleTracker.Assets." + name)
            ?? throw new InvalidOperationException("A bundled application asset is missing: " + name);
        using var memory = new MemoryStream(); resource.CopyTo(memory); return memory.ToArray();
    }
    private static string ReadText(string name) => Encoding.UTF8.GetString(Read(name));

    private static string Extract(string resource, string relativePath)
    {
        lock (Gate)
        {
            var content = Read(resource);
            var path = Path.Combine(CacheDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path) && CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(path)), SHA256.HashData(content))) return path;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, content); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return path;
        }
    }

    public static void PrepareNativeOcr()
    {
        lock (Gate)
        {
            if (nativeReady) return;
            if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("MapleTracker 1.0 requires 64-bit Windows.");
            foreach (var file in new[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "leptonica-1.82.0.dll", "tesseract50.dll" })
            {
                var path = Extract("native." + file, "x64/" + file);
                // Keep these handles loaded for process lifetime. Dependencies are loaded first.
                NativeLibrary.Load(path);
            }
            Tesseract.TesseractEnviornment.CustomSearchPath = CacheDirectory;
            nativeReady = true;
        }
    }
}
