using SoundManager.Core;

namespace SoundManager.Windows;

// Cached view model; no C# mixing or audio rendering remains on the server path.
internal sealed class RemoteMixer
{
    private RustBackend? backend;
    private MixSettings settings=new();
    private readonly SemaphoreSlim changes=new(1);
    private long revision;
    private bool refreshing;
    public event Action? Failed;
    public MixSettings Settings
    {
        get=>settings;
        set
        {
            if(value==settings)return;
            settings=value;long current=++revision;
            if(backend is {} server)_=SendAsync(server,value,current);
        }
    }
    public void Attach(RustBackend server){backend=server;}
    public void Detach(){backend=null;}
    public MixerState Snapshot()=>(backend?.Snapshot()??new MixerState(settings,[],0)) with{Settings=settings};
    private async Task SendAsync(RustBackend server,MixSettings value,long current)
    {
        await changes.WaitAsync();
        try
        {
            if(backend!=server||revision!=current)return;
            await server.SetMixAsync(value);
        }
        catch(Exception){if(backend==server)Failed?.Invoke();}
        finally{changes.Release();}
    }
    public async Task RefreshAsync()
    {
        if(refreshing||backend is not {} server)return;
        refreshing=true;long current=revision;
        try
        {
            await changes.WaitAsync();
            try
            {
                if(backend!=server||current!=revision)return;
                var state=await server.RefreshAsync();
                if(backend==server&&current==revision)settings=state.Settings;
            }
            finally{changes.Release();}
        }
        catch(Exception){if(backend==server)Failed?.Invoke();}
        finally{refreshing=false;}
    }
}
