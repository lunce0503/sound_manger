namespace SoundManager.Updates;

public sealed record LauncherOptions(string? ExtractDirectory = null, int? WaitForPid = null, string? Token = null, string? Fallback = null)
{
    public static LauncherOptions Parse(string[] args)
    {
        if (args.Length == 0) return new();
        if (args.Length == 2 && args[0] == "--extract-only") return new(Path.GetFullPath(args[1]));
        if (args.Length == 6 && args[0] == "--wait-for" && args[2] == "--update-token" && args[4] == "--fallback" &&
            int.TryParse(args[1], out int pid) && pid > 0 && IsToken(args[3])) return new(null, pid, args[3], Path.GetFullPath(args[5]));
        throw new ArgumentException("지원하지 않는 실행 옵션입니다.");
    }
    public static bool IsToken(string text) => text.Length == 32 && text.All(char.IsAsciiHexDigit);
    public static string Marker(string root, string token, string suffix)
    {
        if (!IsToken(token) || suffix is not ("prepared" or "ready" or "cancel")) throw new ArgumentException("잘못된 업데이트 인계");
        return Path.Combine(root, "updates", token + "." + suffix);
    }
    public static bool IsInstalledExecutable(string root, string path) => Path.GetFullPath(path).StartsWith(
        Path.GetFullPath(Path.Combine(root, "versions")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(path).Equals("SoundManager.Windows.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
}
