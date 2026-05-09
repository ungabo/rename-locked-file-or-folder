using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace ProcessLockInspector;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<LockingProcessViewModel> _lockingProcesses = [];

    public MainWindow()
    {
        InitializeComponent();
        ProcessGrid.ItemsSource = _lockingProcesses;
        AppendStatus("Ready.");
        AppendStatus(IsRunningAsAdministrator() ? "Running as Administrator." : "Running without Administrator rights.");
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
            if (!IsRunningAsAdministrator())
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

internal static class NativeMethods
{
    public const int MoveFileReplaceExisting = 0x00000001;
    public const int MoveFileDelayUntilReboot = 0x00000004;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, int dwFlags);
}