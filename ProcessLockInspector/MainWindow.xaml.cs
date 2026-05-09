using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Windows;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;

namespace ProcessLockInspector;

[SupportedOSPlatform("windows")]
public partial class MainWindow : Window
{
    private readonly ObservableCollection<LockingProcessViewModel> _lockingProcesses = [];
    private readonly bool _isRunningAsAdministrator;

    public MainWindow()
    {
        InitializeComponent();
        ProcessGrid.ItemsSource = _lockingProcesses;
        _isRunningAsAdministrator = IsRunningAsAdministrator();
        AppendStatus("Ready.");

        if (_isRunningAsAdministrator)
        {
            AppendStatus("Running as Administrator.");
            if (PrivilegeManager.TryEnableDebugPrivilege(out var detail))
            {
                AppendStatus($"SeDebugPrivilege enabled ({detail}).");
            }
            else
            {
                AppendStatus($"Could not enable SeDebugPrivilege ({detail}).");
            }
        }
        else
        {
            AppendStatus("Running without Administrator rights.");
        }

        UpdateSummary();
    }

    private void BrowseFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Title = "Select locked file"
        };

        if (dialog.ShowDialog(this) == true)
        {
            TargetPathTextBox.Text = dialog.FileName;
            SuggestNewName(dialog.FileName);
        }
    }

    private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = false,
            CheckPathExists = true,
            ValidateNames = false,
            FileName = "Select Folder",
            Title = "Select locked folder"
        };

        if (dialog.ShowDialog(this) == true)
        {
            var selectedDirectory = Path.GetDirectoryName(dialog.FileName);
            if (!string.IsNullOrWhiteSpace(selectedDirectory))
            {
                TargetPathTextBox.Text = selectedDirectory;
                SuggestNewName(selectedDirectory);
            }
        }
    }

    private void AnalyzeLocksButton_Click(object sender, RoutedEventArgs e)
    {
        AnalyzeLocksForCurrentTarget();
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var proc in _lockingProcesses)
        {
            proc.MarkForKill = true;
        }

        UpdateSummary();
    }

    private void SelectNoneButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var proc in _lockingProcesses)
        {
            proc.MarkForKill = false;
        }

        UpdateSummary();
    }

    private void KillSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _lockingProcesses.Where(p => p.MarkForKill).ToList();
        if (selected.Count == 0)
        {
            AppendStatus("No processes selected to kill.");
            return;
        }

        foreach (var proc in selected)
        {
            try
            {
                using var process = Process.GetProcessById(proc.ProcessId);
                process.Kill(true);
                process.WaitForExit(5000);
                AppendStatus($"Killed PID {proc.ProcessId} ({proc.ProcessName}).");
            }
            catch (Exception ex)
            {
                AppendStatus($"Failed to kill PID {proc.ProcessId}: {ex.Message}");
            }
        }

        AnalyzeLocksForCurrentTarget();
    }

    private void RenameNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetRenamePaths(out var sourcePath, out var destinationPath))
        {
            return;
        }

        try
        {
            if (File.Exists(sourcePath))
            {
                File.Move(sourcePath, destinationPath, overwrite: false);
            }
            else
            {
                Directory.Move(sourcePath, destinationPath);
            }

            TargetPathTextBox.Text = destinationPath;
            AppendStatus($"Renamed successfully: {destinationPath}");
            AnalyzeLocksForCurrentTarget();
        }
        catch (Exception ex)
        {
            AppendStatus($"Rename failed: {ex.Message}");
        }
    }

    private void ForceRenameButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetRenamePaths(out var sourcePath, out var destinationPath))
        {
            return;
        }

        const int flags = NativeMethods.MoveFileDelayUntilReboot | NativeMethods.MoveFileReplaceExisting;
        var ok = NativeMethods.MoveFileEx(sourcePath, destinationPath, flags);
        if (ok)
        {
            AppendStatus("Force rename scheduled for next reboot.");
            AppendStatus($"From: {sourcePath}");
            AppendStatus($"To:   {destinationPath}");
            return;
        }

        var error = Marshal.GetLastWin32Error();
        AppendStatus($"Failed to schedule force rename. Win32 error {error}.");
    }

    private void AnalyzeLocksForCurrentTarget()
    {
        var targetPath = NormalizePath(TargetPathTextBox.Text);
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            AppendStatus("Enter or browse a target file/folder path.");
            return;
        }

        if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
        {
            AppendStatus("Target path does not exist.");
            return;
        }

        try
        {
            var processes = RestartManager.GetLockingProcesses(targetPath);
            _lockingProcesses.Clear();
            foreach (var process in processes.OrderBy(p => p.ProcessName))
            {
                _lockingProcesses.Add(process);
            }

            if (_lockingProcesses.Count == 0)
            {
                AppendStatus("No locking processes found.");
            }
            else
            {
                AppendStatus($"Found {_lockingProcesses.Count} locking process(es).");
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            AppendStatus($"Lock scan failed: {ex.Message}");

            if (HandleScanner.TryGetLockingProcesses(targetPath, out var handleProcesses, out var handleMessage))
            {
                _lockingProcesses.Clear();
                foreach (var process in handleProcesses.OrderBy(p => p.ProcessName))
                {
                    _lockingProcesses.Add(process);
                }

                if (_lockingProcesses.Count == 0)
                {
                    AppendStatus("Handle fallback succeeded. No locking processes found.");
                }
                else
                {
                    AppendStatus($"Handle fallback succeeded. Found {_lockingProcesses.Count} locking process(es).");
                }

                if (!string.IsNullOrWhiteSpace(handleMessage))
                {
                    AppendStatus(handleMessage);
                }

                UpdateSummary();
                return;
            }

            if (!string.IsNullOrWhiteSpace(handleMessage))
            {
                AppendStatus(handleMessage);
            }

            if (!_isRunningAsAdministrator)
            {
                AppendStatus("Tip: relaunch the app as Administrator for protected/system processes.");
                var result = MessageBox.Show(
                    this,
                    "Restart Manager returned Access Denied. Relaunch this app as Administrator now?",
                    "Administrator Required",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    RestartAsAdministrator();
                }
            }
            else
            {
                AppendStatus("Even elevated admin can be blocked by protected processes (PPL/System). Try closing obvious apps first, then rescan.");
            }
        }
        catch (Exception ex)
        {
            AppendStatus($"Lock scan failed: {ex.Message}");
        }

        UpdateSummary();
    }

    private bool TryGetRenamePaths(out string sourcePath, out string destinationPath)
    {
        sourcePath = NormalizePath(TargetPathTextBox.Text);
        destinationPath = string.Empty;

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            AppendStatus("Target path is required.");
            return false;
        }

        var isFile = File.Exists(sourcePath);
        var isDirectory = Directory.Exists(sourcePath);
        if (!isFile && !isDirectory)
        {
            AppendStatus("Target path does not exist.");
            return false;
        }

        var newName = NewNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            AppendStatus("New name is required.");
            return false;
        }

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            AppendStatus("New name contains invalid characters.");
            return false;
        }

        var parentDirectory = Path.GetDirectoryName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            AppendStatus("Cannot rename a root path.");
            return false;
        }

        destinationPath = Path.Combine(parentDirectory, newName);
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            AppendStatus("New name is the same as current name.");
            return false;
        }

        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            AppendStatus("A file/folder with the new name already exists.");
            return false;
        }

        return true;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return Path.GetFullPath(path.Trim().Trim('"'));
    }

    private void SuggestNewName(string path)
    {
        if (File.Exists(path))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            NewNameTextBox.Text = $"{stem}_renamed{ext}";
            return;
        }

        var folderName = new DirectoryInfo(path).Name;
        NewNameTextBox.Text = $"{folderName}_renamed";
    }

    private void AppendStatus(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        StatusTextBox.AppendText(line + Environment.NewLine);
        StatusTextBox.ScrollToEnd();
    }

    private void UpdateSummary()
    {
        var total = _lockingProcesses.Count;
        var marked = _lockingProcesses.Count(p => p.MarkForKill);
        SummaryTextBlock.Text = $"Processes: {total} | Selected to kill: {marked}";
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void RestartAsAdministrator()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                AppendStatus("Unable to determine current executable path for elevation.");
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"
            };

            _ = Process.Start(startInfo);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            AppendStatus($"Elevation cancelled or failed: {ex.Message}");
        }
    }
}

