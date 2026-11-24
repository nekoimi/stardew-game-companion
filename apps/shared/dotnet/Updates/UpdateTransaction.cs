using System.IO;
using System.Text.Json;

namespace StardewAI.Companion.Updates;

internal sealed record PendingUpdate(Guid Id, string[] OldFiles, string[] NewFiles);
internal sealed record UpdateResult(bool Installed, string Detail);
internal sealed record ConfirmedUpdate(Guid Id, string ManifestHash);

internal sealed class UpdateTransaction
{
    private readonly string _root;
    private readonly string _updates;
    private readonly string _journal;

    internal UpdateTransaction(string root)
    {
        _root = Path.GetFullPath(root);
        _updates = UpdatePackage.SafePath(_root, ".updates");
        _journal = UpdatePackage.SafePath(_updates, "pending.json");
    }

    internal string Stage(Guid id) => UpdatePackage.SafePath(_updates, id.ToString("N"));
    internal string Payload(Guid id) => UpdatePackage.SafePath(Stage(id), "payload");
    internal bool HasPendingUpdate => File.Exists(_journal);
    internal Guid PendingId => UpdatePackage.ReadJson<PendingUpdate>(_journal).Id;
    internal string ResultPath => UpdatePackage.SafePath(_updates, "result.json");
    private string AuthorizationPath(Guid id) => UpdatePackage.SafePath(Stage(id), "confirmed");
    internal void AuthorizeInstall(Guid id, string manifestHash) =>
        WriteJson(AuthorizationPath(id), new ConfirmedUpdate(id, manifestHash));

    internal void ConsumeAuthorization(Guid id)
    {
        var path = AuthorizationPath(id);
        if (!File.Exists(path) || UpdatePackage.ReadJson<ConfirmedUpdate>(path) is not { } authorization
            || authorization.Id != id)
            throw new InvalidOperationException("桌面未完成更新交接。");
        File.Delete(path);
        if (UpdatePackage.ManifestHash(Payload(id)) != authorization.ManifestHash)
            throw new InvalidDataException("已确认的发行清单发生变化，请重新校验更新包。");
    }

    internal void DiscardStage(Guid id)
    {
        try
        {
            var stage = Stage(id);
            if ((!HasPendingUpdate || PendingId != id) && Directory.Exists(stage))
                Directory.Delete(stage, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (InvalidDataException) { }
    }

    internal void CleanFinishedStages()
    {
        if (HasPendingUpdate || !Directory.Exists(_updates)) return;
        var stages = Directory.EnumerateDirectories(_updates)
            .Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _)
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0).ToArray();
        string? latest = stages.Select(path => Path.Combine(path, "committed.json"))
            .Where(File.Exists).MaxBy(File.GetLastWriteTimeUtc);
        foreach (var stage in stages)
        {
            if (stage == Path.GetDirectoryName(latest))
            {
                DeleteStagePayload(stage);
                continue;
            }
            DiscardStage(Guid.ParseExact(Path.GetFileName(stage), "N"));
        }
    }

