using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace QuickSwitch;

internal sealed class WindowsLauncher : ILauncher
{
    private static readonly string[] ProcessNames = ["Battle.net", "Battle.net Launcher", "Battle.net Helper"];
    private readonly int sessionId = Process.GetCurrentProcess().SessionId;

    public bool IsRunning => ClientPids().Count > 0;

    public void Stop()
    {
        var pids = ClientPids();
        if (pids.Count == 0) return;
        var windows = new List<(nint Handle, int Pid)>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            if (pids.Contains(pid)) windows.Add((window, pid));
            return true;
        }, 0);

        const uint queryEndSession = 0x0011;
        const uint endSession = 0x0016;
        const uint messageFlags = 0x0002 | 0x0020;
        var agreed = new List<(nint Handle, int Pid)>();
        foreach (var target in windows)
        {
            if (GetWindowThreadProcessId(target.Handle, out var owner) == 0 || owner != target.Pid)
                continue;
            if (SendMessageTimeoutW(target.Handle, queryEndSession, 0, 0, messageFlags, 500, out var result) != 0 &&
                result != 0) agreed.Add(target);
        }
        foreach (var target in agreed)
            if (GetWindowThreadProcessId(target.Handle, out var owner) != 0 && owner == target.Pid)
                SendMessageTimeoutW(target.Handle, endSession, 1, 0, messageFlags, 500, out _);

        var timeout = Stopwatch.StartNew();
        while (IsRunning)
        {
            if (timeout.Elapsed.TotalSeconds >= 10)
                throw new InvalidOperationException("战网仍在运行。请右键系统托盘里的战网图标，点「退出」，然后重试。");
            Thread.Sleep(200);
        }
    }

    public void Launch(string exe, string? region)
    {
        var info = new ProcessStartInfo
        {
            FileName = exe, WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false
        };
        if (region is not null) info.ArgumentList.Add("--setregion=" + region);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动战网。");
    }

    private HashSet<int> ClientPids()
    {
        var pids = new HashSet<int>();
        foreach (var name in ProcessNames)
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                {
                    try
                    {
                        if (process.SessionId == sessionId && !process.HasExited) pids.Add(process.Id);
                    }
                    catch (InvalidOperationException) { }
                }
        return pids;
    }

    public static string? FindExe(SwitchEngine engine)
    {
        if (IsBattleNetExe(engine.CustomExe)) return engine.CustomExe;
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var name in new[] { "Battle.net.exe", "Battle.net Launcher.exe" })
            {
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + name);
                var registered = (key?.GetValue(null) as string)?.Trim().Trim('"');
                if (IsBattleNetExe(registered)) return registered;
            }
        }

        var directories = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Battle.net"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Battle.net")
        }.Concat(engine.ConfigInstallPaths());
        foreach (var raw in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Path.IsPathFullyQualified(raw)) continue;
            if (IsBattleNetExe(raw)) return raw;
            foreach (var name in new[] { "Battle.net.exe", "Battle.net Launcher.exe" })
            {
                var path = Path.Combine(raw, name);
                if (IsBattleNetExe(path)) return path;
            }
        }
        return null;
    }

    public static bool IsBattleNetExe(string? path) => !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathFullyQualified(path) && File.Exists(path) &&
        (Path.GetFileName(path).Equals("Battle.net.exe", StringComparison.OrdinalIgnoreCase) ||
         Path.GetFileName(path).Equals("Battle.net Launcher.exe", StringComparison.OrdinalIgnoreCase));

    private delegate bool WindowCallback(nint hwnd, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowCallback callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out int processId);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nint wParam, nint lParam,
        uint flags, uint timeout, out nuint result);
}

internal sealed class WindowsAuth : IAuthSlots
{
    private const string AuthKey = @"Software\Blizzard Entertainment\Battle.net\UnifiedAuth";

    public Dictionary<string, byte[]> Read()
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using var key = Registry.CurrentUser.OpenSubKey(AuthKey);
        if (key is null) return result;
        foreach (var slot in key.GetValueNames())
            if (SwitchEngine.ValidSlot(slot) && key.GetValue(slot) is byte[] { Length: > 0 } blob)
                result.Add(slot, blob);
        return result;
    }

    public void Write(string slot, byte[]? value)
    {
        if (!SwitchEngine.ValidSlot(slot)) throw new InvalidDataException("登录槽格式无效。");
        if (value is null)
        {
            using var key = Registry.CurrentUser.OpenSubKey(AuthKey, writable: true);
            key?.DeleteValue(slot, throwOnMissingValue: false);
        }
        else
        {
            using var key = Registry.CurrentUser.CreateSubKey(AuthKey);
            key.SetValue(slot, value, RegistryValueKind.Binary);
        }
    }
}
