using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SoundManager.Core;

namespace SoundManager.Server;

[SupportedOSPlatform("windows")]
public sealed class WindowsConnectionStore(string directory)
{
    private string FilePath(string name) => Path.Combine(directory,name);
    private void Save(string name,string value)
    {
        Directory.CreateDirectory(directory);
        byte[] encrypted=ProtectedData.Protect(Encoding.UTF8.GetBytes(value),null,DataProtectionScope.CurrentUser);
        string temporary=FilePath(name)+".tmp";
        File.WriteAllBytes(temporary,encrypted); File.Move(temporary,FilePath(name),true);
    }
    private string? Load(string name) => !File.Exists(FilePath(name)) ? null :
        Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FilePath(name)),null,DataProtectionScope.CurrentUser));
    public string ServerKey()
    {
        string? key=Load("server-key.dpapi");
        if (key is not null) return key;
        key=HubServer.CreateToken(); Save("server-key.dpapi",key); return key;
    }
    public void SaveClient(PairingLink pair) => Save("client.dpapi",pair.Encode());
    public PairingLink? Client() { string? value=Load("client.dpapi"); return value is null ? null : PairingLink.Parse(value); }
}