    private static void DeleteStagePayload(string stage)
    {
        try
        {
            var payload = UpdatePackage.SafePath(stage, "payload");
            if (Directory.Exists(payload)) Directory.Delete(payload, recursive: true);
            File.Delete(UpdatePackage.SafePath(stage, "updater.exe"));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (InvalidDataException) { }
    }

    internal void StartHelper(string operation, Guid id)
    {
        var stage = Stage(id);
        var helper = UpdatePackage.SafePath(stage, "updater.exe");
        if (operation == "install")
        {
            Directory.CreateDirectory(stage);
            File.Copy(UpdatePackage.SafePath(_root, UpdatePackage.Helper), helper, overwrite: true);
        }
        UpdateProcess.StartHelper(_root, helper, operation, id);
    }

    internal FileStream AcquireLease()
    {
        Directory.CreateDirectory(_updates);
        return File.Open(UpdatePackage.SafePath(_updates, "update.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    internal UpdateResult Apply(Guid id, Action<string> checkLauncher, Action verifyLaunch)
    {
        if (HasPendingUpdate) throw new InvalidOperationException("请先恢复上一次更新。");
        var stage = Stage(id);
        var payload = Payload(id);
        var backup = UpdatePackage.SafePath(stage, "program-backup");
        var oldFiles = UpdatePackage.ReadManifest(_root, currentPackage: false);
        var newFiles = UpdatePackage.VerifyPayload(payload, CancellationToken.None);
        var info = UpdatePackage.ReadInfo(payload)!;
        var data = PortableData.Open(_root);
        if (data.External is not null && info.DataProfileVersion != 1)
            throw new InvalidDataException("目标版本与当前外部数据目录不兼容。");
        using var dataLease = data.AcquireExternalLease();
        using var sessionLease = data.AcquireSessionLease();
        using var pluginLease = data.AcquirePluginLease();
        checkLauncher(payload);
        var next = new List<string>();
        var previous = new List<string>();
        foreach (var path in Ordered(oldFiles.Keys.Concat(newFiles.Keys).Append(UpdatePackage.Manifest)))
        {
            var target = UpdatePackage.SafePath(_root, path);
            bool exists = File.Exists(target);
            bool listed = oldFiles.TryGetValue(path, out var old);
            bool delivered = newFiles.TryGetValue(path, out var replacement);
            if (exists)
            {
                if (path != UpdatePackage.Manifest && !listed)
                    throw new IOException($"新版文件与未纳入发行清单的本地文件同名（{path}）。");
                if (delivered && string.Equals(old?.Sha256, replacement!.Sha256, StringComparison.OrdinalIgnoreCase)
                    && UpdatePackage.FileHasHash(target, replacement.Sha256))
                    continue;
                CopyFile(target, UpdatePackage.SafePath(backup, path));
                previous.Add(path);
            }
            if (delivered || path == UpdatePackage.Manifest) next.Add(path);
        }
        data.Backup(UpdatePackage.SafePath(stage, "data-backup"));
        var pending = new PendingUpdate(id, [.. previous], [.. next]);
        ValidateJournalPaths(pending.OldFiles);
        WriteJson(_journal, pending);
        try
        {
            foreach (var path in next)
                ReplaceFile(UpdatePackage.SafePath(payload, path), UpdatePackage.SafePath(_root, path), stage);
            foreach (var path in previous.Except(next, StringComparer.OrdinalIgnoreCase))
                File.Delete(UpdatePackage.SafePath(_root, path));
            verifyLaunch();
            var result = new UpdateResult(true, $"已更新到 {info.Tag}。");
            WriteJson(ResultPath, result);
            File.Delete(_journal);
            try { WriteJson(UpdatePackage.SafePath(stage, "committed.json"), result); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return result;
        }
        catch (Exception error)
        {
            Recover($"新版安装失败，已恢复更新前的程序文件。{error.Message}");
            throw;
        }
    }

    internal void Recover(string? detail = null)
    {
        if (!HasPendingUpdate) return;
        var pending = UpdatePackage.ReadJson<PendingUpdate>(_journal);
        ValidateJournalPaths(pending.OldFiles);
        ValidateJournalPaths(pending.NewFiles);
        var stage = Stage(pending.Id);
        var backup = UpdatePackage.SafePath(stage, "program-backup");
        foreach (var path in pending.OldFiles)
            if (!File.Exists(UpdatePackage.SafePath(backup, path)))
                throw new IOException("更新备份不完整，无法恢复旧版。");
        foreach (var path in pending.OldFiles)
            ReplaceFile(UpdatePackage.SafePath(backup, path), UpdatePackage.SafePath(_root, path), stage);
        foreach (var path in pending.NewFiles.Except(pending.OldFiles, StringComparer.OrdinalIgnoreCase))
            File.Delete(UpdatePackage.SafePath(_root, path));
        WriteJson(ResultPath, new UpdateResult(false, detail ?? "更新未完成，已恢复更新前的程序文件。"));
        File.Delete(_journal);
    }

    internal static void CopyFile(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = File.OpenRead(source);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static void ReplaceFile(string source, string destination, string stage)
    {
        string temporary = UpdatePackage.SafePath(stage, "replace.tmp");
        File.Delete(temporary);
        CopyFile(source, temporary);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(temporary, destination, overwrite: true);
    }

    private static void ValidateJournalPaths(string[] paths)
    {
        if (paths is not { Length: > 0 } || !paths.Contains(UpdatePackage.Manifest)
            || paths.Any(path => !UpdatePackage.AllowedFile(path))
            || paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new InvalidDataException("更新事务文件无效。");
    }

    internal static void WriteJson<T>(string path, T data)
    {
        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, data, JsonSerializerOptions.Web);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static string[] Ordered(IEnumerable<string> paths) => paths.Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(path => path == UpdatePackage.Launcher).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
}
