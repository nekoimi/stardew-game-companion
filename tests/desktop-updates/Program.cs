using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using StardewAI.Companion.Desktop;
using StardewAI.Companion.Updates;

string workspace = Directory.CreateTempSubdirectory("stardew-updates-").FullName;
try
{
    if (args is ["--verify-release", var archive, var publisherKey, var tag, var channel])
    {
        VerifyReleaseArchive(archive, publisherKey, tag, channel);
        Console.WriteLine($"Verified signed release {tag} ({channel}).");
    }
    else if (args.Length == 0)
    {
        VerifyArchiveAndApply();
        RejectUnlistedPayload();
        RestoreInterruptedUpdate();
        ImportPortableData();
        await VerifyPublishedUpdatesAsync();
        Console.WriteLine("Windows portable update checks passed.");
    }
    else
    {
        throw new ArgumentException("Expected --verify-release <archive> <public-key-file> <tag> <channel>.");
    }
}
finally { Directory.Delete(workspace, recursive: true); }

void VerifyReleaseArchive(string archive, string publisherKey, string tag, string channel)
{
    string hash = UpdatePackage.ReadChecksum(archive + ".sha256", Path.GetFileName(archive));
    UpdateSignature.VerifyFile(archive + UpdateSignature.Suffix, hash, File.ReadAllBytes(publisherKey));
    var info = UpdatePackage.Extract(archive, hash, Path.Combine(workspace, "payload"), CancellationToken.None);
    Check(info.Tag == tag && info.Channel == channel, $"The package identifies {info.Tag} ({info.Channel}).");
}

void VerifyArchiveAndApply()
{
    string root = Path.Combine(workspace, "install");
    WriteRelease(root, "v0.1.0", "old");
    WriteFile(root, "data/companion.sqlite", "saved database");
    WriteFile(root, "data/companion.sqlite-wal", "pending SQLite pages");
    WriteFile(root, "plugins/example/plugin.json", "installed plugin");
    WriteFile(root, "Mods/StardewAI.Companion.Mod/config.json", "user configuration");
    WriteFile(root, "Mods/StardewAI.Companion.Mod/data/save.json", "saved partner");
    string source = Path.Combine(workspace, "next");
    WriteRelease(source, "v0.2.0", "new");
    var transaction = new UpdateTransaction(root);
    using var lease = transaction.AcquireLease();
    Expect<IOException>(() => transaction.AcquireLease().Dispose());
    var zip = Path.Combine(workspace, "release.zip");
    ZipFile.CreateFromDirectory(source, zip);
    var id = Guid.NewGuid();
    var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
    Check(UpdatePackage.Extract(zip, hash, transaction.Payload(id), CancellationToken.None).Tag == "v0.2.0",
        "The archive must resolve to the actual release tag.");

    Check(transaction.Apply(id, payload =>
    {
        Check(File.ReadAllText(Path.Combine(payload, UpdatePackage.Launcher)) == "new", "Check the staged launcher.");
        Check(File.ReadAllText(Path.Combine(root, UpdatePackage.Launcher)) == "old", "Check before changing the installed launcher.");
    }, () =>
    {
        Check(File.ReadAllText(Path.Combine(root, UpdatePackage.Launcher)) == "new", "Health check must run after replacement.");
        Check(!File.Exists(Path.Combine(root, "runtime", "dist", "old.js")), "Obsolete managed files must be removed.");
    }).Installed, "A successful health check must commit the update.");
    Check(!transaction.HasPendingUpdate, "The journal must be cleared after commit.");
    Check(File.ReadAllText(Path.Combine(root, "data", "companion.sqlite-wal")) == "pending SQLite pages", "WAL must survive updates.");
    Check(File.ReadAllText(Path.Combine(root, "plugins", "example", "plugin.json")) == "installed plugin", "Plugin must survive.");
    Check(File.ReadAllText(Path.Combine(transaction.Stage(id), "data-backup", "Mods", "StardewAI.Companion.Mod", "config.json"))
        == "user configuration", "The Mod settings must be backed up.");
    Check(File.ReadAllText(Path.Combine(root, "Mods", "StardewAI.Companion.Mod", "data", "save.json")) == "saved partner",
        "Mod save data must survive updates.");

    var failed = Guid.NewGuid();
    WriteRelease(transaction.Payload(failed), "v0.3.0", "broken");
    Expect<InvalidOperationException>(() => transaction.Apply(failed, _ => { }, () => throw new InvalidOperationException("health failure")));
    Check(UpdatePackage.ReadInfo(root)?.Tag == "v0.2.0", "Failed health check must restore the previous version.");
    Check(File.ReadAllText(Path.Combine(root, UpdatePackage.Launcher)) == "new", "Rollback must restore the previous executable.");
    Check(!File.Exists(Path.Combine(root, "runtime", "dist", "broken.js")), "Rollback must remove newly installed files.");
    Check(!transaction.HasPendingUpdate, "Successful rollback must clear the journal.");

    var invalidLauncher = Guid.NewGuid();
    WriteRelease(transaction.Payload(invalidLauncher), "v0.3.0", "invalid-launcher");
    Expect<IOException>(() => transaction.Apply(invalidLauncher, _ => throw new IOException("could not start"),
        () => throw new InvalidOperationException("Post-install health check must be unreachable.")));
    Check(UpdatePackage.ReadInfo(root)?.Tag == "v0.2.0" && !transaction.HasPendingUpdate,
        "An invalid staged launcher must leave the installed package and journal untouched.");
}

