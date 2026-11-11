using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace LanFileShare;

internal sealed record HtmlPreviewGrant(string AccessPath, string FullPath, string? SessionToken, string ClientIp);

internal static class HtmlPreviewService
{
    internal const string ResourcePrefix = "/api/html-preview/";
    internal const int MaximumPageBytes = 50 * 1024 * 1024;
    private static readonly ITimeLimitedDataProtector Tickets = new EphemeralDataProtectionProvider()
        .CreateProtector("LanFileShare.HtmlPreview").ToTimeLimitedDataProtector();
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();
    private static readonly Dictionary<string, string> AssetTypes = new(StringComparer.OrdinalIgnoreCase)
    {
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

    internal static bool IsFrameRequest(HttpRequest request, Uri origin)
    {
        var destination = request.Headers["Sec-Fetch-Dest"].ToString();
        if (destination.Length > 0) return destination == "iframe";

        // Browsers omit Fetch Metadata on plain HTTP LAN addresses.
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
        if (string.Equals(fullPath, entryPath, StringComparison.OrdinalIgnoreCase))
            return (fullPath, "text/html");

        return AssetTypes.TryGetValue(Path.GetExtension(fullPath), out var mimeType)
            ? (fullPath, mimeType)
            : null;
    }
}
