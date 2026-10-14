using System.Runtime.InteropServices;

namespace TrafficVista.DesktopIntegration;

internal sealed record StartupLaunchSpec(string Executable, string Arguments, string WorkingDirectory);
internal sealed record StartupSetupResult(int RemovedEntries, IReadOnlyList<string> Warnings);

/// <summary>Current-user startup only. Shortcut COM calls run on a short-lived background STA thread.</summary>
internal sealed class StartupShortcutService
{
    internal string ShortcutPath { get; }
    internal StartupLaunchSpec Launch { get; }
    private readonly bool _cleanSystemEntries;
    internal StartupShortcutService(string? startupDirectory = null, StartupLaunchSpec? launch = null)
    {
        var directory = startupDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException("无法获取当前用户的开机启动目录。");
        ShortcutPath = Path.Combine(Path.GetFullPath(directory), "TrafficVista.lnk");
        Launch = launch ?? CurrentLaunch();
        _cleanSystemEntries = startupDirectory is null;
    }

    internal static StartupLaunchSpec CurrentLaunch()
    {
        var process = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定应用启动路径。");
        var directory = Path.GetFullPath(AppContext.BaseDirectory);
        if (!Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return new(process, "--auto-start", directory);
        var name = System.Reflection.Assembly.GetEntryAssembly()!.GetName().Name!;
        var executable = Path.Combine(directory, name + ".exe");
        return File.Exists(executable) ? new(executable, "--auto-start", directory)
            : new(process, "\"" + Path.Combine(directory, name + ".dll") + "\" --auto-start", directory);
    }

    internal Task<bool> IsEnabledAsync() => OnSta(() =>
    {
        if (!File.Exists(ShortcutPath)) return false;
        var actual = Read(ShortcutPath);
        return Matches(actual, Launch);
    });

    internal Task<StartupSetupResult> EnsureEnabledAsync() => OnSta(() =>
    {
        if (!File.Exists(Launch.Executable)) throw new FileNotFoundException("开机启动的应用文件不存在。", Launch.Executable);
        var directory = Path.GetDirectoryName(ShortcutPath)!;
        Directory.CreateDirectory(directory);
        // 临时文件不放在Startup内，避免保存过程中被系统当作第二个启动项。
        var temporary = Path.Combine(Path.GetTempPath(), "TrafficVista-" + Guid.NewGuid().ToString("N") + ".lnk");
        try
        {
            WithShortcut(temporary, shortcut =>
            {
                shortcut.TargetPath = Launch.Executable;
                shortcut.Arguments = Launch.Arguments;
                shortcut.WorkingDirectory = Launch.WorkingDirectory;
                shortcut.Description = "TrafficVista（登录后自动启动任务）";
                shortcut.WindowStyle = 1;
                shortcut.Save();
                return true;
            });
            if (!Matches(Read(temporary), Launch)) throw new IOException("开机启动快捷方式校验失败。");
            File.Move(temporary, ShortcutPath, overwrite: true);
            if (!Matches(Read(ShortcutPath), Launch)) throw new IOException("最终开机启动快捷方式校验失败。");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return StartupEntryCleanup.Clean(Launch, ShortcutPath, _cleanSystemEntries);
    });

    internal static StartupLaunchSpec Read(string path) => WithShortcut(path, shortcut =>
        new StartupLaunchSpec((string)shortcut.TargetPath, (string)shortcut.Arguments, (string)shortcut.WorkingDirectory));
    internal static Task<StartupLaunchSpec> ReadAsync(string path) => OnSta(() => Read(path));
    private static bool Matches(StartupLaunchSpec actual, StartupLaunchSpec expected) =>
        string.Equals(actual.Executable, expected.Executable, StringComparison.OrdinalIgnoreCase)
        && actual.Arguments == expected.Arguments
        && string.Equals(actual.WorkingDirectory.TrimEnd('\\'), expected.WorkingDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static T WithShortcut<T>(string path, Func<dynamic, T> action)
    {
        object? shell = null, shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("系统不支持创建Windows快捷方式。"));
            shortcut = ((dynamic)shell!).CreateShortcut(path);
            return action(shortcut);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static Task<T> OnSta<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "TrafficVista startup shortcut" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
