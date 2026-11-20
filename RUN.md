# Running Sidelight locally (Linux)

On Windows, follow [RUN-WINDOWS.md](RUN-WINDOWS.md) instead.

How to build and run the desktop agent and the phone app from this checkout, and how to see your changes on the phone.

## 1. One-time setup

### .NET 10 SDK (for the agent)

```bash
sudo apt install dotnet-sdk-10.0
```

```bash
dotnet --version
```

### JDK 17 and the Android SDK (for the phone app)

```bash
sudo apt install openjdk-17-jdk android-sdk-platform-tools-common
```

Download the Android **command-line tools** from <https://developer.android.com/studio#command-tools> and unpack them
so that `~/Android/Sdk/cmdline-tools/latest/bin/sdkmanager` exists. Then add these lines to the end of `~/.bashrc`:

```bash
export JAVA_HOME=/usr/lib/jvm/java-17-openjdk-amd64
export ANDROID_HOME=$HOME/Android/Sdk
export PATH=$PATH:$ANDROID_HOME/cmdline-tools/latest/bin:$ANDROID_HOME/platform-tools
```

Reload the shell and install the SDK packages:

```bash
source ~/.bashrc
```

```bash
sdkmanager "platform-tools" "platforms;android-35" "build-tools;35.0.0"
```

### The phone

1. **Settings → About phone →** tap **Build number** seven times to unlock Developer options.
2. **Developer options →** turn on **USB debugging**.
3. Connect the phone with a **data** cable (not a charge-only cable), choose **File transfer** if asked, and accept
   the **Allow USB debugging** prompt on the phone.
4. Check that the computer sees it:

```bash
adb devices
```

It should list one device as `device` (not `unauthorized` or empty).

### First install of the app

If the phone has Sidelight from somewhere else (a release APK, or a debug build from another computer), it is
signed with another key and must be removed once. This deletes the app's data on the phone (pairings, not your
layouts, which live on the PC):

```bash
adb shell pm uninstall io.github.devpatel04.sidelight
```

## 2. Run it (the easy way)

From the repo root:

```bash
./scripts/dev.sh restart
```

This builds and starts the agent in the background, builds the app, installs it on the phone and opens it.
Then open the settings and layout editor at <http://127.0.0.1:8765>.

### After you change something

| You changed | Run | Then |
|---|---|---|
| Anything under `android/app/src/main/assets/www/` (themes, widgets, CSS, `main.js`, `i18n.js`) | `./scripts/dev.sh phone` | The app reopens with the change |
| `settings.html` or C# files in `agent/` | `./scripts/dev.sh agent` | Reload the browser tab |
| Both, or not sure | `./scripts/dev.sh restart` | — |

The phone loads the panel from files inside the app, not from the agent, so a panel change shows up on the phone
only after the app is rebuilt and reinstalled.

### Other commands

```bash
./scripts/dev.sh status   # is the agent running, is the phone connected
./scripts/dev.sh logs     # follow the agent's output (Ctrl+C to stop following)
./scripts/dev.sh stop     # stop the agent
./scripts/dev.sh start    # start the agent without rebuilding the app
./scripts/dev.sh test     # release metadata + agent + Playwright UI (incl. i18n) before push
```

The agent's output is written to `~/.local/state/sidelight-dev/agent.log`.

## 3. Run it by hand (what the script does)

Agent, in its own terminal (stop with Ctrl+C):

```bash
dotnet run --project agent/Sidelight.Agent -f net10.0
```

Phone app, in another terminal:

```bash
cd android && ./gradlew assembleDebug
```

```bash
adb install -r app/build/outputs/apk/debug/app-debug.apk
```

## On Windows

See [RUN-WINDOWS.md](RUN-WINDOWS.md) for the full step-by-step setup, including the APK.

## 4. Tests

Before push, run the same local suite CI covers for metadata, agent and browser UI (including Vietnamese leftover scans):

```bash
./scripts/dev.sh test
# or one Playwright file: ./scripts/dev.sh test i18n-coverage.spec.js
```

Or by hand:

```bash
dotnet test agent/Sidelight.Agent.Tests
```

```bash
python3 tools/check-release.py
```

```bash
cd android && ./gradlew lintDebug
```

Browser UI tests need Node.js and npm:

```bash
cd tools/ui-tests && npm ci && npx playwright install chromium && npx playwright test
```

Panel and settings with made-up data, no agent or phone needed (size the window to 780×360):

```bash
python3 tools/demo-server.py
```

Then open <http://127.0.0.1:8790/panel/>.

## 5. Updates while developing

In **Settings → General → Updates**, keep **Repository** set to `DevPatel04/sidelight` and turn **automatic updates
off** while you work. Otherwise the agent may install a published release over the app you are testing. "Check now"
returns 404 until the repository is public and has a release.

## Troubleshooting

| Problem | Fix |
|---|---|
| `adb: command not found` | Run `source ~/.bashrc` or open a new terminal. `dev.sh` finds adb by itself. |
| `adb devices` shows nothing | Use a data cable, pick **File transfer** on the phone, re-plug, accept the debugging prompt. |
| `INSTALL_FAILED_UPDATE_INCOMPATIBLE ... signatures do not match` | The installed app has another signing key: `adb shell pm uninstall io.github.devpatel04.sidelight`, then install again. |
| `adb uninstall` says `DELETE_FAILED_INTERNAL_ERROR` (vivo) | Use `adb shell pm uninstall io.github.devpatel04.sidelight` instead. |
| `address already in use` on port 8765 | An agent is still running: `./scripts/dev.sh stop` (or `pkill -f SidelightAgent`). |
| Ctrl+C or `stop` takes about 10 seconds | The agent is slow to shut down; `dev.sh` force-stops it after 10 seconds. |
| `gradlew` complains about `JAVA_HOME` | Install `openjdk-17-jdk` and add the `JAVA_HOME` line above to `~/.bashrc`. |
| Phone shows "Waiting for agent" | Check `./scripts/dev.sh status`; if the agent runs, `adb reverse tcp:8765 tcp:8765`. |
