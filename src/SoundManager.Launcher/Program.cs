using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

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
            if (args.Length!=0 && !extractOnly) throw new ArgumentException("지원하지 않는 실행 옵션입니다.");
            var assembly=Assembly.GetExecutingAssembly();
            string hash=assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a=>a.Key=="PayloadSha256").Value!;
            string version=assembly.GetName().Version!.ToString(3);
            string destination=extractOnly?Path.GetFullPath(args[1]):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SoundManager","versions",version+"-"+hash[..12]);
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
