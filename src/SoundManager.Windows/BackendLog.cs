using System.IO;
using System.Text.Json;

namespace SoundManager.Windows;

internal static class BackendLog
{
    private static readonly object Gate=new();
    // Only structured lifecycle events are recorded. Never copy config, URLs, keys or raw child output.
    public static void Write(string name,int? pid,int? exitCode=null)
    {
        try
        {
            lock(Gate)
            {
                string folder=Path.Combine(WindowsUpdates.Root,"logs");Directory.CreateDirectory(folder);
                string path=Path.Combine(folder,"backend.jsonl");
                if(File.Exists(path)&&new FileInfo(path).Length>262144)File.Move(path,path+".1",overwrite:true);
                File.AppendAllText(path,JsonSerializer.Serialize(new{at=DateTimeOffset.UtcNow,eventName=name,pid,exitCode})+Environment.NewLine);
            }
        }
        catch(IOException){}
        catch(UnauthorizedAccessException){}
    }
}