void RejectUnlistedPayload()
{
    var source = Path.Combine(workspace, "malicious");
    WriteRelease(source, "v0.2.0", "new");
    var zip = Path.Combine(workspace, "invalid.zip");
    ZipFile.CreateFromDirectory(source, zip);
    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        archive.CreateEntry("data/desktop-settings.json");
    string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
    Expect<InvalidDataException>(() => UpdatePackage.Extract(zip, hash, Path.Combine(workspace, "rejected"), CancellationToken.None));
    Check(!File.Exists(Path.Combine(workspace, "rejected", "data", "desktop-settings.json")), "An update archive must never write user data.");
}

void RestoreInterruptedUpdate()
{
    string root = Path.Combine(workspace, "interrupted");
    WriteRelease(root, "v0.1.0", "old");
    var transaction = new UpdateTransaction(root);
    var id = Guid.NewGuid();
    var stage = transaction.Stage(id);
    var managed = UpdatePackage.ReadManifest(root, currentPackage: true).Keys.Append(UpdatePackage.Manifest).ToArray();
    foreach (var path in managed)
        WriteFile(Path.Combine(stage, "program-backup"), path, File.ReadAllText(Path.Combine(root, path)));
    UpdateTransaction.WriteJson(Path.Combine(root, ".updates", "pending.json"),
        new PendingUpdate(id, managed, [.. managed, "runtime/dist/new.js"]));
    WriteFile(root, UpdatePackage.Launcher, "new");
    WriteFile(root, "runtime/dist/new.js", "partial install");

    transaction.Recover();
    Check(File.ReadAllText(Path.Combine(root, UpdatePackage.Launcher)) == "old", "Startup recovery must restore the original launcher.");
    Check(!File.Exists(Path.Combine(root, "runtime", "dist", "new.js")), "Startup recovery must remove the partial install.");
    Check(!transaction.HasPendingUpdate, "Recovery must reach a stable state.");
}