public sealed class LockingProcessViewModel : INotifyPropertyChanged
{
    private bool _markForKill;

    public int ProcessId { get; init; }

    public string ProcessName { get; init; } = string.Empty;

    public string AppType { get; init; } = string.Empty;

    public string MainWindowTitle { get; init; } = string.Empty;

    public string ExecutablePath { get; init; } = string.Empty;

    public bool MarkForKill
    {
        get => _markForKill;
        set
        {
            if (_markForKill == value)
            {
                return;
            }

            _markForKill = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MarkForKill)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

 [SupportedOSPlatform("windows")]
internal static class RestartManager
{
    private const int ErrorSuccess = 0;
    private const int ErrorAccessDenied = 5;
    private const int ErrorMoreData = 234;
    private const int CchRmSessionKey = 32;
    private const int CchRmMaxAppName = 255;
    private const int CchRmMaxSvcName = 63;

    public static IReadOnlyList<LockingProcessViewModel> GetLockingProcesses(string path)
    {
        var sessionKey = new StringBuilder(CchRmSessionKey + 1);
        var startResult = NativeMethods.RmStartSession(out var sessionHandle, 0, sessionKey);
        if (startResult != ErrorSuccess)
        {
            throw new InvalidOperationException($"Could not start Restart Manager session. Error {startResult}.");
        }

        try
        {
            var resources = new[] { path };
            var registerResult = NativeMethods.RmRegisterResources(sessionHandle, (uint)resources.Length, resources, 0, null, 0, null);
            if (registerResult != ErrorSuccess)
            {
                throw new InvalidOperationException($"Could not register resource. Error {registerResult}.");
            }

            uint processInfoNeeded;
            uint processInfoCount = 0;
            uint rebootReasons = 0;

            var listResult = NativeMethods.RmGetList(sessionHandle, out processInfoNeeded, ref processInfoCount, null, ref rebootReasons);

            if (listResult == ErrorSuccess && processInfoNeeded == 0)
            {
                return [];
            }

            if (listResult == ErrorAccessDenied)
            {
                var fallback = TryGetLockingProcessesFromFileHandle(path);
                if (fallback.Count > 0)
                {
                    return fallback;
                }

                throw new UnauthorizedAccessException("Access denied while enumerating locking processes (error 5). Try running as Administrator.");
            }

            if (listResult != ErrorMoreData)
            {
                throw new InvalidOperationException($"Could not get process list size. Error {listResult}.");
            }

            var processInfo = new RmProcessInfo[processInfoNeeded];
            processInfoCount = processInfoNeeded;
            listResult = NativeMethods.RmGetList(sessionHandle, out processInfoNeeded, ref processInfoCount, processInfo, ref rebootReasons);

            if (listResult == ErrorAccessDenied)
            {
                var fallback = TryGetLockingProcessesFromFileHandle(path);
                if (fallback.Count > 0)
                {
                    return fallback;
                }

                throw new UnauthorizedAccessException("Access denied while reading process lock details (error 5). Try running as Administrator.");
            }

            if (listResult != ErrorSuccess)
            {
                throw new InvalidOperationException($"Could not get process list details. Error {listResult}.");
            }

            var results = new List<LockingProcessViewModel>((int)processInfoCount);
            for (var i = 0; i < processInfoCount; i++)
            {
                results.Add(MapProcessInfo(processInfo[i]));
            }

            return results;
        }
        finally
        {
            _ = NativeMethods.RmEndSession(sessionHandle);
        }
    }

    private static LockingProcessViewModel MapProcessInfo(RmProcessInfo rmInfo)
    {
        var pid = rmInfo.Process.dwProcessId;
        var processName = rmInfo.strAppName;
        var windowTitle = string.Empty;
        var executablePath = string.Empty;

        try
        {
            using var process = Process.GetProcessById(pid);
            if (!string.IsNullOrWhiteSpace(process.ProcessName))
            {
                processName = process.ProcessName;
            }

            windowTitle = process.MainWindowTitle;
            executablePath = process.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            // Process may have exited or be inaccessible by the time we inspect it.
        }

        return new LockingProcessViewModel
        {
            ProcessId = pid,
            ProcessName = processName,
            AppType = rmInfo.ApplicationType.ToString(),
            MainWindowTitle = windowTitle,
            ExecutablePath = executablePath
        };
    }

    private static IReadOnlyList<LockingProcessViewModel> TryGetLockingProcessesFromFileHandle(string path)
    {
        var isDirectory = Directory.Exists(path);
        const int fileReadAttributes = 0x80;
        const int shareRead = 0x1;
        const int shareWrite = 0x2;
        const int shareDelete = 0x4;
        const int openExisting = 3;
        const int fileFlagBackupSemantics = 0x02000000;
        const int fileInfoClassProcessIdsUsingFile = 47;

        var flags = isDirectory ? fileFlagBackupSemantics : 0;

        using var handle = NativeMethods.CreateFile(
            path,
            fileReadAttributes,
            shareRead | shareWrite | shareDelete,
            IntPtr.Zero,
            openExisting,
            flags,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return [];
        }

        var buffer = new byte[64 * 1024];
        if (!NativeMethods.GetFileInformationByHandleEx(
                handle,
                fileInfoClassProcessIdsUsingFile,
                buffer,
                (uint)buffer.Length))
        {
            return [];
        }

        var processCount = BitConverter.ToUInt32(buffer, 0);
        var processIds = new HashSet<int>();
        var offset = IntPtr.Size == 8 ? 8 : 4;
        var stride = IntPtr.Size;

        for (var i = 0; i < processCount; i++)
        {
            if (offset + stride > buffer.Length)
            {
                break;
            }

            long pid = IntPtr.Size == 8
                ? BitConverter.ToInt64(buffer, offset)
                : BitConverter.ToInt32(buffer, offset);

            if (pid is > 0 and <= int.MaxValue)
            {
                processIds.Add((int)pid);
            }

            offset += stride;
        }

        var results = new List<LockingProcessViewModel>(processIds.Count);
        foreach (var pid in processIds.OrderBy(x => x))
        {
            var model = new LockingProcessViewModel
            {
                ProcessId = pid,
                AppType = "HandleOwner",
                ProcessName = "Unknown",
                MainWindowTitle = string.Empty,
                ExecutablePath = string.Empty
            };

            try
            {
                using var process = Process.GetProcessById(pid);
                model = new LockingProcessViewModel
                {
                    ProcessId = pid,
                    AppType = "HandleOwner",
                    ProcessName = process.ProcessName,
                    MainWindowTitle = process.MainWindowTitle,
                    ExecutablePath = process.MainModule?.FileName ?? string.Empty
                };
            }
            catch
            {
                // Process may have exited or may not be queryable.
            }

            results.Add(model);
        }

        return results;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int dwProcessId;
        public FileTime ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    private enum RmAppType
    {
        RmUnknownApp = 0,
        RmMainWindow = 1,
        RmOtherWindow = 2,
        RmService = 3,
        RmExplorer = 4,
        RmConsole = 5,
        RmCritical = 1000
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)]
        public string strAppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)]
        public string strServiceShortName;

