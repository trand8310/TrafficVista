using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Win32;

namespace TrafficVista.DesktopIntegration;

/// <summary>只按实际应用目标识别旧启动项；名称相同不足以认定属于本应用。</summary>
internal static class StartupEntryCleanup
{
    internal static StartupSetupResult Clean(StartupLaunchSpec launch, string keepShortcut, bool systemEntries)
    {
        var warnings = new List<string>();
        var removed = 0;
        void Attempt(string location, Action action)
        {
            try { action(); }
            catch (Exception ex) { warnings.Add(location + "：" + ex.Message); }
        }
        var directories = new List<string> { Path.GetDirectoryName(keepShortcut)! };
        if (systemEntries) directories.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));
        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) continue;
            Attempt("启动目录", () =>
            {
                foreach (var file in Directory.GetFiles(directory, "*.lnk"))
                {
                    if (SamePath(file, keepShortcut)) continue;
                    Attempt("旧快捷方式 " + Path.GetFileName(file), () =>
                    {
                        var target = StartupShortcutService.Read(file);
                        if (!MatchesApplication(target.Executable, target.Arguments, launch)) return;
                        File.Delete(file);
                        removed++;
                    });
                }
            });
        }
        if (!systemEntries) return new(removed, warnings);
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        foreach (var name in new[] { "Run", "RunOnce" })
        {
            var keyPath = @"Software\Microsoft\Windows\CurrentVersion\" + name;
            Attempt(hive + "/" + view + "/" + name, () =>
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var read = root.OpenSubKey(keyPath);
                if (read is null) return;
                var matches = read.GetValueNames().Where(value =>
                    read.GetValue(value) is string command && MatchesCommand(command, launch)).ToArray();
                if (matches.Length == 0) return;
                using var write = root.OpenSubKey(keyPath, writable: true)
                    ?? throw new UnauthorizedAccessException("无法写入旧启动项注册表。");
                foreach (var value in matches)
                {
                    // 写入前重新核对，避免删除扫描后被其它程序替换的值。
                    if (write.GetValue(value) is not string current || !MatchesCommand(current, launch)) continue;
                    write.DeleteValue(value, throwOnMissingValue: false);
                    removed++;
                }
            });
        }
        Attempt("开机/登录计划任务", () =>
        {
            object? service = null, root = null;
            try
            {
                service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!);
                ((dynamic)service!).Connect();
                root = ((dynamic)service).GetFolder("\\");
                VisitFolder(root);
            }
            finally { Release(root); Release(service); }
        });
        return new(removed, warnings);

        void VisitFolder(dynamic folder)
        {
            object? tasks = null, folders = null;
            try
            {
                tasks = folder.GetTasks(1); // 包括隐藏任务。
                var names = new List<string>();
                for (var index = 1; index <= (int)((dynamic)tasks).Count; index++)
                {
                    object? task = null;
                    try
                    {
                        task = ((dynamic)tasks).Item(index);
                        var xml = XDocument.Parse((string)((dynamic)task).Xml);
                        var ns = xml.Root!.Name.Namespace;
                        var triggers = xml.Root.Element(ns + "Triggers")?.Elements().ToArray() ?? [];
                        var actions = xml.Root.Element(ns + "Actions")?.Elements().ToArray() ?? [];
                        if (!triggers.Any(t => t.Name.LocalName is "BootTrigger" or "LogonTrigger")
                            || actions.Length == 0 || actions.Any(a => a.Name.LocalName != "Exec"
                                || !MatchesApplication(a.Element(ns + "Command")?.Value ?? "",
                                    a.Element(ns + "Arguments")?.Value ?? "", launch))) continue;
                        names.Add((string)((dynamic)task).Name);
                    }
                    finally { Release(task); }
                }
                foreach (var name in names)
                    Attempt("旧计划任务 " + name, () => { folder.DeleteTask(name, 0); removed++; });
                folders = folder.GetFolders(0);
                for (var index = 1; index <= (int)((dynamic)folders).Count; index++)
                {
                    object? child = null;
                    try { child = ((dynamic)folders).Item(index); Attempt("计划任务目录", () => VisitFolder(child)); }
                    finally { Release(child); }
                }
            }
            finally { Release(folders); Release(tasks); }
        }
    }

    internal static bool MatchesApplication(string executable, string arguments, StartupLaunchSpec launch)
    {
        executable = Environment.ExpandEnvironmentVariables(executable.Trim().Trim('"'));
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var tokens = SplitCommandLine("host " + arguments);
            var dll = Path.Combine(launch.WorkingDirectory,
                System.Reflection.Assembly.GetEntryAssembly()!.GetName().Name + ".dll");
            return tokens.Length >= 2 && SamePath(tokens[1], dll);
        }
        // dotnet 宿主不能被当作应用目标，否则会误删其它 .NET 应用。
        return !Path.GetFileNameWithoutExtension(launch.Executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && SamePath(executable, launch.Executable);
    }

    internal static bool MatchesCommand(string command, StartupLaunchSpec launch)
    {
        var tokens = SplitCommandLine(Environment.ExpandEnvironmentVariables(command));
        return tokens.Length > 0 && MatchesApplication(tokens[0], string.Join(" ",
            tokens.Skip(1).Select(token => "\"" + token + "\"")), launch);
    }

    private static bool SamePath(string first, string second)
    {
        try { return Path.IsPathFullyQualified(first) && Path.IsPathFullyQualified(second)
            && Path.GetFullPath(first).Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    private static string[] SplitCommandLine(string command)
    {
        var pointer = CommandLineToArgvW(command, out var count);
        if (pointer == IntPtr.Zero) return [];
        try { return Enumerable.Range(0, count).Select(i =>
            Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size)) ?? "").ToArray(); }
        finally { LocalFree(pointer); }
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}

