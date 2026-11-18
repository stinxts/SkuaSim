namespace SkuaSim.Core.Hosting;

/// <summary>
/// Locates the installed Skua/VibeSkua client (API assemblies) and its user data folder (scripts, QuestData.json).
/// This type must not reference any Skua type: it runs before the assembly resolver is registered.
/// </summary>
public sealed class SkuaInstallation
{
    public const string AssembliesEnvVar = "SKUASIM_SKUA_ASSEMBLIES";
    public const string DataEnvVar = "SKUASIM_SKUA_DATA";

    public string AssembliesDirectory { get; }
    public string ApplicationDirectory { get; }
    public string UserDataDirectory { get; }
    public string ScriptsDirectory => Path.Combine(UserDataDirectory, "Scripts");
    public string QuestDataFile => Path.Combine(UserDataDirectory, "QuestData.json");
    public string Flavor { get; }

    private SkuaInstallation(string assembliesDirectory, string applicationDirectory, string userDataDirectory, string flavor)
    {
        AssembliesDirectory = assembliesDirectory;
        ApplicationDirectory = applicationDirectory;
        UserDataDirectory = userDataDirectory;
        Flavor = flavor;
    }

    /// <summary>
    /// Directories probed (in order) when resolving Skua/VibeSkua and WindowsDesktop assemblies at runtime.
    /// </summary>
    public IReadOnlyList<string> ProbeDirectories =>
        new[] { AssembliesDirectory, ApplicationDirectory, FindWindowsDesktopSharedDirectory() ?? "" }
            .Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string? FindWindowsDesktopSharedDirectory()
    {
        try
        {
            var corlibDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            if (string.IsNullOrEmpty(corlibDir))
                return null;
            var version = Path.GetFileName(corlibDir);
            var sharedRoot = Path.GetDirectoryName(Path.GetDirectoryName(corlibDir));
            if (string.IsNullOrEmpty(sharedRoot))
                return null;
            var desktopRoot = Path.Combine(sharedRoot, "Microsoft.WindowsDesktop.App");
            if (!Directory.Exists(desktopRoot))
                return null;
            var exact = Path.Combine(desktopRoot, version);
            if (Directory.Exists(exact))
                return exact;
            return Directory.GetDirectories(desktopRoot).OrderByDescending(d => d).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public static SkuaInstallation Detect(string? assembliesOverride = null, string? userDataOverride = null)
    {
        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrWhiteSpace(userProfile))
            userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
            userProfile = Path.Combine(@"C:\Users", Environment.UserName);

        var localDirs = new[]
        {
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(userProfile, "AppData", "Local")
        }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var roamingDirs = new[]
        {
            Environment.GetEnvironmentVariable("APPDATA"),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Path.Combine(userProfile, "AppData", "Roaming")
        }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var assemblyCandidates = new List<string>();
        AddIfSet(assemblyCandidates, assembliesOverride);
        AddIfSet(assemblyCandidates, Environment.GetEnvironmentVariable(AssembliesEnvVar));
        foreach (var localAppData in localDirs)
        {
            assemblyCandidates.Add(Path.Combine(localAppData, "VibeSkua", "current", "Assemblies"));
            assemblyCandidates.Add(Path.Combine(localAppData, "VibeSkua", "current"));
            assemblyCandidates.Add(Path.Combine(localAppData, "Skua", "current", "Assemblies"));
            assemblyCandidates.Add(Path.Combine(localAppData, "Skua", "current"));
        }

        var assembliesDir = assemblyCandidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "Skua.Core.Interfaces.dll")))
            ?? throw new DirectoryNotFoundException(
                "Could not find Skua.Core.Interfaces.dll. Install VibeSkua/Skua or set the " +
                $"{AssembliesEnvVar} environment variable (or --skua-assemblies) to the folder containing the Skua.Core.* assemblies.");

        var appDir = string.Equals(Path.GetFileName(assembliesDir), "Assemblies", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(assembliesDir)!
            : assembliesDir;

        var flavor = assembliesDir.Contains("VibeSkua", StringComparison.OrdinalIgnoreCase) ? "VibeSkua" : "Skua";

        var dataCandidates = new List<string>();
        AddIfSet(dataCandidates, userDataOverride);
        AddIfSet(dataCandidates, Environment.GetEnvironmentVariable(DataEnvVar));
        foreach (var roamingAppData in roamingDirs)
        {
            dataCandidates.Add(Path.Combine(roamingAppData, flavor));
            dataCandidates.Add(Path.Combine(roamingAppData, "VibeSkua"));
            dataCandidates.Add(Path.Combine(roamingAppData, "Skua"));
        }

        var dataDir = dataCandidates.FirstOrDefault(d => Directory.Exists(Path.Combine(d, "Scripts")))
            ?? dataCandidates.FirstOrDefault(Directory.Exists)
            ?? dataCandidates[^1];

        return new SkuaInstallation(Path.GetFullPath(assembliesDir), Path.GetFullPath(appDir), Path.GetFullPath(dataDir), flavor);
    }

    private static void AddIfSet(List<string> list, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            list.Add(value.Trim());
    }
}
