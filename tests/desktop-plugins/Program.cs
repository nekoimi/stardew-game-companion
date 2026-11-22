using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using StardewAI.Companion.Desktop.Plugins;
using StardewAI.Companion.Logging;

string root = Path.Combine(Path.GetTempPath(), $"伴侣 插件验收 {Guid.NewGuid():N}");
Directory.CreateDirectory(root);
using var fixture = JsonDocument.Parse(File.ReadAllBytes(args[1]));
var example = fixture.RootElement;
var package = example.GetProperty("package");
var original = JsonNode.Parse(package.GetProperty("manifest").GetRawText())!.AsObject();
var payload = File.ReadAllBytes(args[0]);
string id = original["id"]!.GetValue<string>();
string data = Path.Combine(root, "data", "plugins", id);
string flag(string name) => Path.Combine(data, name);
var logger = new FileLogger(Path.Combine(root, "data", "logs"));
var originalKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
try
{
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", "credential-fixture-must-not-reach-plugin");
    byte[] sharedEntry = System.Text.Encoding.UTF8.GetBytes(package.GetProperty("entryText").GetString()!);
    string good = WriteZip(original, "good", payload);
    string corrupt = WriteZip(original, "corrupt", sharedEntry, corruptHash: true);
    string traversal = WriteZip(original, "zip-traversal", sharedEntry, escape: true);
    foreach (var variant in package.GetProperty("rejected").EnumerateArray())
    {
        var manifest = original.DeepClone().AsObject();
        foreach (var property in variant.GetProperty("patch").EnumerateObject())
            manifest[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        string name = variant.GetProperty("name").GetString()!;
        string zip = WriteZip(manifest, name, sharedEntry);
        await using var host = new PluginHost(root, logger);
        await Expect<InvalidDataException>(() => host.InstallAsync(zip, Confirm));
    }

    await using (var host = new PluginHost(root, logger))
    {
        await Expect<InvalidOperationException>(async () =>
        {
            await using var duplicate = new PluginHost(root, logger);
        });
        using var cancellation = new CancellationTokenSource();
        var registry = new PluginRegistry(root);
        await Expect<OperationCanceledException>(() => registry.InstallAsync(good, _ =>
        {
            cancellation.Cancel();
            return Task.FromResult(true);
        }, cancellation.Token));
        Check(registry.List().Length == 0 && !Directory.EnumerateFileSystemEntries(Path.Combine(root, "plugins", ".staging")).Any(),
            "cancelled install published a package");
        Check(await host.InstallAsync(good, _ => Task.FromResult(false)) is null, "declined package installed");
        await Expect<InvalidDataException>(() => host.InstallAsync(corrupt, Confirm));
        await Expect<InvalidDataException>(() => host.InstallAsync(traversal, Confirm));
        var installed = await host.InstallAsync(good, Confirm);
        Check(installed is { Enabled: false, AutoStart: false }, "new installation must be disabled");
        await Expect<InvalidOperationException>(() => host.InstallAsync(good, Confirm));
        Check(host.List().Length == 1 && !File.Exists(Path.Combine(root, "escape")), "failed install changed registered package");

        await Task.WhenAll(host.StartAsync(id), host.StartAsync(id));
        await Eventually(() => host.List().Single().Status.State == "needs_configuration", "configuration state");
        string firstPid = File.ReadAllText(flag("pid"));
        string childPid = File.ReadAllText(flag("child-pid"));
        string oldToken = File.ReadAllText(flag("token"));
        string hostAddress = File.ReadAllText(flag("address"));
        Uri? firstUrl = null;
        await host.OpenSettingsAsync(id, url => firstUrl = url);
        Check(firstUrl?.AbsoluteUri == File.ReadAllText(flag("settings")), "current settings address mismatch");
        Check(File.ReadAllText(flag("pid")) == firstPid, "opening settings launched a duplicate process");

        var heartbeat = example.GetProperty("configuration");
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        using (var request = new HttpRequestMessage(HttpMethod.Post, hostAddress + "/v1/heartbeat"))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer incorrect");
            request.Content = JsonContent.Create(heartbeat);
            using var response = await http.SendAsync(request);
            var error = await response.Content.ReadFromJsonAsync<PluginError>(PluginPackages.Json);
            Check(response.StatusCode == HttpStatusCode.Unauthorized
                && error?.Code == example.GetProperty("expiredToken").GetProperty("code").GetString(), "invalid token admitted");
        }
        using (var request = new HttpRequestMessage(HttpMethod.Post, hostAddress + "/v1/heartbeat"))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + oldToken);
            request.Content = JsonContent.Create(new { state = "ready", summary = new string('x', 70_000) });
            using var response = await http.SendAsync(request);
            Check((int)response.StatusCode == 413, "oversize body did not return 413");
        }

        File.WriteAllText(flag("configured"), "yes");
        await Eventually(() => host.List().Single().Status.State == "ready", "hot configuration");
        File.WriteAllText(flag("restart"), "yes");
        await Eventually(() => host.List().Single().Status.RestartRequired, "restart notice");
        File.Delete(flag("restart"));
        await host.RestartAsync(id);
        Uri? secondUrl = null;
        await host.OpenSettingsAsync(id, url => secondUrl = url);
        Check(secondUrl?.AbsoluteUri == File.ReadAllText(flag("settings")) && File.ReadAllText(flag("token")) != oldToken,
            "restart reused old instance");
        await Eventually(() => Exited(firstPid) && Exited(childPid), "job stopped plugin and its child");
        using (var request = new HttpRequestMessage(HttpMethod.Post, hostAddress + "/v1/heartbeat"))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + oldToken);
            request.Content = JsonContent.Create(heartbeat);
            using var response = await http.SendAsync(request);
            Check(response.StatusCode == HttpStatusCode.Unauthorized, "revoked token still admitted");
        }

        var crashPid = File.ReadAllText(flag("pid"));
        File.WriteAllText(flag("crash"), "yes");
        await Eventually(() => File.ReadAllText(flag("pid")) != crashPid && host.List().Single().Status.State == "ready", "crash restart");
        await CheckPublicApi(host, example, http, hostAddress, File.ReadAllText(flag("token")));
        await host.StopAsync(id);
        var stoppedPid = File.ReadAllText(flag("pid"));
        await host.OpenSettingsAsync(id, _ => { });
        Check(stoppedPid != File.ReadAllText(flag("pid")), "opening settings did not start stopped plugin");
        await host.SetAutoStartAsync(id, true);
        await host.StopAsync(id);
        File.WriteAllText(flag("bad-url"), "yes");
        await Expect<InvalidOperationException>(() => host.OpenSettingsAsync(id, _ => throw new Exception("external settings URL opened")));
        File.Delete(flag("bad-url"));
        File.WriteAllText(flag("no-hello"), "yes");
        await Expect<TimeoutException>(() => host.StartAsync(id));
        await Eventually(() => Exited(File.ReadAllText(flag("child-pid"))), "handshake timeout leaked process tree");
        File.Delete(flag("no-hello"));
        Check(host.List().Single().Installation.AutoStart, "automatic startup preference was lost");
    }

    await using (var host = new PluginHost(root, logger))
    {
        await host.StartAutomaticAsync();
        await Eventually(() => host.List().Single().Status.State == "ready", "automatic startup");
        File.WriteAllText(flag("ignore-stop"), "yes");
        var pid = File.ReadAllText(flag("pid"));
        var child = File.ReadAllText(flag("child-pid"));
        await host.StopAsync(id, disable: true);
        await Eventually(() => Exited(pid) && Exited(child), "force stop leaked plugin process");
        Check(!host.List().Single().Installation.AutoStart, "disable did not clear automatic startup");
        await host.UninstallAsync(id, keepData: true);
        Check(host.List().Length == 0 && File.Exists(flag("configured")), "keep-data uninstall removed configuration");
        await host.InstallAsync(good, Confirm);
        await host.UninstallAsync(id, keepData: false);
        Check(!Directory.Exists(data), "remove-data uninstall kept configuration");
    }
    await CheckCatalog(original, payload);
    Console.WriteLine("Plugin host passed package rollback, lifecycle, notification deduplication, state projection, replay and stream limits.");
}
catch
{
    if (File.Exists(flag("http-error"))) Console.Error.WriteLine(File.ReadAllText(flag("http-error")));
    throw;
}
finally
{
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", originalKey);
    await logger.StopAsync();
    Directory.Delete(root, recursive: true);
}

