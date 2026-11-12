using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace LanFileShare;

internal sealed record HtmlPreviewGrant(string AccessPath, string FullPath, string? SessionToken, string ClientIp, bool StorageEnabled);

internal static class HtmlPreviewService
{
    internal const string ResourcePrefix = "/api/html-preview/";
    internal const int MaximumPageBytes = 50 * 1024 * 1024;
    internal const string FrameRequiredPolicy = "base-uri 'none'";
    internal const string SourceMediaType = "application/vnd.lanfileshare.preview+html";
    private const string StorageScriptPath = "/CDN/html-preview-storage.js";
    private const string InteractiveSandboxPermissions = "allow-scripts allow-forms";
    private const string PackageCdnSources =
        "https://cdn.jsdelivr.net https://cdnjs.cloudflare.com/ajax/libs/ https://unpkg.com " +
        "https://cdn.bootcdn.net/ajax/libs/ https://ajax.googleapis.com/ajax/libs/";
    private const string ScriptCdnSources =
        PackageCdnSources + " https://esm.sh https://esm.unpkg.com https://code.jquery.com https://cdn.tailwindcss.com";
    private const string StyleCdnSources = PackageCdnSources + " https://fonts.googleapis.com";
    private const string FontCdnSources = PackageCdnSources + " https://fonts.gstatic.com";
    private static readonly ITimeLimitedDataProtector Tickets = new EphemeralDataProtectionProvider()
        .CreateProtector("LanFileShare.HtmlPreview").ToTimeLimitedDataProtector();
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();
    private static readonly Dictionary<string, string> AssetTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html", [".htm"] = "text/html",
        [".css"] = "text/css",
        [".js"] = "text/javascript", [".mjs"] = "text/javascript",
        [".json"] = "application/json", [".txt"] = "text/plain",
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif", [".webp"] = "image/webp", [".avif"] = "image/avif",
        [".bmp"] = "image/bmp", [".ico"] = "image/x-icon", [".svg"] = "image/svg+xml",
        [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
    };

    internal static string CreateUrl(HtmlPreviewGrant grant)
    {
        var ticket = Tickets.Protect(JsonSerializer.Serialize(grant), TimeSpan.FromHours(8));
        return ResourcePrefix + ticket + "/" + Uri.EscapeDataString(Path.GetFileName(grant.FullPath));
    }

    internal static HtmlPreviewGrant? ReadGrant(string ticket)
    {
        try
        {
            return JsonSerializer.Deserialize<HtmlPreviewGrant>(Tickets.Unprotect(ticket));
        }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
        catch (JsonException) { return null; }
    }

    internal static Uri GetOrigin(HttpRequest request) =>
        new($"{request.Scheme}://{request.Host.ToUriComponent()}");

    internal static IResult OpenTab(HttpRequest request, string pageUrl, string fullPath, string? userId)
    {
        var origin = GetOrigin(request);
        var authority = origin.GetLeftPart(UriPartial.Authority);
        var isIpv6 = origin.HostNameType == UriHostNameType.IPv6;
        var frameSource = isIpv6 ? "'self'" : authority + pageUrl[..(pageUrl.LastIndexOf('/') + 1)];
        var storageScript = isIpv6 ? "" : $"<script src=\"{StorageScriptPath}\"></script>";
        var scope = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new[] { userId, Path.GetDirectoryName(fullPath)!.ToUpperInvariant() })));
        var response = request.HttpContext.Response;
        response.Headers["Content-Security-Policy"] =
            $"default-src 'none'; style-src 'unsafe-inline'; frame-src {frameSource}; frame-ancestors 'none'; base-uri 'none'; form-action 'none'" +
            (isIpv6 ? "" : $"; script-src {authority}{StorageScriptPath}");
        response.Headers["Referrer-Policy"] = "strict-origin";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        var title = HtmlEncoder.Default.Encode(Path.GetFileName(fullPath));
        var source = HtmlEncoder.Default.Encode(pageUrl);
        return Results.Content($$"""
            <!doctype html>
            <html lang="zh-CN" data-preview-scope="{{scope}}">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>{{title}}</title>
                <style>body { margin: 0; } iframe { position: fixed; inset: 0; width: 100%; height: 100%; border: 0; }</style>
                {{storageScript}}
            </head>
            <body><iframe src="{{source}}" title="{{title}}" sandbox="{{InteractiveSandboxPermissions}}" csp="{{FrameRequiredPolicy}}"></iframe></body>
            </html>
            """, "text/html; charset=utf-8");
    }

    internal static IResult LoadStorageDocument() => Results.Content($"""
        <!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <script defer src="{StorageScriptPath}"></script></head><body>正在加载网页…</body></html>
        """, "text/html; charset=utf-8");

    internal static string GetInteractivePolicy(string assets, string? authority = null) =>
        $"sandbox {InteractiveSandboxPermissions}; default-src 'none'; script-src 'unsafe-inline' {assets} {ScriptCdnSources}" +
        (authority == null ? "" : $" {authority}{StorageScriptPath}") + "; " +
        $"style-src 'unsafe-inline' {assets} {StyleCdnSources}; img-src data: blob: {assets}; " +
        $"font-src data: {assets} {FontCdnSources}; connect-src {assets}; " +
        $"{FrameRequiredPolicy}; form-action {assets}; frame-ancestors 'self'";

    internal static bool IsFrameRequest(HttpRequest request, Uri origin)
    {
        var destination = request.Headers["Sec-Fetch-Dest"].ToString();
        if (destination.Length > 0) return destination == "iframe";
        // Sandboxed navigations omit referrers. On HTTP LAN origins, Chromium still sends the frame's required CSP.
        if (request.Headers["Sec-Required-CSP"] == FrameRequiredPolicy) return true;

        // The first load can use the viewer's referrer on plain HTTP LAN addresses.
        return Uri.TryCreate(request.Headers.Referer, UriKind.Absolute, out var referer)
            && referer.GetLeftPart(UriPartial.Authority) == origin.GetLeftPart(UriPartial.Authority);
    }

    internal static bool IsRegularFile(string shareRoot, string fullPath)
    {
        if (!FileShareService.IsPathUnderDirectory(shareRoot, fullPath) || !File.Exists(fullPath))
            return false;

        // Existing junctions and symbolic links must not extend the preview beyond the share.
        var current = Path.GetFullPath(shareRoot);
        try
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            foreach (var segment in Path.GetRelativePath(current, fullPath).Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, segment);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static (string Path, string MimeType)? ResolveAsset(string entryPath, string relativePath)
    {
        if (relativePath.Split('/').Any(segment => segment.Length == 0
            || segment.EndsWith('.') || segment.EndsWith(' ') || segment.IndexOfAny(InvalidNameChars) >= 0))
            return null;

        var fullPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(entryPath)!, relativePath));
        return AssetTypes.TryGetValue(Path.GetExtension(fullPath), out var mimeType)
            ? (fullPath, mimeType)
            : null;
    }
}
