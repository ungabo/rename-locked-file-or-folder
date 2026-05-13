# Rename Locked File or Folder

A Windows desktop application that detects which processes are preventing a file or folder from being renamed, and provides options to terminate those processes or schedule the rename for the next system reboot.

## ⚠️ IMPORTANT: Administrator Privileges Required

**This application MUST be run as Administrator to function properly.** Without admin privileges, the lock detection and process termination will not work.

To run as admin:
1. Right-click `ProcessLockInspector.exe`
2. Select "Run as Administrator"
3. Click "Yes" when prompted by User Account Control (UAC)

## System Requirements

- **Windows 10 or later** (Windows 11 recommended)
- **.NET Runtime 8.0** (automatically installed with the app if using the self-contained executable)
- **Administrator account** with UAC enabled
- **Sysinternals Handle.exe** (automatically used as a fallback for lock detection)

## Installation

### Option 1: Use Pre-Built Executable (Recommended)

1. Download `ProcessLockInspector.exe` from the `bin/Release/net8.0-windows/win-x64/publish/` folder
2. Place it anywhere accessible (e.g., Desktop, C:\Tools\)
3. Right-click and select "Run as Administrator"

### Option 2: Build from Source

**Prerequisites:**
- .NET SDK 8.0 or later (download from https://dotnet.microsoft.com/download)
- Visual Studio or Visual Studio Code with C# extensions

**Build Steps:**
1. Open PowerShell in the project folder
2. Run: `dotnet publish -c Release -r win-x64 --self-contained`
3. Find the executable at: `bin/Release/net8.0-windows/win-x64/publish/ProcessLockInspector.exe`

## Usage

### Step 1: Select Target File or Folder

Click one of the buttons at the top:
- **"Browse File"** – to select a specific file
- **"Browse Folder"** – to select a directory

A file browser dialog will open. Select the file or folder you want to rename.

### Step 2: Analyze Locks

Click the **"Analyze Locks"** button to scan for processes holding locks on your target.

The app will:
1. Query the Windows Restart Manager API (primary method)
2. If blocked by access restrictions, fall back to file handle enumeration with SeDebugPrivilege
3. If still no results, use Sysinternals Handle.exe as a final fallback

The process list will populate in the grid below. Each process shows:
- **Process Name** – the executable name (e.g., `code.exe`, `node.exe`)
- **PID** – the process ID (e.g., `12345`)
- **Mark for Kill** – checkbox to select processes for termination

### Step 3: Review and Select Processes

Review the process list carefully. Use the **"Select All"** and **"Select None"** buttons to quickly toggle all checkboxes, or manually check individual processes you want to terminate.

**Warning:** Killing processes will terminate them immediately and may cause data loss if they have unsaved work.

### Step 4: (Optional) Kill Selected Processes

If you want to immediately free up the file/folder, check the box next to each process you want to terminate, then click:
- **"Kill Selected"** – immediately terminates the selected processes

After killing, proceed to Step 5.

### Step 5: Rename the File or Folder

Choose one of two rename options:

#### Option A: Rename Now
Click **"Rename Now"** to rename immediately. This will only succeed if no processes are locking the file/folder.

#### Option B: Force Rename on Reboot
Click **"Force Rename on Reboot"** to schedule a rename for the next system reboot. This works by using the Windows `MoveFileEx` API with the `MOVEFILE_DELAY_UNTIL_REBOOT` flag. The rename will complete automatically after the next restart.

A dialog will prompt you to enter the new name. Enter the desired filename (without the full path).

## Troubleshooting

### "No lock owners found" message appears

This can mean:
1. **The file/folder is actually unlocked.** Try renaming immediately.
2. **The processes are protected system processes.** These cannot be forcibly killed; try rebooting first.
3. **Handle.exe is not available.** Ensure Sysinternals tools are installed or allow the app to download them automatically.

**Solution:** Try the "Force Rename on Reboot" option to schedule the rename after your next restart.

### "Access Denied" error during lock detection

This occurs when:
- The app is **not running as Administrator**
- You are attempting to scan a system-protected file

**Solution:**
1. Right-click the app and select "Run as Administrator"
2. Click "Yes" on the UAC prompt
3. Try scanning again

### "Cannot rename: Access Denied" during rename

The file/folder is still locked by running processes.

**Solution:**
1. Click "Analyze Locks" again to find any remaining processes
2. Kill those processes using the "Kill Selected" button
3. Try renaming again, or use "Force Rename on Reboot"

### The process list is empty but I know something has the file locked

This indicates processes holding the lock are protected (e.g., System, csrss.exe, services).

**Solution:** Use "Force Rename on Reboot" to schedule the rename after the next system restart.

### Sysinternals Handle.exe errors

The app uses Handle.exe as a fallback if the primary lock detection methods don't work.

**Solution:** Manually install Sysinternals tools:
```powershell
winget install Sysinternals.Sysinternals
```

Or download them from: https://docs.microsoft.com/en-us/sysinternals/downloads/handle

## Technical Details

### Lock Detection Methods (in order)

1. **Windows Restart Manager API** (fastest, primary method)
   - Queries the system for processes using a specific file/folder
   - Requires admin privileges
   - May be blocked by access restrictions

2. **SeDebugPrivilege + File Handle Enumeration** (fallback)
   - Enumerates all open file handles in the system
   - Requires the `SeDebugPrivilege` token privilege
   - Works on elevated admin processes

3. **Sysinternals Handle.exe** (final fallback)
   - Subprocess call to `handle64.exe` (or `handle.exe` on 32-bit systems)
   - Parses command output to identify locking processes
   - Most reliable but slower

### Process Termination

- Uses Windows `Process.Kill()` API with tree-kill flag to terminate process and all child processes
- Requires the process to be running under the same or lower privilege level as the app

### Rename Scheduling

- **Immediate rename:** Uses `File.Move()` or `Directory.Move()` and succeeds only if the file/folder is not locked
- **Reboot rename:** Uses Windows `MoveFileEx()` API with `MOVEFILE_DELAY_UNTIL_REBOOT` flag; works even if the file/folder is still locked

## License

This project is provided as-is for Windows system administration and file management purposes.

## Support

If you encounter issues:
1. Ensure the app is **running as Administrator**
2. Check that your Windows OS is Windows 10 or later
3. Verify Sysinternals Handle.exe is installed (`winget install Sysinternals.Sysinternals`)
4. Try "Force Rename on Reboot" if immediate operations fail

---

**Built with:** .NET 8 WPF, Windows Restart Manager API, Sysinternals Handle.exe integration
