using System.Reflection;
using System.Security.Cryptography;

namespace LanFileShare;

public static class AppPaths
{
#if SINGLE_FILE
    public static bool IsSingleFile => true;
#else
    public static bool IsSingleFile => false;
#endif
    public static string DataDirectory { get; private set; } = AppContext.BaseDirectory;
    public static string ResourceDirectory { get; private set; } = AppContext.BaseDirectory;

    public static void Initialize(string? dataDirectory = null)
    {
        if (!IsSingleFile && dataDirectory == null) return;
        DataDirectory = Path.GetFullPath(dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanFileShare-Fixed"));
        Directory.CreateDirectory(DataDirectory);
        if (!IsSingleFile) return;

        var assembly = typeof(AppPaths).Assembly;
        ResourceDirectory = Path.Combine(DataDirectory, "assets", assembly.ManifestModule.ModuleVersionId.ToString("N"));
        Directory.CreateDirectory(ResourceDirectory);
        const string prefix = "LanFileShare.Assets/";
        foreach (var name in assembly.GetManifestResourceNames().Where(x => x.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var relative = name[prefix.Length..].Replace('\\', '/');
            var destination = Path.GetFullPath(Path.Combine(ResourceDirectory, relative));
            if (!FileShareService.IsPathUnderDirectory(ResourceDirectory, destination))
                throw new InvalidDataException("资源文件路径无效。");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = assembly.GetManifestResourceStream(name)!;
            using var memory = new MemoryStream();
            input.CopyTo(memory);
            var bytes = memory.ToArray();
            if (File.Exists(destination) && SHA256.HashData(File.ReadAllBytes(destination)).SequenceEqual(SHA256.HashData(bytes))) continue;
            var staged = destination + ".tmp";
            File.WriteAllBytes(staged, bytes);
            File.Move(staged, destination, true);
        }
        var settings = Path.Combine(DataDirectory, "settings.json");
        if (!File.Exists(settings))
            File.WriteAllText(settings, """
                {"UploadDirectory":"./SharedFiles","Port":8080,"AutoStart":false,"MinimizeToTray":false,"SelectedInterface":""}
                """);
    }
}