        public RmAppType ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(
            string lpFileName,
            int dwDesiredAccess,
            int dwShareMode,
            IntPtr lpSecurityAttributes,
            int dwCreationDisposition,
            int dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandleEx(
            SafeFileHandle hFile,
            int fileInfoClass,
            [Out] byte[] lpFileInformation,
            uint dwBufferSize);

        [DllImport("rstrtmgr", CharSet = CharSet.Unicode)]
        public static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

        [DllImport("rstrtmgr", CharSet = CharSet.Unicode)]
        public static extern int RmRegisterResources(
            uint pSessionHandle,
            uint nFiles,
            string[]? rgsFileNames,
            uint nApplications,
            [In] RmUniqueProcess[]? rgApplications,
            uint nServices,
            string[]? rgsServiceNames);

        [DllImport("rstrtmgr", CharSet = CharSet.Unicode)]
        public static extern int RmGetList(
            uint dwSessionHandle,
            out uint pnProcInfoNeeded,
            ref uint pnProcInfo,
            [In, Out] RmProcessInfo[]? rgAffectedApps,
            ref uint lpdwRebootReasons);

        [DllImport("rstrtmgr")]
        public static extern int RmEndSession(uint pSessionHandle);
    }
}

 [SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    public const int MoveFileReplaceExisting = 0x00000001;
    public const int MoveFileDelayUntilReboot = 0x00000004;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, int dwFlags);
}

 [SupportedOSPlatform("windows")]
