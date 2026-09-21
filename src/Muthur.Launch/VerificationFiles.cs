using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Muthur.Launch;

public static class VerificationFiles
{
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string HashFile(string path, CancellationToken ct = default)
    {
        using var stream = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        ct.ThrowIfCancellationRequested();
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static bool Within(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static bool Intersects(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase) || Within(a, b) || Within(b, a);

    public static string Relative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\') || path.Contains(':') ||
            path.Split('/').Any(s => s is "" or "." or ".." || s.EndsWith('.') || s.EndsWith(' ')))
            throw new ArgumentException("Expected a normalized repository-relative path without traversal.");
        return path;
    }

    public static string PlainPath(string path)
    {
        var full = Path.GetFullPath(path);
        for (var cursor = full; cursor is not null; cursor = Path.GetDirectoryName(cursor))
        {
            try
            {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Reparse points are not allowed: {cursor}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }

    public static void Atomic<T>(string path, T value, JsonTypeInfo<T> type)
    {
        PlainPath(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, type);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static List<VerificationFile> Inventory(string root, CancellationToken ct = default)
    {
        PlainPath(root);
        var files = new List<VerificationFile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                PlainPath(entry);
                var relative = Relative(Path.GetRelativePath(root, entry).Replace('\\', '/'));
                if (!names.Add(relative)) throw new IOException("Case-colliding installation paths.");
                var name = Path.GetFileName(entry).ToLowerInvariant();
                if (name is "home" or ".nuget" or "scratch" or "temp" or "tmp" or ".aws" or ".azure" or ".ssh" or ".env" ||
                    name.EndsWith(".db") || name.Contains(".db-") || name.EndsWith(".sqlite") ||
                    name.EndsWith(".log") || name.EndsWith(".token") || name.EndsWith(".key") ||
                    name.EndsWith(".pfx") || name.EndsWith(".pem") || name.EndsWith(".p12") || name.EndsWith(".sqlite3") ||
                    name is "credentials" or "credentials.json" or "id_rsa" or "id_ed25519")
                    throw new IOException($"Mutable or credential installation path is forbidden: {relative}");
                if (Directory.Exists(entry)) Visit(entry);
                else files.Add(new(relative, new FileInfo(entry).Length, HashFile(entry, ct)));
            }
        }
        Visit(root);
        return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    public static void Copy(string source, string destination, List<VerificationFile> files, CancellationToken ct = default)
    {
        PlainPath(source);
        PlainPath(destination);
        Directory.CreateDirectory(destination);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Relative(file.Path);
            var input = PlainPath(Path.Combine(source, relative));
            var output = PlainPath(Path.Combine(destination, relative));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var sourceStream = File.OpenRead(input);
            using var destinationStream = new FileStream(output, FileMode.CreateNew);
            var buffer = new byte[65536];
            int count;
            while ((count = sourceStream.Read(buffer)) != 0)
            {
                ct.ThrowIfCancellationRequested();
                destinationStream.Write(buffer, 0, count);
            }
        }
        if (!Inventory(destination, ct).SequenceEqual(files)) throw new IOException("Copied installation digest mismatch.");
    }

    public static void DeleteOwned(string path, string parent, CancellationToken ct = default)
    {
        if (!Within(path, parent)) throw new IOException("Deletion escaped owned root.");
        PlainPath(path);
        if (!Directory.Exists(path)) return;
        // Inspect every directory before recursing: Directory.Delete must never traverse a junction.
        void Check(string dir)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                PlainPath(entry);
                if (Directory.Exists(entry)) Check(entry);
            }
        }
        Check(path);
        ct.ThrowIfCancellationRequested();
        Directory.Delete(path, true);
    }
}
