using System.Buffers;
using System.IO.Compression;
using System.Threading.Channels;
using Dalamud.Plugin.Services;
using MogVault.Store;

namespace MogVault.Capture;

internal abstract record Capture;

internal sealed record OwnerCapture(OwnerUpdate Update) : Capture;

internal sealed record DeleteCapture(ulong[]? OwnerIds) : Capture;

internal sealed record ContainerCapture(
    OwnerInfo Owner, RetainerInfo? Retainer, ushort InventoryType, long Ticks,
    Stack[] Buffer, int Count, StackExtra[]? ExtraBuffer, int ExtraCount) : Capture
{
    public (ulong Owner, ulong Retainer, ushort Type) Key => (Owner.Id, Retainer?.Id ?? 0, InventoryType);

    public ContainerUpdate ToUpdate() => new(
        Owner, Retainer, InventoryType, Ticks,
        Buffer.AsSpan(0, Count).ToArray(),
        ExtraBuffer is null ? [] : ExtraBuffer.AsSpan(0, ExtraCount).ToArray());

    public void Release()
    {
        ArrayPool<Stack>.Shared.Return(Buffer);
        if (ExtraBuffer is not null)
            ArrayPool<StackExtra>.Shared.Return(ExtraBuffer);
    }
}

internal sealed class VaultWriter : IDisposable
{
    private const int FlushDelayMs = 2000;

    private readonly Channel<Capture> channel = Channel.CreateUnbounded<Capture>(new UnboundedChannelOptions { SingleReader = true });
    private readonly VaultStore store;
    private readonly string directory;
    private readonly IPluginLog log;
    private readonly Action<ContainerUpdate[], ulong[]> published;
    private readonly Action loaded;
    private readonly int forgetFreeCompanyDays;
    private readonly int backupDays;
    private readonly Dictionary<ulong, long> flushDue = [];
    private readonly HashSet<string> unreadable = new(StringComparer.OrdinalIgnoreCase);
    private bool savingDisabled;
    private volatile bool isLoaded;
    private readonly Task loop;

    public VaultWriter(
        VaultStore store, string directory, IPluginLog log, Action<ContainerUpdate[], ulong[]> published, Action loaded,
        int forgetFreeCompanyDays, int backupDays)
    {
        this.store = store;
        this.directory = directory;
        this.log = log;
        this.published = published;
        this.loaded = loaded;
        this.forgetFreeCompanyDays = forgetFreeCompanyDays;
        this.backupDays = backupDays;
        loop = Task.Run(RunAsync);
    }

    public bool IsLoaded => isLoaded;

    public void Post(Capture capture)
    {
        if (!channel.Writer.TryWrite(capture))
            (capture as ContainerCapture)?.Release();
    }

    public void Dispose()
    {
        channel.Writer.TryComplete();
        if (!loop.Wait(TimeSpan.FromSeconds(5)))
            log.Warning("MogVault writer did not finish within 5s; last changes may be lost.");
    }

    private async Task RunAsync()
    {
        if (backupDays > 0)
            Backup();

        try
        {
            LoadAll();
        }
        catch (Exception ex)
        {
            savingDisabled = true;
            log.Error(ex, "Loading MogVault data failed; saving is disabled this session so existing files are not overwritten.");
        }

        isLoaded = true;
        loaded();

        var reader = channel.Reader;
        var pending = new Dictionary<(ulong, ulong, ushort), ContainerCapture>();
        var updates = new List<ContainerUpdate>();
        var owners = new List<OwnerUpdate>();
        Task<bool>? wait = null;

        while (true)
        {
            try
            {
                wait ??= reader.WaitToReadAsync().AsTask();
                var delay = NextFlushDelay();
                if (delay >= 0 && await Task.WhenAny(wait, Task.Delay(delay)) != wait)
                {
                    Flush(all: false);
                    continue;
                }

                if (!await wait)
                    break;
                wait = null;

                while (reader.TryRead(out var item))
                {
                    switch (item)
                    {
                        case ContainerCapture c:
                            if (pending.Remove(c.Key, out var superseded))
                                superseded.Release();
                            pending[c.Key] = c;
                            break;
                        case OwnerCapture o:
                            owners.Add(o.Update);
                            flushDue[o.Update.Owner.Id] = Environment.TickCount64 + FlushDelayMs;
                            break;
                        case DeleteCapture d:
                            Delete(d.OwnerIds, pending, owners);
                            break;
                    }
                }

                foreach (var c in pending.Values)
                {
                    updates.Add(c.ToUpdate());
                    c.Release();
                    flushDue[c.Owner.Id] = Environment.TickCount64 + FlushDelayMs;
                }
                pending.Clear();
                if (updates.Count == 0 && owners.Count == 0)
                    continue;

                store.Apply(updates, owners);
                published([.. updates], [.. owners.Select(o => o.Owner.Id).Distinct()]);
            }
            catch (Exception ex)
            {
                wait = null;
                log.Error(ex, "MogVault writer batch failed.");
            }
            finally
            {
                updates.Clear();
                owners.Clear();
            }
        }

        Flush(all: true);
    }

