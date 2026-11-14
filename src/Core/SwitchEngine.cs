using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace QuickSwitch;

public sealed record Profile(string Login, string Region, string Label, string? Pointer, AuthSlot? Auth, DateTime SavedUtc)
{
    public string Id => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Region + ":" + Login.ToUpperInvariant())));
}

public sealed record AuthSlot(string Name, byte[] Blob);
public sealed record SwitchJournal(byte[] Config, bool ChangePointer, string? Pointer, string? SlotName, byte[]? SlotValue, bool Applied);

public interface ILauncher
{
    bool IsRunning { get; }
    void Stop();
    void Launch(string exe, string? region);
}

public interface IAuthSlots
{
    Dictionary<string, byte[]> Read();
    void Write(string slot, byte[]? value);
}

public sealed class SwitchEngine(string dataDir, string configPath, ClientCache cache, ILauncher launcher, IAuthSlots auth)
{
    private readonly string profilesPath = Path.Combine(dataDir, "accounts.json");
    private readonly string journalPath = Path.Combine(dataDir, "switch-recovery.json");
    private readonly string setupPath = Path.Combine(dataDir, "setup.json");
    private Setup? setup;

    private sealed record Setup(string[] Remembered, Dictionary<string, string> AuthHashes);

    public Profile[] Profiles => ReadJson<Profile[]>(profilesPath) ?? [];
    public string? CustomExe => ReadJson<string>(Path.Combine(dataDir, "launcher.json"));