void ImportPortableData()
{
    string sourceRoot = Path.Combine(workspace, "old-portable");
    string newRoot = Path.Combine(workspace, "new-portable");
    string profileRoot = Path.Combine(workspace, "profile");
    Directory.CreateDirectory(newRoot);
    WriteFile(sourceRoot, "data/companion.sqlite-wal", "committed WAL");
    WriteFile(sourceRoot, "plugins/example/plugin.json", "plugin");
    WriteFile(sourceRoot, "Mods/StardewAI.Companion.Mod/config.json", "configuration");
    WriteFile(sourceRoot, "Mods/StardewAI.Companion.Mod/data/conversations/save.json", "memory");
    WriteFile(sourceRoot, "Mods/StardewAI.Companion.Mod/data/transport-discovery.json", "transient session");

    var source = PortableData.Open(sourceRoot);
    PortableData.CreateFrom(source, profileRoot);
    Expect<IOException>(() => PortableData.CreateFrom(source, profileRoot));
    PortableData.Link(newRoot, profileRoot);
    var imported = PortableData.Open(newRoot);
    using var lease = imported.AcquireExternalLease();
    Expect<IOException>(() => imported.AcquireExternalLease());
    Check(imported.CredentialRoot == source.CredentialRoot, "Import must preserve the Windows credential namespace.");
    Check(File.ReadAllText(Path.Combine(imported.Data, "companion.sqlite-wal")) == "committed WAL", "Import must preserve WAL.");
    Check(File.ReadAllText(imported.ModConfig) == "configuration", "Import must preserve Mod configuration.");
    Check(File.Exists(Path.Combine(imported.ModData, "conversations", "save.json")), "Import must preserve Mod history.");
    Check(File.Exists(Path.Combine(imported.Plugins, "example", "plugin.json")), "Import must preserve plugin packages.");
    Check(!File.Exists(Path.Combine(imported.ModData, "transport-discovery.json")), "Import must exclude stale session credentials.");
}