string WriteZip(JsonObject manifest, string name, byte[] executable, bool corruptHash = false, bool escape = false)
{
    string path = Path.Combine(root, name + ".zip");
    var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
    var entry = original["entry"]!["executable"]!.GetValue<string>();
    using var stream = File.Create(path);
    using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
    void Add(string relative, byte[] bytes)
    {
        using var target = zip.CreateEntry(relative).Open();
        target.Write(bytes);
    }
    Add("plugin.json", manifestBytes);
    Add(entry, executable);
    Add("files.json", JsonSerializer.SerializeToUtf8Bytes(new
    {
        files = new[]
        {
            new { path = "plugin.json", sha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)) },
            new { path = entry, sha256 = corruptHash ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(executable)) }
        }
    }));
    if (escape) Add("../escape", [1]);
    return path;
}

async Task CheckCatalog(JsonObject manifest, byte[] executable)
{
    using var directoryKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var publisherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var format = DSASignatureFormat.Rfc3279DerSequence;
    var algorithm = HashAlgorithmName.SHA256;
    var publisher = manifest["publisher"]!.GetValue<string>();
    var name = manifest["name"]!.GetValue<string>();
    var publicKey = Convert.ToBase64String(publisherKey.ExportSubjectPublicKeyInfo());
    const string catalogRoute = "/catalog.json";
    const string originalVersion = "0.1.0";
    await using var transport = await CatalogFixture.StartAsync();
    var source = new CatalogSource(transport.Address + catalogRoute, Convert.ToBase64String(directoryKey.ExportSubjectPublicKeyInfo()));
    var first = Entry(originalVersion, "", 1);
    var bad = Entry("0.2.0", "--fail-migration", 2);
    var second = Entry("0.3.0", "--migrate", 2);
    var incompatible = second with { Version = "5.0.0", HostProtocol = new(2, 2) };
    var index = new CatalogIndex(1, DateTimeOffset.UtcNow.AddDays(1), [new(publisher, publicKey)],
        [first, bad, second, incompatible]);
    byte[] body = JsonSerializer.SerializeToUtf8Bytes(index, PluginPackages.Json);
    SetDirectory(invalidSignature: true);

    await using (var host = new PluginHost(root, logger, catalogTransport: transport.CreateClientHandler()))
    {
        await Expect<InvalidDataException>(() => host.ReadCatalogAsync(source, default));
        Check(host.CatalogSettings() is null && host.List().Length == 0, "untrusted catalog changed state");
        SetDirectory(invalidSignature: false);
        var loaded = await host.ReadCatalogAsync(source, default);
        Check(host.CatalogSettings() == source, "trusted catalog settings did not persist");
        var packages = loaded.Plugins.ToDictionary(item => item.Version);
        await Expect<InvalidDataException>(() => host.ApplyCatalogAsync(loaded, packages["5.0.0"]));
        Check(transport.Requests("/plugin-0.3.0.zip") == 0, "downloaded an incompatible version");
        await host.ApplyCatalogAsync(loaded, packages[originalVersion]);
        var installed = host.List().Single().Installation;
        Check(installed.Manifest.Version == originalVersion && !installed.Enabled && installed.PublisherKey == publicKey,
            "signed package was not registered correctly");
        Directory.CreateDirectory(data);
        File.WriteAllText(flag("configured"), "original settings");
        await host.StartAsync(id);
        await host.SetAutoStartAsync(id, true);
        await Eventually(() => host.List().Single().Status.State == "ready", "signed plugin start");

        byte[] signedUpdate = transport.Files["/plugin-0.3.0.zip"];
        transport.Files["/plugin-0.3.0.zip"] = [1, 2, 3];
        await Expect<InvalidDataException>(() => host.ApplyCatalogAsync(loaded, packages["0.3.0"]));
        Check(host.List().Single().Installation.Manifest.Version == originalVersion, "corrupt update changed installation");
        transport.Files["/plugin-0.3.0.zip"] = signedUpdate;

        var invalid = packages["0.3.0"] with { PackageSignature = Convert.ToBase64String([0, 1]) };
        var wrong = loaded with { Plugins = [invalid] };
        await Expect<InvalidDataException>(() => host.ApplyCatalogAsync(wrong, invalid));
        Check(host.List().Single().Installation.Manifest.Version == originalVersion, "invalid publisher signature changed installation");

        await Expect<InvalidOperationException>(() => host.ApplyCatalogAsync(loaded, packages["0.2.0"]));
        await Eventually(() => host.List().Single().Status.State == "ready", "previous version restored after failure");
        installed = host.List().Single().Installation;
        Check(installed.Manifest.Version == originalVersion && installed.AutoStart
            && File.ReadAllText(flag("configured")) == "original settings", "failed migration changed data or preferences");

        await host.ApplyCatalogAsync(loaded, packages["0.3.0"]);
        installed = host.List().Single().Installation;
        Check(installed is { DataVersion: 2, AutoStart: true, Backup.Manifest.Version: originalVersion }
            && File.ReadAllText(flag("configured")) == "migrated", "successful update did not retain backup");
    }

    await using (var host = new PluginHost(root, logger))
    {
        await host.StartAutomaticAsync();
        await host.RollbackAsync(id);
        var installed = host.List().Single().Installation;
        Check(installed is { DataVersion: 1, AutoStart: true, Backup: null } && installed.Manifest.Version == originalVersion
            && File.ReadAllText(flag("configured")) == "original settings", "manual rollback failed after restart");
    }

    var registry = new PluginRegistry(root);
    registry.BeginUpdate(id);
    File.WriteAllText(flag("configured"), "interrupted migration");
    registry.UseVersion(registry.Find(id) with { Manifest = registry.Find(id).Manifest with { Version = "0.4.0" } });
    registry = new PluginRegistry(root);
    Check(registry.Find(id).Manifest.Version == originalVersion && File.ReadAllText(flag("configured")) == "original settings",
        "interrupted transaction did not restore settings and registration");
    Console.WriteLine("Signed catalog passed incompatibility, invalid signatures, corrupt download, migration failure, version rollback and crash recovery.");

    CatalogEntry Entry(string version, string args, int dataVersion)
    {
        var copy = manifest.DeepClone().AsObject();
        copy["version"] = version;
        if (args.Length > 0) copy["entry"]!["args"]!.AsArray().Add(args);
        var zip = WriteZip(copy, "catalog-" + version, executable);
        var bytes = File.ReadAllBytes(zip);
        var hash = SHA256.HashData(bytes);
        var route = "/plugin-" + version + ".zip";
        transport.Files[route] = bytes;
        return new(id, name, publisher, version, new(1, 1), transport.Address + route,
            Convert.ToHexStringLower(hash), Convert.ToBase64String(publisherKey.SignData(hash, algorithm, format)), dataVersion);
    }

    void SetDirectory(bool invalidSignature)
    {
        var signature = invalidSignature ? publisherKey.SignData(body, algorithm, format) : directoryKey.SignData(body, algorithm, format);
        transport.Files[catalogRoute] = JsonSerializer.SerializeToUtf8Bytes(
            new CatalogEnvelope(Convert.ToBase64String(body), Convert.ToBase64String(signature)), PluginPackages.Json);
    }
}

