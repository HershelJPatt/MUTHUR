namespace Muthur.XmlDocCheck;

public static class Program
{
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--root" || string.IsNullOrWhiteSpace(args[1])))
        {
            error.WriteLine("Usage: Muthur.XmlDocCheck [--root <directory>]");
            return 2;
        }
        try
        {
            var diagnostics = RepositoryChecker.Scan(args.Length == 0 ? Directory.GetCurrentDirectory() : args[1]);
            foreach (var diagnostic in diagnostics) output.WriteLine(diagnostic);
            return diagnostics.Count == 0 ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or System.ComponentModel.Win32Exception)
        {
            error.WriteLine($"XML documentation check failed: {ex.Message}");
            return 2;
        }
    }
}