internal static class PrivilegeManager
{
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int ErrorNotAllAssigned = 1300;

    public static bool TryEnableDebugPrivilege(out string detail)
    {
        detail = string.Empty;

        var processHandle = NativeMethods.GetCurrentProcess();
        if (!NativeMethods.OpenProcessToken(processHandle, TokenAdjustPrivileges | TokenQuery, out var tokenHandle))
        {
            detail = $"OpenProcessToken failed ({Marshal.GetLastWin32Error()})";
            return false;
        }

        try
        {
            if (!NativeMethods.LookupPrivilegeValue(null, "SeDebugPrivilege", out var luid))
            {
                detail = $"LookupPrivilegeValue failed ({Marshal.GetLastWin32Error()})";
                return false;
            }

            var tokenPrivileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges = new LuidAndAttributes
                {
                    Luid = luid,
                    Attributes = SePrivilegeEnabled
                }
            };

            NativeMethods.SetLastError(0);
            if (!NativeMethods.AdjustTokenPrivileges(tokenHandle, false, ref tokenPrivileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                detail = $"AdjustTokenPrivileges failed ({Marshal.GetLastWin32Error()})";
                return false;
            }

            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotAllAssigned)
            {
                detail = "SeDebugPrivilege not assigned to token";
                return false;
            }

            detail = "token adjusted";
            return true;
        }
        finally
        {
            _ = NativeMethods.CloseHandle(tokenHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void SetLastError(int dwErrorCode);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out Luid lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AdjustTokenPrivileges(
            IntPtr tokenHandle,
            [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
            ref TokenPrivileges newState,
            int bufferLength,
            IntPtr previousState,
            IntPtr returnLength);
    }
}

 [SupportedOSPlatform("windows")]
internal static class HandleScanner
{
    public static bool TryGetLockingProcesses(string targetPath, out IReadOnlyList<LockingProcessViewModel> processes, out string message)
    {
        processes = [];
        message = string.Empty;

        var handleExePath = FindHandleExePath();
        if (string.IsNullOrWhiteSpace(handleExePath) || !File.Exists(handleExePath))
        {
            message = "Handle fallback unavailable (handle64.exe not found). Install with: winget install --id Microsoft.Sysinternals.Handle --exact";
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = handleExePath,
                Arguments = $"-accepteula \"{targetPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                message = "Failed to start handle64.exe fallback process.";
                return false;
            }

            var output = process.StandardOutput.ReadToEnd();
            var errors = process.StandardError.ReadToEnd();
            process.WaitForExit(10000);

            if (!string.IsNullOrWhiteSpace(errors))
            {
                message = $"Handle fallback stderr: {errors.Trim()}";
            }

            var parsed = ParseHandleOutput(output);
            if (parsed.Count == 0)
            {
                processes = [];
                message = "Handle fallback found no lock owners.";
                return true;
            }

            processes = parsed;
            return true;
        }
        catch (Exception ex)
        {
            message = $"Handle fallback failed: {ex.Message}";
            return false;
        }
    }

    private static string FindHandleExePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var basePath = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages", "Microsoft.Sysinternals.Handle_Microsoft.Winget.Source_8wekyb3d8bbwe");
        var preferred = Path.Combine(basePath, "handle64.exe");
        if (File.Exists(preferred))
        {
            return preferred;
        }

        var fallback = Path.Combine(basePath, "handle.exe");
        if (File.Exists(fallback))
        {
            return fallback;
        }

        return string.Empty;
    }