static Task<bool> Confirm(PluginManifest _) => Task.FromResult(true);
static void Check(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException(reason);
}

static async Task Expect<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

static async Task Eventually(Func<bool> condition, string reason)
{
    var started = Stopwatch.GetTimestamp();
    while (!condition())
    {
        if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(8)) throw new TimeoutException(reason);
        await Task.Delay(50);
    }
}

static bool Exited(string pid)
{
    try
    {
        using var process = Process.GetProcessById(int.Parse(pid));
        return process.HasExited;
    }
    catch (ArgumentException) { return true; }
}

static async Task CheckPublicApi(PluginHost host, JsonElement fixture, HttpClient http, string address, string credential)
{
    using var time = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var token = time.Token;
    var noticeCount = 0;
    host.Notice += (_, _) => Interlocked.Increment(ref noticeCount);
    async Task<HttpResponseMessage> Request(HttpMethod method, string route, object? body = null, string? lastId = null)
    {
        using var request = new HttpRequestMessage(method, address + route);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + credential);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (lastId is not null) request.Headers.TryAddWithoutValidation("Last-Event-ID", lastId);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }
    void Push(string type, object body) => host.ApplyRuntimeEvent(type, JsonSerializer.SerializeToElement(body));
    async Task<JsonElement> Snapshot()
    {
        using var response = await Request(HttpMethod.Get, "/v1/state");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
    }

    Check((await Snapshot()).GetProperty("sessionId").ValueKind == JsonValueKind.Null, "missing empty session field");
    Push("status", new { gameSessionId = "save-session-1", ready = true, progress = "internal-private",
        controlOwner = "agent", gameDate = "1:spring:5", timeOfDay = 820 });
    var snapshot = await Snapshot();
    Check(!snapshot.GetRawText().Contains("internal-private", StringComparison.Ordinal)
        && snapshot.GetProperty("game").GetProperty("timeOfDay").GetInt32() == 820, "public state leaked internal data");
    string from = snapshot.GetProperty("cursor").GetString()!;
    var reply = new { gameSessionId = "save-session-1", status = "completed", requestId = "reply-1", replyText = "你好" };
    Push("life.chat.reply", reply);
    Push("life.chat.reply", reply);
    var afterReply = (await Snapshot()).GetProperty("cursor").GetString();
    Check(long.Parse(afterReply!) == long.Parse(from) + 1, "duplicate reply became a second event");
    Push("result", new { gameSessionId = "save-session-1", commandId = "action-1", skillId = "water-zone",
        terminalState = "partially-succeeded", completedCount = 1, privateDetail = "never-publish" });

    string streamRoute = "/v1/events?sessionId=save-session-1&cursor=" + from;
    using var streamA = await Request(HttpMethod.Get, streamRoute);
    using var streamB = await Request(HttpMethod.Get, streamRoute, lastId: afterReply);
    using var limit = await Request(HttpMethod.Get, streamRoute);
    Check((int)limit.StatusCode == 429, "subscription count not bounded");
    using var readerA = new StreamReader(await streamA.Content.ReadAsStreamAsync(token));
    using var readerB = new StreamReader(await streamB.Content.ReadAsStreamAsync(token));
    Check((await Frame(readerA, token)).Contains("agent.reply.completed", StringComparison.Ordinal), "missed event after snapshot");
    var resumed = await Frame(readerB, token);
    Check(resumed.Contains("agent.action.completed", StringComparison.Ordinal) && !resumed.Contains("never-publish", StringComparison.Ordinal),
        "Last-Event-ID not respected or private detail exposed");

    Push("status", new { gameSessionId = "next-session", ready = true });
    Check(await readerB.ReadLineAsync(token) is null, "save switch left old stream connected");
    using var stale = await Request(HttpMethod.Get, streamRoute);
    Check((int)stale.StatusCode == 409, "old session cursor accepted");
    string nextCursor = (await Snapshot()).GetProperty("cursor").GetString()!;
    for (int index = 0; index < 260; index++)
        Push("status", new { gameSessionId = "next-session", ready = true, timeOfDay = index });
    using var expired = await Request(HttpMethod.Get, "/v1/events?sessionId=next-session&cursor=" + nextCursor);
    var expiredError = await expired.Content.ReadFromJsonAsync<PluginError>(PluginPackages.Json, token);
    Check((int)expired.StatusCode == 409 && expiredError?.Code == fixture.GetProperty("expiredCursor").GetProperty("code").GetString(),
        "expired cursor was not rejected");

    var notice = new { version = 1, type = "notification", messageId = "notice-1", at = "2025-01-01T00:00:00Z",
        payload = new { text = "测试通知" } };
    using var accepted = await Request(HttpMethod.Post, "/v1/messages", notice);
    using var duplicate = await Request(HttpMethod.Post, "/v1/messages", notice);
    using var opaque = await Request(HttpMethod.Post, "/v1/messages", new { version = 1, type = "event", messageId = "event-1",
        at = "2025-01-01T00:00:00Z", payload = new { arbitrarilyNested = new[] { 1, 2 } } });
    Check(accepted.StatusCode == HttpStatusCode.NoContent && duplicate.StatusCode == HttpStatusCode.NoContent
        && opaque.StatusCode == HttpStatusCode.NoContent && noticeCount == 1, "message or deduplication failed");
    using var command = await Request(HttpMethod.Post, "/v1/commands", fixture.GetProperty("command").GetProperty("request"));
    Check((int)command.StatusCode == 503, "missing agent runtime did not return an unavailable result");

    var isolated = new PluginEvents();
    var manifest = fixture.GetProperty("package").GetProperty("manifest").Deserialize<PluginManifest>(PluginPackages.Json)!;
    var instance = new PluginInstance(new InstalledPlugin(manifest, true, false));
    using var slow = isolated.Subscribe(instance, null, 0, PluginEvents.Topics).Stream!;
    for (int index = 0; index < 258; index++)
        isolated.Apply("status", JsonSerializer.SerializeToElement(new { timeOfDay = index }));
    Check(slow.End.IsCancellationRequested, "slow subscriber was not released");
}

