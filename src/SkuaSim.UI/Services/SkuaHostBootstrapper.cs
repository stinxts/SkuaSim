using SkuaSim.Core.Hosting;

namespace SkuaSim.UI.Services;

/// <summary>
/// Detects the installed Skua/VibeSkua client and registers assembly probing before any
/// code touching Skua.Core.* types is JIT-compiled.
/// </summary>
public static class SkuaHostBootstrapper
{
    public static SkuaInstallation? Installation { get; private set; }
    public static string? StartupError { get; private set; }

    public static void Initialize(string? assembliesOverride = null, string? userDataOverride = null)
    {
        try
        {
            var installation = SkuaInstallation.Detect(assembliesOverride, userDataOverride);
            SkuaAssemblyResolver.Register(installation.ProbeDirectories);
            Installation = installation;
            StartupError = null;
        }
        catch (Exception ex)
        {
            Installation = null;
            StartupError = ex.Message;
        }
    }
}
