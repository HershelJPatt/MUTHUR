namespace Muthur.Launch;

public static class VerificationEnvironment
{
    public const string GitConfiguration = "[core]\n\tlongpaths = true\n";
    public static readonly string[] Allowed = ["PATH", "SystemRoot", "COMSPEC", "PATHEXT", "ProgramFiles",
        "ProgramFiles(x86)", "ProgramData", "PROCESSOR_ARCHITECTURE", "DOTNET_ROOT", "NUGET_PACKAGES", "VSINSTALLDIR", "VCINSTALLDIR", "VCToolsInstallDir",
        "VCToolsVersion", "WindowsSdkDir", "WindowsSDKVersion", "WindowsSDKLibVersion", "UniversalCRTSdkDir",
        "UCRTVersion", "INCLUDE", "LIB", "LIBPATH", "VSCMD_ARG_HOST_ARCH", "VSCMD_ARG_TGT_ARCH"];

    public static string[] Scrub() => Environment.GetEnvironmentVariables().Keys.Cast<string>().ToArray();

    public static Dictionary<string, string> Create(string scratch, string url)
    {
        scratch = Path.GetFullPath(scratch);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Allowed)
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) result[name] = value;
        foreach (var name in new[] { "HOME", "USERPROFILE", "DOTNET_CLI_HOME" }) result[name] = Path.Combine(scratch, "profile");
        foreach (var name in new[] { "TEMP", "TMP" }) result[name] = Path.Combine(scratch, "temp");
        result["APPDATA"] = Path.Combine(scratch, "profile", "AppData", "Roaming");
        result["LOCALAPPDATA"] = Path.Combine(scratch, "profile", "AppData", "Local");
        result["MUTHUR_HOME"] = Path.Combine(scratch, "home");
        result["MUTHUR_URL"] = url;
        foreach (var name in new[] { "HOME", "TEMP", "MUTHUR_HOME", "APPDATA", "LOCALAPPDATA" }) Directory.CreateDirectory(result[name]);
        result["GIT_CEILING_DIRECTORIES"] = scratch;
        var gitConfig = Path.Combine(result["HOME"], ".gitconfig");
        VerificationFiles.PlainPath(gitConfig);
        File.WriteAllText(gitConfig, GitConfiguration, new System.Text.UTF8Encoding(false));
        return result;
    }

    public static SortedDictionary<string, string> Identity(IReadOnlyDictionary<string, string> environment)
    {
        var identity = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var variable in Allowed)
            identity["environment/" + variable] = VerificationFiles.Hash(environment.GetValueOrDefault(variable, "<absent>"));
        foreach (var variable in new[] { "HOME", "USERPROFILE", "DOTNET_CLI_HOME", "TEMP", "TMP", "MUTHUR_HOME", "APPDATA", "LOCALAPPDATA" })
            identity["environment/" + variable] = "run-owned-v2/" + variable;
        identity["environment/MUTHUR_URL"] = "run-owned-v2/loopback-port";
        identity["environment/GIT_CEILING_DIRECTORIES"] = "run-owned-v1/scratch-ceiling";
        identity["environment/git-config"] = VerificationFiles.HashFile(Path.Combine(environment["HOME"], ".gitconfig"));
        return identity;
    }

    public static string Canonicalize(string value, IReadOnlyDictionary<string, string> environment)
    {
        foreach (var variable in new[] { "APPDATA", "LOCALAPPDATA", "HOME", "TEMP", "MUTHUR_HOME", "MUTHUR_URL" }
            .OrderByDescending(name => environment[name].Length))
            value = value.Replace(environment[variable], "{run-owned/" + variable + "}", StringComparison.OrdinalIgnoreCase);
        var scratch = Path.GetDirectoryName(environment["HOME"])!;
        return value.Replace(scratch, "{run-owned/scratch}", StringComparison.OrdinalIgnoreCase);
    }
}
