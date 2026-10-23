using System.IO;
using Microsoft.Win32;

namespace SoundManager.Windows;

// Per-user, opt-in login startup. No administrator access or Windows service.
internal sealed class StartupRegistration(string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run")
{
    private const string ValueName = "SoundManager";
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(ValueName) is string;
        }
    }

    public void Enable(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"') ||
            !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
            throw new ArgumentException("자동 실행할 EXE 경로를 확인하세요.");
        string command = $"\"{executable}\" --startup";
        if (command.Length > 260) throw new ArgumentException("자동 실행 경로가 너무 깁니다. 더 짧은 경로에서 앱을 실행하세요.");
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
