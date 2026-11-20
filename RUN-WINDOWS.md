# Running Sidelight locally on Windows 10/11

Step by step: install the tools, get the code, run the desktop agent, and build and install the phone app (APK).
Run every command in **PowerShell** (not as administrator unless a step says so). For Linux see [RUN.md](RUN.md).

## Step 1: Install the tools

Open PowerShell and run these one at a time. Accept the prompts that appear.

```powershell
winget install --id Git.Git -e
```

```powershell
winget install --id Microsoft.DotNet.SDK.10 -e
```

```powershell
winget install --id EclipseAdoptium.Temurin.17.JDK -e
```

**Close PowerShell and open a new window** so the new programs are found, then check:

```powershell
git --version; dotnet --version; java -version
```

`dotnet` should show 10.x and `java` should show 17.

## Step 2: Install the Android SDK (for building the APK)

1. Download **Command line tools only → Windows** from <https://developer.android.com/studio#command-tools>.
2. Create the SDK folder and unpack the zip into it so the tools end up in `cmdline-tools\latest`:

```powershell
$sdk = "$env:LOCALAPPDATA\Android\Sdk"
New-Item -ItemType Directory -Force "$sdk\cmdline-tools" | Out-Null
Expand-Archive "$HOME\Downloads\commandlinetools-win-*_latest.zip" "$sdk\cmdline-tools" -Force
Rename-Item "$sdk\cmdline-tools\cmdline-tools" latest
```

3. Set the environment variables for your user (permanently):

```powershell
$jdk = (Get-ChildItem "C:\Program Files\Eclipse Adoptium" -Directory | Where-Object Name -like 'jdk-17*' | Select-Object -First 1).FullName
[Environment]::SetEnvironmentVariable('JAVA_HOME', $jdk, 'User')
[Environment]::SetEnvironmentVariable('ANDROID_HOME', "$env:LOCALAPPDATA\Android\Sdk", 'User')
$path = [Environment]::GetEnvironmentVariable('Path', 'User')
[Environment]::SetEnvironmentVariable('Path', "$path;$env:LOCALAPPDATA\Android\Sdk\platform-tools;$env:LOCALAPPDATA\Android\Sdk\cmdline-tools\latest\bin", 'User')
```

4. **Close PowerShell and open a new window**, then install the SDK packages and accept the licences (type `y`):

```powershell
sdkmanager "platform-tools" "platforms;android-35" "build-tools;35.0.0"
```

```powershell
sdkmanager --licenses
```

```powershell
adb version
```

The agent also looks for adb in `%LOCALAPPDATA%\Android\Sdk\platform-tools`, so it finds this one by itself.

## Step 3: Get the code

```powershell
cd $HOME
git clone https://github.com/DevPatel04/phonebox.git
cd sidelight
```

The repository is private: Git opens a browser window to sign in to GitHub the first time.
To work on the testing branch instead of `main`:

```powershell
git switch test
```

## Step 4: Check that it builds

```powershell
dotnet test agent\Sidelight.Agent.Tests
```

All tests should pass.

## Step 5: Prepare the phone

1. **Settings → About phone →** tap **Build number** seven times to unlock Developer options.
2. **Developer options →** turn on **USB debugging**.
3. Connect the phone with a **data** cable, choose **File transfer** if asked, and accept **Allow USB debugging**.
4. Check that Windows sees it:

```powershell
adb devices
```

It should list the phone as `device`. If the list is empty, Windows needs the phone maker's **USB driver**
(for vivo, Samsung, Xiaomi etc. download it from the maker's site; for Pixel use the Google USB driver from
`sdkmanager "extras;google;usb_driver"`), then re-plug the cable.

## Step 6: Run it (the easy way)

From the repo root:

```powershell
.\scripts\dev.ps1 restart
```

This builds and starts the agent, builds the debug APK, installs it on the phone and opens it.
Then open the settings and layout editor at <http://127.0.0.1:8765> (or double-click the tray icon).

### After you change something

| You changed | Run | Then |
|---|---|---|
| Anything under `android\app\src\main\assets\www\` (themes, widgets, CSS, `main.js`, `i18n.js`) | `.\scripts\dev.ps1 phone` | The app reopens with the change |
| `settings.html` or C# files in `agent\` | `.\scripts\dev.ps1 agent` | Reload the browser tab |
| Both, or not sure | `.\scripts\dev.ps1 restart` | — |

The phone loads the panel from files inside the app, not from the agent, so a panel change shows up on the phone
only after the app is rebuilt and reinstalled.

### Other commands

```powershell
.\scripts\dev.ps1 status   # is the agent running, is the phone connected
.\scripts\dev.ps1 logs     # follow the agent's output (Ctrl+C to stop following)
.\scripts\dev.ps1 stop     # stop the agent
.\scripts\dev.ps1 start    # start the agent without rebuilding the app
.\scripts\dev.ps1 test     # release metadata + agent + Playwright UI (incl. i18n) before push
```

The agent's log is `%LOCALAPPDATA%\Sidelight\agent.log`.

If the phone install fails with **`INSTALL_FAILED_UPDATE_INCOMPATIBLE ... signatures do not match`**, remove the
installed app once (this clears the app's data on the phone, not your layouts), then run `phone` again:

```powershell
adb shell pm uninstall io.github.devpatel04.sidelight
```

Debug builds are signed with a key that belongs to each PC, so installing a debug build from a different computer
needs this uninstall too.

While developing, go to **Settings → General → Updates**, set **Repository** to `DevPatel04/phonebox` and turn
**automatic updates off**, so a published release does not replace the app you are testing.

Windows Firewall may ask about network access the first time. Allow **private networks** only if you will use
Wi-Fi pairing; USB works without it.

## Step 7: Run it by hand (what the script does)

Agent:

```powershell
dotnet build agent\Sidelight.Agent -f net10.0-windows10.0.19041.0
Start-Process agent\Sidelight.Agent\bin\Debug\net10.0-windows10.0.19041.0\SidelightAgent.exe
```

Phone app:

```powershell
cd android
.\gradlew.bat assembleDebug
cd ..
adb install -r android\app\build\outputs\apk\debug\app-debug.apk
adb shell am start -n io.github.devpatel04.sidelight/.MainActivity
```

With the agent running and the phone on USB, the phone wakes and opens the panel. If it shows
"Waiting for agent", open the Sidelight app or run:

```powershell
adb reverse tcp:8765 tcp:8765
```

To stop the agent, right-click the tray icon → **Exit**, or `.\scripts\dev.ps1 stop`.

## Troubleshooting

| Problem | Fix |
|---|---|
| `dotnet`, `java`, `adb` or `sdkmanager` not recognized | Open a new PowerShell window after installing or setting variables. |
| `gradlew` says `JAVA_HOME is not set` | Repeat Step 2.3, then open a new window. |
| `SDK location not found` | Check `$env:ANDROID_HOME` points to `%LOCALAPPDATA%\Android\Sdk`; repeat Step 2.3. |
| `adb devices` empty or `unauthorized` | Data cable, File transfer mode, USB driver, accept the prompt on the phone. |
| Agent does not start, port 8765 in use | Another agent runs: `.\scripts\dev.ps1 stop`, or exit the installed Sidelight from the tray. |
| `adb.exe - Application Error (0xc0000142)` at shutdown | Known Windows adb issue; harmless, see issue #179 upstream. |
| Update check shows `404 (Not Found)` | The repository is private or has no release yet; leave automatic updates off. |
