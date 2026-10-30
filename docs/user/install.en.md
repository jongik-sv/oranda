# Installing Oranda

[한국어](install.md)

| | Windows | macOS |
|---|---|---|
| Supported | Windows 10/11 x64 | macOS 14.7 or later, Apple Silicon |
| File | `Oranda-win-Setup.exe` (or `Oranda-win-Portable.zip`) | `Oranda-osx-Setup.pkg` |
| Download | [GitHub Releases](https://github.com/jongik-sv/oranda/releases) | same |
| Size | under 100 MB | under 100 MB |

You do not need an Oracle Client. The installers work offline, so you can copy them to machines without internet access.

## Windows

### Install
1. Run `Oranda-win-Setup.exe`. It installs for the current user without administrator rights (`%LocalAppData%\Oranda`) and adds Desktop and Start menu shortcuts.
2. Windows builds are not code signed yet (a SignPath Foundation application is pending), so SmartScreen shows **"Windows protected your PC"**.
   - Click **More info**, then click the **Run anyway** button that appears.
   - If your browser blocks the download ("not commonly downloaded"), open the downloads list and choose **… → Keep → Keep anyway**.
3. On first start the connection manager opens. Continue with [connect.md](connect.md) (Korean).

### Portable (no install)
1. Download `Oranda-win-Portable.zip` and, before extracting, remove the downloaded-from-internet mark in PowerShell: `Unblock-File .\Oranda-win-Portable.zip`
2. Extract it anywhere and run `Oranda.exe`. The portable copy updates itself the same way.

### If Windows blocks it
- PCs with **Smart App Control**, WDAC or AppLocker turned on block unsigned programs, and no "More info" link appears. Until signed releases are available, that PC's policy has to allow it (Windows Security > App & browser control > Smart App Control).
- Company PCs may block it with security software; ask your administrator.
- Installer log: `%LocalAppData%\velopack\velopack.log`.

## macOS

### Install
1. Download `Oranda-osx-Setup.pkg`.
2. Signed and notarized releases: double-click and follow the installer.
3. Unsigned releases are blocked as "unidentified developer" / "cannot verify that it is free of malware".
   - macOS 14: in Finder, **right-click (Control-click) the .pkg → Open**, then **Open** again.
   - macOS 15 and later (the right-click shortcut was removed): try to open it once, then go to **System Settings > Privacy & Security**, click **Open Anyway** at the bottom and enter your password.
   - Or remove the quarantine flag in Terminal and open it: `xattr -d com.apple.quarantine ~/Downloads/Oranda-osx-Setup.pkg`
4. Open Oranda from the Applications folder. If it is still blocked: `xattr -dr com.apple.quarantine /Applications/Oranda.app`.

## Automatic updates
- An installed copy checks GitHub Releases for a new version 20 seconds after start, once a day, and downloads it in the background. Use **Help > Check for updates** to check now.
- A new version is applied **when you quit** (or with "Restart now to apply"). Oranda asks first if you have unsaved tabs or uncommitted transactions.
- To turn it off, open **Settings > Update** and turn off checking at startup and automatic download. To use your own feed, enter it as the update server address there.
- Update packages are verified against the hashes in the release metadata. Until signing is approved, Windows updates are unsigned like the first install.

## Uninstall
- **Windows**: Settings > Apps > Installed apps > Oranda > **Uninstall**. For the portable copy, delete its folder.
- **macOS**: move Oranda from the Applications folder to the Trash.
- Settings, connections and workspaces stay after uninstalling. To remove everything, also delete:
  - Windows: `%APPDATA%\Oranda`
  - macOS: `~/Library/Application Support/Oranda`, and the `Oranda` items in Keychain Access (saved connection passwords)

## Crash reports
- If Oranda crashes, the details are saved in the log folder (Help > Open log folder).
- In Help > Error report you can review the report and press **Send** only if you agree. Nothing is sent otherwise.
- Values such as connection passwords are masked as `***`.
