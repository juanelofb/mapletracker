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
    internal static bool IsPreviewBuild => Resources.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion.Split('+')[0].Contains('-') == true;
    private static readonly Lazy<CatalogSnapshot> Snapshot = new(() =>
    {
        using var stream = new MemoryStream(Read("world-catalog.json"));
        return CatalogSnapshot.Read(stream);
    });
    internal static CatalogSnapshot BundledCatalog => Snapshot.Value;
    private static readonly Lazy<GameCatalog> Catalog = new(() => BundledCatalog.BuildCatalog());
    public static GameCatalog WorldCatalog => Catalog.Value;
    private static readonly Lazy<MesoGlyphReader> MesoModel = new(() => MesoGlyphReader.Load(Read("meso-glyphs.json")));
    internal static MesoGlyphReader MesoGlyphs => MesoModel.Value;
    public static string CacheDirectory => Path.Combine(SettingsStore.DataDirectory, "cache", Resources.GetName().Version!.ToString(3));
    public static string ExperienceTablePath => Extract("exp-table.json", "exp-table.json");
    public static string OcrDataDirectory => Path.GetDirectoryName(Extract("eng.traineddata", "tessdata/eng.traineddata"))!;
    public static bool HasExperimentalChatModel => Resources.GetManifestResourceNames().Contains("MapleTracker.Assets.maple_chat_int.traineddata");
    public static string ExperimentalChatDataDirectory => Path.GetDirectoryName(Extract("maple_chat_int.traineddata", "tessdata/maple_chat_int.traineddata"))!;
    public static bool HasExperimentalFallbackModel => Resources.GetManifestResourceNames().Contains("MapleTracker.Assets.maple_chat_fallback.traineddata");
    public static string ExperimentalFallbackDataDirectory => Path.GetDirectoryName(Extract("maple_chat_fallback.traineddata", "tessdata-fallback/maple_chat_int.traineddata"))!;
    public static string ReadGuide() => ReadText("README.md");
    public static string ReadNotices() => ReadText("THIRD-PARTY-NOTICES.txt") + "\n\n" + ReadText("OSMS-LICENSE.txt") +
        "\nCatalog changes: reduced to item/map/monster names, categories and unique map monster IDs. No drop tables are supplied.\n" + (HasExperimentalChatModel
        ? "\n\nOptional MapleStory chat model: fine-tuned from Tesseract tessdata_best/eng (https://github.com/tesseract-ocr/tessdata_best).\n" + ReadText("Experimental-OCR-LICENSE.txt") : "");

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
            if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("MapleTracker requires 64-bit Windows.");
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