    private static IReadOnlyList<LockingProcessViewModel> ParseHandleOutput(string output)
    {
        var result = new Dictionary<int, LockingProcessViewModel>();
        var lines = output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            var pidMarker = "pid:";
            var pidIndex = line.IndexOf(pidMarker, StringComparison.OrdinalIgnoreCase);
            if (pidIndex <= 0)
            {
                continue;
            }

            var processName = line[..pidIndex].Trim();
            var pidSection = line[(pidIndex + pidMarker.Length)..].TrimStart();
            var pidToken = pidSection.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!int.TryParse(pidToken, out var pid) || pid <= 0)
            {
                continue;
            }

            if (result.ContainsKey(pid))
            {
                continue;
            }

            var model = new LockingProcessViewModel
            {
                ProcessId = pid,
                ProcessName = processName,
                AppType = "HandleExe",
                MainWindowTitle = string.Empty,
                ExecutablePath = string.Empty
            };

            try
            {
                using var process = Process.GetProcessById(pid);
                model = new LockingProcessViewModel
                {
                    ProcessId = pid,
                    ProcessName = process.ProcessName,
                    AppType = "HandleExe",
                    MainWindowTitle = process.MainWindowTitle,
                    ExecutablePath = process.MainModule?.FileName ?? string.Empty
                };
            }
            catch
            {
                // Keep parsed defaults when process inspection is unavailable.
            }

            result[pid] = model;
        }

        return result.Values.ToList();
    }
}