static async Task<string> Frame(StreamReader reader, CancellationToken token)
{
    string result = "";
    while (await reader.ReadLineAsync(token) is { } line)
    {
        if (line.Length == 0 && result.Length > 0) return result;
        if (!line.StartsWith(':')) result += line;
    }
    throw new InvalidOperationException("Expected a plugin event.");
}

sealed class CatalogFixture : IAsyncDisposable
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _requests = new();
    private readonly WebApplication _server;
    private readonly X509Certificate2 _certificate;
    internal System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Files { get; } = new();
    internal string Address => _server.Urls.Single();

    private CatalogFixture(WebApplication server, X509Certificate2 certificate)
    {
        _server = server;
        _certificate = certificate;
    }

    internal int Requests(string route) => _requests.GetValueOrDefault(route);

    internal static async Task<CatalogFixture> StartAsync()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var subject = new SubjectAlternativeNameBuilder();
        subject.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(subject.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        var fixture = new CatalogFixture(app, certificate);
        app.Run(fixture.RespondAsync);
        await app.StartAsync();
        return fixture;
    }

    internal HttpClientHandler CreateClientHandler() => new()
    {
        UseProxy = false, AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            certificate?.Thumbprint == _certificate.Thumbprint && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == 0
    };

    private async Task RespondAsync(HttpContext context)
    {
        string route = context.Request.Path;
        _requests.AddOrUpdate(route, 1, (_, count) => count + 1);
        if (Files.TryGetValue(route, out var bytes))
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        else context.Response.StatusCode = 404;
    }

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync();
        await _server.DisposeAsync();
        _certificate.Dispose();
    }
}