async Task VerifyPublishedUpdatesAsync()
{
    const string archiveName = "StardewGameCompanion-0.1.0-windows-x64.zip";
    const string github = "https://github.com/nekoimi/stardew-game-companion/releases/download";
    const string stableTag = "production-beta-label";
    const string previewTag = "v0.1.0-dev.20261124.202.1";
    string source = Path.Combine(workspace, "github-release");
    WriteRelease(source, stableTag, "production");
    string zip = Path.Combine(workspace, archiveName);
    ZipFile.CreateFromDirectory(source, zip);
    byte[] archive = File.ReadAllBytes(zip);
    byte[] hash = SHA256.HashData(archive);
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    byte[] signature = key.SignHash(hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    var publishedAt = DateTimeOffset.Parse("2026-11-24T12:00:00Z");

    object Release(string tag, bool preview, bool draft = false, bool signed = true)
    {
        var names = signed ? new[] { archiveName, archiveName + ".sha256", archiveName + ".sig" }
            : [archiveName, archiveName + ".sha256"];
        return new
        {
            tag_name = tag, draft, prerelease = preview, published_at = publishedAt, body = "Release notes",
            assets = names.Select(name => new
            {
                name, created_at = publishedAt, browser_download_url = $"{github}/{tag}/{name}",
                digest = name == archiveName ? $"sha256:{Convert.ToHexString(hash)}" : null
            })
        };
    }

    bool tampered = false;
    bool includeStable = true;
    bool interruptArchive = true;
    int archiveRequests = 0;
    using var http = new HttpClient(new TestHttpHandler(request =>
    {
        string path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith(".zip", StringComparison.Ordinal))
        {
            archiveRequests++;
            var content = new ByteArrayContent(interruptArchive ? archive[..(archive.Length / 2)] : archive);
            content.Headers.ContentLength = archive.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
        var listing = new List<object>
        {
            Release("draft", false, draft: true),
            Release("unsigned", false, signed: false),
            Release(previewTag, true),
        };
        if (includeStable) listing.Add(Release(stableTag, false));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = path.EndsWith(".sig") ? new ByteArrayContent(tampered ? new byte[64] : signature)
                : path.EndsWith(".sha256") ? new StringContent($"{Convert.ToHexString(hash)}  {archiveName}")
                : new StringContent(JsonSerializer.Serialize(listing))
        };
    }));
    var releases = new GitHubCompanionReleases(http);
    var stable = await releases.FindAsync(false, CancellationToken.None) ?? throw new InvalidOperationException("Missing stable.");
    var preview = await releases.FindAsync(true, CancellationToken.None) ?? throw new InvalidOperationException("Missing preview.");
    Check(stable.Tag == stableTag && preview.Tag == previewTag, "Channels must exclude drafts and unsigned legacy releases.");
    includeStable = false;
    Check(await releases.FindAsync(false, CancellationToken.None) is null,
        "A channel with only unsigned or draft assets must not fall back to the other channel.");
    Check(!stable.CanOfferTo(new(stableTag, "0.1.0", "stable", 1)), "The installed tag must not be offered again.");
    Check(!preview.CanOfferTo(new("v0.1.0-dev.20261124.203.1", "0.1.0", "preview", 1, publishedAt.AddHours(1))),
        "A same-channel build published before the installed build must be skipped.");
    Check(stable.CanOfferTo(new(previewTag, "0.1.0", "preview", 1)), "Switching to the stable channel must be available.");

    var transaction = new UpdateTransaction(Path.Combine(workspace, "download-install"));
    byte[] publicKey = key.ExportSubjectPublicKeyInfo();
    Task<ReleaseInfo> Download(PublishedUpdate release, Guid id) =>
        releases.DownloadAsync(release, transaction.Stage(id), transaction.Payload(id), publicKey,
            new Progress<int?>(), CancellationToken.None);

    var interrupted = Guid.NewGuid();
    await ExpectAsync<IOException>(() => Download(stable, interrupted));
    Check(!Directory.Exists(transaction.Payload(interrupted)), "An interrupted archive must never become installable.");
    transaction.DiscardStage(interrupted);
    Check(!Directory.Exists(transaction.Stage(interrupted)), "Discarding a failed download must remove partial assets.");
    interruptArchive = false;
    var retried = await Download(stable, Guid.NewGuid());
    Check(archiveRequests == 2 && retried.Tag == stableTag && retried.Channel == "stable",
        "Retry must download and validate a fresh copy of the same release.");

    var mismatched = stable with { AssetDigest = $"sha256:{new string('0', 64)}" };
    await ExpectAsync<InvalidDataException>(() => Download(mismatched, Guid.NewGuid()));
    Check(archiveRequests == 2, "A mismatch between asset metadata and checksum must prevent archive download.");

    tampered = true;
    await ExpectAsync<InvalidDataException>(() => Download(stable, Guid.NewGuid()));
    Check(archiveRequests == 2, "Bad signatures must prevent archive download.");
}

static void WriteRelease(string root, string tag, string content)
{
    string[] paths = [UpdatePackage.Launcher, UpdatePackage.Helper, "runtime/node.exe", "runtime/models.json",
        "runtime/dist/bootstrap.js", $"runtime/dist/{content}.js", "Mods/StardewAI.Companion.Mod/manifest.json",
        "Mods/StardewAI.Companion.Mod/StardewAI.Companion.Mod.dll", "Mods/StardewAI.Companion.Mod/docs/agent-ts-instructions.md",
        "docs/LICENSE", "docs/NOTICE"];
    foreach (var path in paths) WriteFile(root, path, content);
    UpdateTransaction.WriteJson(Path.Combine(root, UpdatePackage.Info), new ReleaseInfo(tag, tag[1..], "stable", 1));
    var files = paths.Append(UpdatePackage.Info).Select(path =>
        new ReleaseFile(path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, path)))))).ToArray();
    UpdateTransaction.WriteJson(Path.Combine(root, UpdatePackage.Manifest), new ReleaseManifest(2, files));
}

static void WriteFile(string root, string relative, string content)
{
    string path = Path.Combine(root, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
}

static void Check(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException(reason);
}

static void Expect<T>(Action run) where T : Exception
{
    try { run(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static async Task ExpectAsync<T>(Func<Task> run) where T : Exception
{
    try { await run(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class TestHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(respond(request));
}
