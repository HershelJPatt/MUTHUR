namespace Muthur.Launch;

/// <summary>One local inference at a time across CLI processes sharing the same hub home.</summary>
public static class LocalInferenceLease
{
    public static FileStream Acquire(string home)
    {
        Directory.CreateDirectory(home);
        return new FileStream(Path.Combine(home, "local-inference.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
}