    public void SetExe(string path) => WriteJson(Path.Combine(dataDir, "launcher.json"), path);
    public string[] ConfigInstallPaths()
    {
        if (!File.Exists(configPath)) return [];
        try
        {
            using var json = JsonDocument.Parse(Encoding.UTF8.GetString(File.ReadAllBytes(configPath)).TrimStart('\uFEFF'),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return FindPaths(json.RootElement).ToArray();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void ObserveLoginSlots()
    {
        if (!File.Exists(setupPath))
            setup = new Setup([], HashSlots(auth.Read()));
    }

    public Profile SaveCurrent(string exe, string fallbackRegion)
    {
        Recover();
        var beforeStop = new LauncherConfig(ReadConfig());
        if (beforeStop.Names.Length == 0) throw new InvalidOperationException("请先在战网登录账号并勾选保持登录，再保存快照。");
        var wasRunning = launcher.IsRunning;
        launcher.Stop();
        var bytes = ReadConfig();
        var config = new LauncherConfig(bytes);
        var login = config.Names.FirstOrDefault() ??
            throw new InvalidOperationException("战网未写入已记住的账号。请勾选保持登录后重试。");
        var region = config.Region ?? RequireRegion(fallbackRegion);
        var accounts = Profiles.ToList();
        var matched = cache.Match(login, region);
        var previous = accounts.FirstOrDefault(p => SameProfile(p, login, region));
        var pending = ReadJson<Setup>(setupPath);
        var currentSlots = auth.Read();
        var reference = pending ?? setup;
        var slot = previous?.Auth;
        if (matched is not null)
        {
            if (reference is not null)
            {
                var changed = currentSlots.Where(p => !reference.AuthHashes.TryGetValue(p.Key, out var last)
                    || last != Hash(p.Value)).ToArray();
                if (changed.Length == 1)
                    slot = new(changed[0].Key, changed[0].Value);
            }
            if (slot is null && accounts.Count == 0 && currentSlots.Count == 1)
            {
                var only = currentSlots.Single();
                slot = new(only.Key, only.Value);
            }
        }
        var label = previous?.Label ?? (string.IsNullOrWhiteSpace(matched?.BattleTag) ? login : matched.BattleTag);
        var profile = new Profile(login, region, label, matched?.Pointer ?? previous?.Pointer, slot, DateTime.UtcNow);
        accounts.RemoveAll(p => SameProfile(p, login, region));
        accounts.Add(profile);
        WriteJson(profilesPath, accounts);
        if (pending is not null)
        {
            var merged = config.Names.Concat(pending.Remembered);
            Apply(bytes, config.SelectAccount(login, region, merged), null, null, exe, region, wasRunning);
            File.Delete(setupPath);
        }
        else if (wasRunning)
        {
            launcher.Launch(exe, null);
        }
        setup = new Setup([], HashSlots(auth.Read()));
        return profile;
    }

    public void PrepareLogin(string exe, string region)
    {
        region = RequireRegion(region);
        Recover();
        launcher.Stop();
        var slots = HashSlots(auth.Read());
        if (!File.Exists(configPath))
        {
            WriteJson(setupPath, new Setup([], slots));
            launcher.Launch(exe, region);
            return;
        }
        var bytes = ReadConfig();
        var config = new LauncherConfig(bytes);
        var earlier = ReadJson<Setup>(setupPath);
        WriteJson(setupPath, new Setup(config.Names.Concat(earlier?.Remembered ?? []).Distinct(
            StringComparer.OrdinalIgnoreCase).ToArray(), slots));
        Apply(bytes, config.SelectAccount(null, region, []), null, null, exe, region, true);
    }

    public void SwitchTo(string exe, Profile target)
    {
        var region = RequireRegion(target.Region);
        if (string.IsNullOrWhiteSpace(target.Login)) throw new InvalidDataException("快照缺少登录账号。");
        Recover();
        launcher.Stop();
        var bytes = ReadConfig();
        var config = new LauncherConfig(bytes);
        var profiles = Profiles.ToList();
        var pending = ReadJson<Setup>(setupPath);
        var currentSlots = auth.Read();
        var current = profiles.FirstOrDefault(p => SameProfile(p, config.Names.FirstOrDefault(), config.Region));
        if (current?.Auth is { } currentAuth && !SameProfile(target, current.Login, current.Region) &&
            cache.Match(current.Login, current.Region) is not null &&
            currentSlots.TryGetValue(currentAuth.Name, out var fresh) &&
            !profiles.Any(p => p.Id != current.Id && p.Auth is { } other &&
                string.Equals(other.Name, currentAuth.Name, StringComparison.OrdinalIgnoreCase) && fresh.AsSpan().SequenceEqual(other.Blob)) &&
            !fresh.AsSpan().SequenceEqual(currentAuth.Blob))
        {
            var index = profiles.FindIndex(p => p.Id == current.Id);
            profiles[index] = current with { Auth = new(currentAuth.Name, fresh) };
            WriteJson(profilesPath, profiles);
        }

        AuthSlot? restore = null;
        if (target.Auth is { } wanted && ValidSlot(wanted.Name) && wanted.Blob.Length > 0)
        {
            var sharesSlot = profiles.Any(p => p.Id != target.Id && p.Auth is { } other &&
                string.Equals(other.Name, wanted.Name, StringComparison.OrdinalIgnoreCase));
            if (!currentSlots.TryGetValue(wanted.Name, out var live) ||
                (sharesSlot && !live.AsSpan().SequenceEqual(wanted.Blob)))
                restore = wanted;
        }

        var remembered = config.Names.Concat(pending?.Remembered ?? [])
            .Concat(profiles.Select(p => p.Login));
        Apply(bytes, config.SelectAccount(target.Login, region, remembered),
            target.Pointer, restore, exe, region, true);
        if (pending is not null) File.Delete(setupPath);
        setup = new Setup([], HashSlots(auth.Read()));
    }

    public void Rename(string id, string label)
    {
        label = label.Trim();
        if (label.Length is 0 or > 60) throw new ArgumentException("请输入 1 至 60 个字符的备注。");
        WriteJson(profilesPath, Profiles.Select(p => p.Id == id ? p with { Label = label } : p).ToArray());
    }

    public void Remove(string id) => WriteJson(profilesPath, Profiles.Where(p => p.Id != id).ToArray());

    public bool Recover()
    {
        var pending = ReadJson<SwitchJournal>(journalPath);
        if (pending is null) return false;
        if (!pending.Applied)
        {
            if (launcher.IsRunning) throw new InvalidOperationException("发现上次切换中断。请从托盘退出战网后再操作，以恢复原配置。");
            Restore(pending);
        }
        File.Delete(journalPath);
        return !pending.Applied;
    }

    private void Apply(byte[] before, byte[] next, string? pointer, AuthSlot? slot,
        string exe, string region, bool launch)
    {
        if (launcher.IsRunning) throw new InvalidOperationException("战网正在运行，请从托盘退出后重试。");
        string? previousPointer = null;
        var canWritePointer = pointer is not null;
        if (canWritePointer)
        {
            try { previousPointer = cache.ReadPointer(); }
            catch (SqliteException) { canWritePointer = false; }
        }
        canWritePointer &= File.Exists(cache.FilePath) && pointer != previousPointer;
        byte[]? previousSlot = null;
        if (slot is not null) auth.Read().TryGetValue(slot.Name, out previousSlot);
        var journal = new SwitchJournal(before, canWritePointer, previousPointer, slot?.Name, previousSlot, false);
        WriteJson(journalPath, journal);
        try
        {
            WriteAtomic(configPath, next);
            if (canWritePointer) cache.WritePointer(pointer);
            if (slot is not null) auth.Write(slot.Name, slot.Blob);
            WriteJson(journalPath, journal with { Applied = true });
            if (launch) launcher.Launch(exe, region);
        }
        catch (Exception ex)
        {
            WriteJson(journalPath, journal);
            try { Recover(); }
            catch (Exception rollback)
            {
                throw new InvalidOperationException("操作中断，恢复记录已保留。请退出战网再试。", new AggregateException(ex, rollback));
            }
            throw new InvalidOperationException("操作失败，已恢复切换前的战网配置。\n" + ex.Message, ex);
        }

        try { File.Delete(journalPath); }
        catch (IOException) { /* Applied checkpoints can be removed next time. */ }
        catch (UnauthorizedAccessException) { }
    }

    private void Restore(SwitchJournal previous)
    {
        WriteAtomic(configPath, previous.Config);
        if (previous.ChangePointer) cache.WritePointer(previous.Pointer);
        if (previous.SlotName is { } slot && ValidSlot(slot)) auth.Write(slot, previous.SlotValue);
    }

    private byte[] ReadConfig() => File.Exists(configPath)
        ? File.ReadAllBytes(configPath)
        : throw new InvalidOperationException("还没有找到战网配置。请先打开战网并登录。");

    private static string RequireRegion(string region) => LauncherConfig.NormalizeRegion(region) ??
        throw new ArgumentException("请选择有效的登录区服。");

    private static bool SameProfile(Profile profile, string? login, string? region) =>
        profile.Region == region && string.Equals(profile.Login, login, StringComparison.OrdinalIgnoreCase);

    public static bool ValidSlot(string name) => Regex.IsMatch(name, "\\A[0-9A-Fa-f]{8}\\z");

    private static Dictionary<string, string> HashSlots(Dictionary<string, byte[]> slots) =>
        slots.ToDictionary(p => p.Key, p => Hash(p.Value), StringComparer.OrdinalIgnoreCase);

    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));

    private static T? ReadJson<T>(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path)) : default;

    private static void WriteJson<T>(string path, T value) =>
        WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(value));

    private static void WriteAtomic(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    private static IEnumerable<string> FindPaths(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in node.EnumerateObject())
            {
                if (field.NameEquals("Path") && field.Value.ValueKind == JsonValueKind.String &&
                    field.Value.GetString() is { Length: > 0 } path) yield return path;
                foreach (var nested in FindPaths(field.Value)) yield return nested;
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in node.EnumerateArray())
                foreach (var nested in FindPaths(entry)) yield return nested;
        }
    }
}
