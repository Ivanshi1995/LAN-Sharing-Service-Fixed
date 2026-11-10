using Microsoft.Extensions.FileProviders;

namespace LanFileShare;

public static class StaticAssets
{
    public static void UseLanStaticAssets(this WebApplication app, string appBaseDir)
    {
        var cdn = Path.Combine(appBaseDir, "CDN");
        if (!Directory.Exists(cdn)) return;
        app.UseStaticFiles(new StaticFileOptions
        {
            RequestPath = "/CDN",
            FileProvider = new PhysicalFileProvider(cdn)
        });
    }
}
