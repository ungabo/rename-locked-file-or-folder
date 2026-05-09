# Process Lock Inspector

Windows WPF desktop app that helps identify which processes are locking a file/folder and lets you:

- inspect lock owners
- select and kill locking processes
- rename immediately
- schedule a force-rename for next reboot

## Build

```powershell
dotnet build -c Release
```

## Publish Single EXE

```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```

Output:

- `bin/Release/net8.0-windows/win-x64/publish/ProcessLockInspector.exe`

## Notes

- Lock detection uses the Windows Restart Manager API.
- Killing some processes may require running the app as Administrator.
- "Force Rename on Reboot" schedules a rename operation using `MoveFileEx(..., MOVEFILE_DELAY_UNTIL_REBOOT)`.
