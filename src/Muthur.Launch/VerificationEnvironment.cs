using System.Collections;

namespace Muthur.Launch;

public static class VerificationEnvironment
{
    public static readonly string[] Allowed = ["PATH", "SystemRoot", "COMSPEC", "PATHEXT", "LOCALAPPDATA", "ProgramFiles",
        "ProgramFiles(x86)", "DOTNET_ROOT", "NUGET_PACKAGES", "VSINSTALLDIR", "VCINSTALLDIR", "VCToolsInstallDir",
        "VCToolsVersion", "WindowsSdkDir", "WindowsSDKVersion", "WindowsSDKLibVersion", "UniversalCRTSdkDir",
        "UCRTVersion", "INCLUDE", "LIB", "LIBPATH", "VSCMD_ARG_HOST_ARCH", "VSCMD_ARG_TGT_ARCH"];

    public static string[] Scrub() => Environment.GetEnvironmentVariables().Keys.Cast<string>().ToArray();

    public static Dictionary<string, string> Create(string scratch, string url)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Allowed)
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) result[name] = value;
        foreach (var name in new[] { "HOME", "USERPROFILE", "DOTNET_CLI_HOME" }) result[name] = Path.Combine(scratch, "profile");
        foreach (var name in new[] { "TEMP", "TMP" }) result[name] = Path.Combine(scratch, "temp");
        result["MUTHUR_HOME"] = Path.Combine(scratch, "home");
        result["MUTHUR_URL"] = url;
        foreach (var name in new[] { "HOME", "TEMP", "MUTHUR_HOME" }) Directory.CreateDirectory(result[name]);
        return result;
    }
}
