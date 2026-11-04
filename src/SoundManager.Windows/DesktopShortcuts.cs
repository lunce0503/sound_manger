using System.IO;
using System.Runtime.InteropServices;
using SoundManager.Updates;

namespace SoundManager.Windows;

internal static class DesktopShortcuts
{
    public static void Ensure()
    {
        string executable=Environment.ProcessPath!;
        if(!LauncherOptions.IsInstalledExecutable(WindowsUpdates.Root,executable))return;
        string marker=Path.Combine(WindowsUpdates.Root,"shortcuts-installed");
        bool first=!File.Exists(marker);
        string icon=Path.Combine(WindowsUpdates.Root,"sound-manager.ico");
        File.Copy(Path.Combine(AppContext.BaseDirectory,"sound-manager.ico"),icon,overwrite:true);
        string desktop=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"Sound Manager.lnk");
        string startMenu=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"Sound Manager.lnk");
        foreach(string shortcut in new[]{desktop,startMenu})
            if(first||File.Exists(shortcut))Create(shortcut,executable,icon);
        File.WriteAllText(marker,"1");
    }
    internal static void Create(string path,string executable,string icon)
    {
        if(!Path.IsPathFullyQualified(executable)||!File.Exists(executable)||!File.Exists(icon))throw new IOException("바로가기 파일을 확인하세요.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        object? shell=null,link=null;
        try
        {
            shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")??throw new IOException("Windows 바로가기를 생성할 수 없습니다."));
            dynamic automation=shell!;link=automation.CreateShortcut(path);dynamic shortcut=link;
            shortcut.TargetPath=executable;shortcut.WorkingDirectory=Path.GetDirectoryName(executable);
            shortcut.Description="Sound Manager · 기기와 브라우저 탭의 소리 통합";
            shortcut.IconLocation=icon+",0";shortcut.Save();
        }
        finally
        {
            if(link is not null&&Marshal.IsComObject(link))Marshal.FinalReleaseComObject(link);
            if(shell is not null&&Marshal.IsComObject(shell))Marshal.FinalReleaseComObject(shell);
        }
    }
}
