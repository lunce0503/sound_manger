using System.Text.Json;
using SoundManager.Core;
using SoundManager.Server;

// Local synthetic-browser test host. Never opens an audio device or a public listener.
var mixer = new Mixer();
string key = HubServer.CreateToken();
await using var server = new HubServer(mixer, key, 0, "127.0.0.1");
await server.StartAsync();
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