    private void Delete(ulong[]? ids, Dictionary<(ulong, ulong, ushort), ContainerCapture> pending, List<OwnerUpdate> owners)
    {
        bool Hit(ulong id) => ids is null || ids.Contains(id);

        foreach (var key in pending.Keys.Where(k => Hit(k.Item1)).ToArray())
            if (pending.Remove(key, out var dropped))
                dropped.Release();
        owners.RemoveAll(o => Hit(o.Owner.Id));

        var gone = Array.FindAll(store.Current.Characters, c => Hit(c.Id));
        foreach (var c in gone)
            flushDue.Remove(c.Id);
        var paths = ids is null ? DataFiles() : gone.Select(c => Path.Combine(directory, VaultFile.FileName(c.Kind, c.Id)));
        foreach (var path in paths)
        {
            try
            {
                File.Delete(path);
                unreadable.Remove(path);
            }
            catch (Exception ex)
            {
                log.Warning(ex, $"Could not delete {path}.");
            }
        }

        if (ids is null)
            store.Load([]);
        else
            store.Remove(ids);
        published([], [.. gone.Select(c => c.Id)]);
        log.Information($"Deleted {gone.Length} owner(s) on request.");
    }

    private string[] DataFiles() => Directory.Exists(directory)
        ? [.. Directory.EnumerateFiles(directory, "*" + VaultFile.Extension).Where(p => p.EndsWith(VaultFile.Extension, StringComparison.OrdinalIgnoreCase))]
        : [];

    private int NextFlushDelay()
    {
        if (flushDue.Count == 0)
            return -1;
        var now = Environment.TickCount64;
        var next = long.MaxValue;
        foreach (var due in flushDue.Values)
            next = Math.Min(next, due);
        return (int)Math.Max(0, next - now);
    }

    private void Flush(bool all)
    {
        if (flushDue.Count == 0)
            return;

        if (savingDisabled)
        {
            flushDue.Clear();
            return;
        }

        var now = Environment.TickCount64;
        var snapshot = store.Current;
        foreach (var (ownerId, due) in flushDue.ToArray())
        {
            if (!all && due > now)
                continue;
            flushDue.Remove(ownerId);
            if (snapshot.Find(ownerId) is not { } data)
                continue;
            var path = Path.Combine(directory, VaultFile.FileName(data.Kind, data.Id));
            if (unreadable.Contains(path))
            {
                log.Warning($"Not saving {path}: it could not be read at startup.");
                continue;
            }

            try
            {
                Directory.CreateDirectory(directory);
                VaultFile.Write(path, data);
            }
            catch (Exception ex)
            {
                log.Error(ex, $"Saving MogVault data for {data.Id:X16} failed.");
            }
        }
    }

    private void LoadAll()
    {
        if (!Directory.Exists(directory))
            return;

        var loaded = new List<CharacterData>();
        foreach (var path in Directory.EnumerateFiles(directory, "*" + VaultFile.Extension))
        {
            if (!path.EndsWith(VaultFile.Extension, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var data = ReadWithRetry(path);
                if (loaded.Exists(x => x.Id == data.Id))
                {
                    log.Warning($"Duplicate MogVault owner {data.Id:X16} in {path}; ignored.");
                    continue;
                }

                var valid = Array.FindAll(data.Containers, c => ContainerKinds.BelongsTo(data.Kind, c));
                if (valid.Length != data.Containers.Length)
                {
                    log.Information($"Dropped {data.Containers.Length - valid.Length} misfiled container(s) from {path}.");
                    data = data with { Containers = valid };
                    flushDue[data.Id] = Environment.TickCount64;
                }

                loaded.Add(data);
            }
            catch (InvalidDataException ex)
            {
                log.Error($"Corrupt MogVault file {path}: {ex.Message}. Renamed to .corrupt.");
                TryMove(path, path + ".corrupt");
            }
            catch (IOException ex)
            {
                unreadable.Add(path);
                log.Error(ex, $"Reading MogVault file {path} failed; it will not be overwritten this session.");
            }
        }

        if (forgetFreeCompanyDays > 0)
            ForgetLeftFreeCompanies(loaded);
        store.Load([.. loaded]);
        log.Debug($"MogVault loaded {loaded.Count} owners.");
    }

    private void ForgetLeftFreeCompanies(List<CharacterData> loaded)
    {
        var cutoff = DateTime.UtcNow.Ticks - TimeSpan.FromDays(forgetFreeCompanyDays).Ticks;
        foreach (var fc in VaultStore.LeftFreeCompanies(loaded, cutoff))
        {
            var path = Path.Combine(directory, VaultFile.FileName(fc.Kind, fc.Id));
            try
            {
                File.Delete(path);
                loaded.Remove(fc);
                log.Information($"Deleted {fc.Name} ({fc.Id:X16}): none of your characters is in it, last seen {new DateTime(fc.LastUpdatedTicks, DateTimeKind.Utc):u}.");
            }
            catch (Exception ex)
            {
                log.Warning(ex, $"Could not delete {path}.");
            }
        }
    }

    private void Backup()
    {
        try
        {
            if (!Directory.Exists(directory))
                return;
            var folder = Path.Combine(directory, VaultFile.BackupFolder);
            var target = Path.Combine(folder, VaultFile.BackupName(DateTime.Now));
            var files = DataFiles();
            if (File.Exists(target) || files.Length == 0)
                return;

            Directory.CreateDirectory(folder);
            var temp = target + ".tmp";
            File.Delete(temp);
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                foreach (var file in files)
                    zip.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
            File.Move(temp, target);

            foreach (var old in VaultFile.ExpiredBackups(Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>(), backupDays))
                File.Delete(Path.Combine(folder, old));
            log.Debug($"MogVault backup {target}: {files.Length} files.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "MogVault backup failed; loading continues.");
        }
    }

    private static CharacterData ReadWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return VaultFile.Read(path);
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(200);
            }
        }
    }

    private void TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"Could not rename {from}.");
        }
    }
}

