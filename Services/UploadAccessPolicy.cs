namespace LanFileShare;

public class UploadAccessPolicy(FileShareService files, UserService users)
{
    public bool CanWrite(UploadSessionMeta meta)
    {
        try
        {
            if (meta.Version != 2 || string.IsNullOrEmpty(meta.ResolvedBaseDir)) return false;
            var storedRoot = Path.GetFullPath(meta.ResolvedBaseDir);
            var storedDirectory = Path.GetFullPath(Path.Combine(storedRoot, meta.SubPath));
            if (!FileShareService.IsPathUnderDirectory(storedRoot, storedDirectory)) return false;
            if (meta.UserId.StartsWith("pub:", StringComparison.Ordinal))
            {
                return files.Settings.PublicShareEnabled && !files.Settings.PublicShareReadOnly
                    && !files.IsIPBlacklisted(meta.UserId[4..])
                    && files.GetPublicSharePaths().Any(root => SamePath(root, storedRoot));
            }

            var user = users.FindById(meta.UserId);
            if (user == null || !user.Enabled || user.Pending) return false;
            string rootPath, relativePath;
            if (meta.AccessPath.StartsWith("__shared__/", StringComparison.Ordinal))
            {
                var parts = meta.AccessPath["__shared__/".Length..].Split('/', 2);
                var share = users.GetSharedFolders(user.Id).FirstOrDefault(x => x.Id == parts[0]);
                if (share == null || !share.WritePermission || share.IsFile) return false;
                rootPath = share.SourcePath;
                relativePath = parts.Length > 1 ? parts[1] : "";
            }
            else
            {
                if (user.ReadOnly) return false;
                var folders = user.Folders.Count > 0 ? user.Folders
                    : string.IsNullOrEmpty(user.Folder) ? new List<string>() : new List<string> { user.Folder };
                if (meta.AccessPath.StartsWith("__folder__/", StringComparison.Ordinal))
                {
                    var parts = meta.AccessPath["__folder__/".Length..].Split('/', 2);
                    if (!int.TryParse(parts[0], out var index) || index < 0 || index >= folders.Count) return false;
                    rootPath = folders[index];
                    relativePath = parts.Length > 1 ? parts[1] : "";
                }
                else
                {
                    if (folders.Count == 0) return false;
                    rootPath = folders[0];
                    relativePath = meta.AccessPath;
                }
            }
            var currentDirectory = Path.GetFullPath(Path.Combine(rootPath, FileShareService.NormalizeSubPath(relativePath)));
            return SamePath(rootPath, storedRoot)
                && FileShareService.IsPathUnderDirectory(rootPath, currentDirectory)
                && SamePath(currentDirectory, storedDirectory);
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }
    }

    private static bool SamePath(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
}
