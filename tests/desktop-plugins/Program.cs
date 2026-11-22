using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    Console.WriteLine("Plugin host passed package rollback, lease, handshake, input limits, settings, crash/timeout recovery, automatic startup and owned tree cleanup.");
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
