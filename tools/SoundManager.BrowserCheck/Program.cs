using System.Text.Json;
using SoundManager.Core;
using SoundManager.Server;
using System.Diagnostics;

// Local synthetic-browser test host. Never opens an audio device or a public listener.
var mixer = new Mixer();
string key = HubServer.CreateToken();
await using var server = new HubServer(mixer, key, 0, "127.0.0.1");
await server.StartAsync();
using var renderStop = new CancellationTokenSource();
Task render = args.Contains("--realtime") ? Task.Factory.StartNew(() =>
{
    var clock = Stopwatch.StartNew(); var output = new float[960]; long frame = 0;
    while (!renderStop.IsCancellationRequested)
    {
        mixer.Render(output); frame++;
        double remaining = frame * 10 - clock.Elapsed.TotalMilliseconds;
        if (remaining > 0) renderStop.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(remaining));
        else if (remaining < -1000) frame = (long)(clock.Elapsed.TotalMilliseconds / 10);
    }
}, TaskCreationOptions.LongRunning) : Task.CompletedTask;
Console.WriteLine(JsonSerializer.Serialize(new { address = server.Addresses.Single(), key }));
while (await Console.In.ReadLineAsync() is string command)
{
    if (command == "exit") break;
    if (command == "render")
    {
        var output = new float[960]; mixer.Render(output);
        Console.WriteLine(JsonSerializer.Serialize(new { rms = Math.Sqrt(output.Select(x => (double)x * x).Average()), sources = mixer.Snapshot().Sources.Length }));
    }
}
renderStop.Cancel(); await render;
