using StardewAI.Companion.Mod.Discovery;
using StardewAI.Companion.Mod.Transport;
using StardewModdingAPI;

namespace StardewAI.Companion.Mod.Hosting;

internal sealed class GameTransportSession : IDisposable
{
    private readonly DiscoveryService? _discovery;
    private readonly string _saveId;
    private readonly string _token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public WebSocketTransportServer Server { get; }

    public GameTransportSession(string modDirectory, string saveId, ITransportHandler handler,
        DiscoveryService? discovery, IMonitor monitor)
    {
        _saveId = saveId;
        _discovery = discovery;
        Server = new WebSocketTransportServer(port: 0, sessionToken: _token, handler: handler,
            saveId: saveId, gameSessionId: Id, smapiLogger: monitor.Log,
            completedArchiveDirectory: Path.Combine(modDirectory, "data", "transport-receipts", Id));
    }

    public void Publish() => _discovery?.Publish(_saveId, Id, Server.Port, _token, Server.ProtocolVersion);

    public void Dispose()
    {
        _discovery?.Invalidate();
        Server.Stop();
        Server.Dispose();
    }
}
