namespace Muthur.Server.Services;

// T-57's component walk, also resolving ancestors of each link target from its volume root.
// Path resolution cannot detect hard links or prevent concurrent filesystem mutation before open.
internal static class SpecPath
{
    public static string? Resolve(string absolute)
    {
        var remaining = 64;
        try
        {
            return Walk(absolute, ref remaining);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? Walk(string absolute, ref int remaining)
    {
        var normalized = Path.GetFullPath(absolute);
        var current = Path.GetPathRoot(normalized);
        if (string.IsNullOrEmpty(current)) return null;
        var components = normalized[current.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < components.Length; i++)
        {
            var candidate = Path.Combine(current, components[i]);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(candidate);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return Path.Combine(current, Path.Combine(components[i..]));
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (remaining-- == 0) return null;
                var target = File.ResolveLinkTarget(candidate, returnFinalTarget: false);
                if (target is null || Walk(target.FullName, ref remaining) is not { } resolved) return null;
                current = resolved;
            }
            else current = candidate;
        }
        return current;
    }
}
