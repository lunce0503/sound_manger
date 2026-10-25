using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SoundManager.Updates;

internal static class Program
{
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window,string text,string title,uint type);

    [STAThread]
    private static int Main(string[] args)
    {
        bool extractOnly=args.Length==2 && args[0]=="--extract-only";
        try
        {
            var options=LauncherOptions.Parse(args);
            var assembly=Assembly.GetExecutingAssembly();
            string hash=assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a=>a.Key=="PayloadSha256").Value!;
            string version=assembly.GetName().Version!.ToString(3);
            string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SoundManager");
            if (!extractOnly && options.WaitForPid is null && InstalledVersion.NewerExecutable(root,Version.Parse(version)) is string newer)
            { Process.Start(new ProcessStartInfo(newer){UseShellExecute=true,WorkingDirectory=Path.GetDirectoryName(newer)!}); return 0; }
            string destination=options.ExtractDirectory??Path.Combine(root,"versions",version+"-"+hash[..12]);
            Directory.CreateDirectory(destination);
            using var launchLock=AcquireLock(Path.Combine(destination,".launch.lock"));
            string marker=Path.Combine(destination,".ready");
            if (!File.Exists(marker) || File.ReadAllText(marker)!=hash || !File.Exists(Path.Combine(destination,"SoundManager.Windows.exe")))
            {
                using var payload=assembly.GetManifestResourceStream("SoundManager.Payload.zip") ?? throw new IOException("앱 실행 파일을 읽지 못했습니다.");
                if (!Convert.ToHexString(SHA256.HashData(payload)).Equals(hash,StringComparison.OrdinalIgnoreCase))
                    throw new IOException("앱 파일 검증에 실패했습니다. 공식 배포 파일을 다시 받으세요.");
                payload.Position=0;
                using var zip=new ZipArchive(payload,ZipArchiveMode.Read);
                zip.ExtractToDirectory(destination,overwriteFiles:true);
                File.WriteAllText(marker,hash);
            }
            if (extractOnly) return 0;
            if (options.WaitForPid is int parentId)
            {
                if (!LauncherOptions.IsInstalledExecutable(root,options.Fallback!)) throw new IOException("이전 앱 경로를 확인하지 못했습니다.");
                using var parent=Process.GetProcessById(parentId);
                if (!string.Equals(Path.GetFileName(parent.MainModule?.FileName),"SoundManager.Windows.exe",StringComparison.OrdinalIgnoreCase)) throw new IOException("업데이트할 앱을 확인하지 못했습니다.");
                Directory.CreateDirectory(Path.Combine(root,"updates"));
                File.WriteAllText(LauncherOptions.Marker(root,options.Token!,"prepared"),version);
                var timeout=Stopwatch.StartNew();
                while (!parent.HasExited)
                {
                    if (File.Exists(LauncherOptions.Marker(root,options.Token!,"cancel"))) return 0;
                    if (timeout.Elapsed>TimeSpan.FromMinutes(5)) throw new TimeoutException("앱 종료를 기다리는 시간이 초과되었습니다.");
                    Thread.Sleep(100);
                }
                var start=new ProcessStartInfo(Path.Combine(destination,"SoundManager.Windows.exe")){UseShellExecute=false,WorkingDirectory=destination,CreateNoWindow=true};
                start.ArgumentList.Add("--updated");start.ArgumentList.Add(options.Token!);
                var fallback=new ProcessStartInfo(options.Fallback!){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(options.Fallback!)!,CreateNoWindow=true};
                fallback.ArgumentList.Add("--update-fallback");fallback.ArgumentList.Add(options.Token!);
                return UpdateHandoff.Launch(start,fallback,LauncherOptions.Marker(root,options.Token!,"ready"),TimeSpan.FromMinutes(2))?0:1;
            }
            Process.Start(new ProcessStartInfo(Path.Combine(destination,"SoundManager.Windows.exe")) { WorkingDirectory=destination,UseShellExecute=true });
            return 0;
        }
        catch (Exception error)
        {
            if (extractOnly) Console.Error.WriteLine(error.Message);
            else MessageBox(IntPtr.Zero,"Sound Manager를 실행하지 못했습니다.\n"+error.Message,"Sound Manager",0x10);
            return 1;
        }
    }

    private static FileStream AcquireLock(string path)
    {
        for (int attempt=0;;attempt++)
        {
            try { return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
            catch (IOException) when (attempt<100) { Thread.Sleep(200); }
        }
    }
}
