# PhoneBox --- Complete From-Scratch Setup Guide

## Windows 10/11 + Android phone + USB and Wi-Fi

This guide takes you from a fresh Windows PC and Android phone to a
working PhoneBox desk panel. It includes the official-release
installation method and an optional source-code installation method.

> **Recommended for this PC:** use the official prebuilt Windows Agent
> and official Android APK. Windows Smart App Control previously blocked
> a locally built agent on this PC, while the official release agent ran
> successfully. Do not disable Smart App Control just to install
> PhoneBox.

------------------------------------------------------------------------

## 1. What PhoneBox is and what you need

PhoneBox has two parts:

1.  **Android app** --- the full-screen dashboard displayed on your
    phone.
2.  **Windows Agent** --- runs on your PC and sends computer information
    to the phone.

The phone can communicate with the PC over USB. After one successful USB
pairing, it can use secure Wi-Fi or Tailscale instead.

### Requirements

-   Windows 10 or Windows 11 PC.
-   Android 9 or newer phone.
-   USB data cable (some cables only charge).
-   Internet connection for downloading the software.
-   Android Developer Options and USB debugging enabled.
-   Android Platform Tools (`adb`) on Windows.
-   .NET 10 Desktop Runtime for the prebuilt Windows Agent.

Official project: <https://github.com/ersinkoc/phonebox>

Official releases: <https://github.com/ersinkoc/phonebox/releases>

------------------------------------------------------------------------

## 2. Understand the network ports

PhoneBox uses two different ports. Do not mix them up.

  ----------------------------------------------------------------------------
  Port                    Purpose                 Address on this PC
  ----------------------- ----------------------- ----------------------------
  `8765`                  Local settings          `http://127.0.0.1:8765`
                          dashboard on the PC     

  `8766`                  Secure Wi-Fi/Tailscale  `https://192.168.1.9:8766`
                          phone connection        after pairing
  ----------------------------------------------------------------------------

The phone should normally receive the secure connection details during
USB pairing. You should not need to manually type the HTTPS address or
copy a pairing token.

------------------------------------------------------------------------

# Part A --- Prepare Windows

## 3. Install Android Platform Tools (ADB)

ADB lets the Windows Agent communicate with the Android phone over USB.

1.  Open **Windows Terminal** or **PowerShell**.

2.  Run:

    ``` powershell
    winget install Google.PlatformTools
    ```

3.  If Windows asks for confirmation, approve the installation.

4.  Close and reopen PowerShell.

5.  Check that ADB is installed:

    ``` powershell
    adb version
    ```

You should see an Android Debug Bridge version.

If `winget` is unavailable, download Android SDK Platform Tools from
Google's official page:

<https://developer.android.com/tools/releases/platform-tools>

Extract the ZIP and either run `adb.exe` from its extracted folder or
add that folder to your Windows PATH.

## 4. Install the .NET 10 Desktop Runtime

The prebuilt Windows Agent needs the .NET 10 Desktop Runtime.

1.  Open the official .NET download page:

    <https://dotnet.microsoft.com/en-us/download/dotnet/10.0>

2.  Find **.NET Desktop Runtime 10** for Windows x64.

3.  Download and install it.

4.  Restart PowerShell after installation.

You can check installed runtimes with:

``` powershell
dotnet --list-runtimes
```

Look for a `Microsoft.WindowsDesktop.App 10.x` entry. If the `dotnet`
command is not found, the runtime may still be installed; check Windows
Installed Apps.

------------------------------------------------------------------------

# Part B --- Install PhoneBox

## 5. Download the official Windows Agent and Android APK

1.  Open the official releases page:

    <https://github.com/ersinkoc/phonebox/releases>

2.  Open the latest stable release.

3.  Download the **Windows x64 Agent ZIP**.

4.  Download the **Android APK** from the same release.

5.  Keep both files from the same release version where possible.

The release page provides the official signed APK and platform-specific
agent archives. Read the release asset names carefully; choose Windows
x64 for a normal 64-bit Windows PC.

## 6. Extract the Windows Agent

1.  Open your Windows **Downloads** folder.

2.  Right-click the Windows Agent ZIP.

3.  Choose **Extract All**.

4.  Extract it to:

    ``` text
    C:\Users\YOUR-WINDOWS-USER\Downloads\PhoneBoxAgent
    ```

For this PC, the folder is:

``` text
C:\Users\ankan\Downloads\PhoneBoxAgent
```

The folder should contain `PhoneBoxAgent.exe`, `PhoneBoxAgent.dll`, and
other agent files.

If Windows marks the downloaded ZIP or its extracted files as blocked,
you can unblock the files you downloaded from the official PhoneBox
release. Open PowerShell and run:

``` powershell
Get-ChildItem "$env:USERPROFILE\Downloads\PhoneBoxAgent" -File | Unblock-File
```

Only do this for files obtained from the official PhoneBox release page.
Do not disable Windows Smart App Control or other security protections
to get an unknown executable to run.

## 7. Install the Android APK

There are two ways to install the APK.

### Method A --- Install from the phone

1.  Transfer the downloaded APK to the phone (USB file transfer, cloud
    storage, or another method).
2.  Open the APK on the phone.
3.  If Android asks, allow your file manager/browser to install apps
    from that source.
4.  Confirm **Install**.
5.  Open PhoneBox once the installation finishes.

You can turn off the temporary "Install unknown apps" permission again
after installation.

### Method B --- Install using ADB

Connect the phone to the PC and locate the APK file. In PowerShell, use
the actual path to your downloaded APK:

``` powershell
adb install -r "$env:USERPROFILE\Downloads\PhoneBox.apk"
```

Replace `PhoneBox.apk` with the exact APK filename you downloaded.

If ADB reports `Success`, the app is installed. If it says the app is
not allowed or the device is unauthorized, complete the phone USB
debugging steps below and try again.

If PhoneBox is already installed, you do not need to reinstall it.

------------------------------------------------------------------------

# Part C --- Prepare the Android phone

## 8. Enable Developer Options

The names of Android settings vary by manufacturer.

1.  Open **Settings** on the phone.
2.  Open **About phone**.
3.  Find **Build number**. On some phones it is under **Software
    information**.
4.  Tap **Build number** seven times.
5.  Enter the phone PIN/password if requested.
6.  Android should say that Developer Options are enabled.

## 9. Enable USB debugging

1.  Return to the main Android **Settings** screen.
2.  Open **System → Developer options**. Some phones show Developer
    options directly in Settings.
3.  Turn on **USB debugging**.
4.  Read the warning and confirm if you want to allow debugging with
    this PC.

Keep USB debugging enabled while using PhoneBox over USB. You can turn
it off later if you stop using the USB connection, but USB pairing and
updates need ADB access.

## 10. Connect the phone and authorize the PC

1.  Unlock the phone.

2.  Connect it to the PC using a USB data cable.

3.  If Android shows a USB-use notification, choose a data-transfer
    option if needed.

4.  If Android displays **Allow USB debugging?**, check the computer
    fingerprint prompt and tap **Allow**. Only authorize your own PC.

5.  In PowerShell, run:

    ``` powershell
    adb devices
    ```

A successful connection looks similar to:

``` text
List of devices attached
XXXXXXXX    device
```

If the status says `unauthorized`, unlock the phone and accept the USB
debugging prompt. Then run `adb devices` again.

If no device appears, try another USB cable or USB port. A charge-only
cable cannot carry ADB data.

------------------------------------------------------------------------

# Part D --- Start the Windows Agent

## 11. Start PhoneBox Agent manually

1.  Open **Windows Terminal** or **PowerShell**.

2.  Run:

    ``` powershell
    & "$env:USERPROFILE\Downloads\PhoneBoxAgent\PhoneBoxAgent.exe"
    ```

3.  Keep this PowerShell window open. Closing it stops the agent.

4.  Open this address in your PC browser:

    <http://127.0.0.1:8765>

You should see the PhoneBox settings dashboard.

### Start the Agent again if you close its window

Open a new PowerShell window and run the same command:

``` powershell
& "$env:USERPROFILE\Downloads\PhoneBoxAgent\PhoneBoxAgent.exe"
```

### Stop the Agent

Click the PowerShell window and press:

``` text
Ctrl + C
```

## 12. Confirm the phone works over USB

With the Agent running and the phone connected:

1.  Open PhoneBox on the Android phone.
2.  Wait for the panel to load.
3.  If it says the PC is not connected, check `adb devices` again.
4.  Confirm that the device status is `device`, not `unauthorized`.

If the dashboard is available but the phone is not connected, the
Windows Agent and the Android ADB connection are separate things: both
must be working.

------------------------------------------------------------------------

# Part E --- Enable Wi-Fi pairing

## 13. Open Wi-Fi & Tailscale settings

On the PC:

1.  Open <http://127.0.0.1:8765>.

2.  Go to **Settings → General → Wi-Fi & Tailscale**.

3.  Find the main Wi-Fi/LAN enable switch.

4.  Turn Wi-Fi/LAN access **On**.

5.  Leave the port at its default value, normally:

    ``` text
    8766
    ```

6.  Check the **Addresses offered to the phone** list.

7.  Confirm that the PC's Wi-Fi address is listed.

8.  Save the setting if a Save button is shown.

9.  Restart the PhoneBox Agent if the settings page asks you to.

**Important:** The **New key (unpair phones)** control is not the Wi-Fi
enable switch. Leave it Off during normal setup. Turning it on is for
rotating the pairing key and unpairing phones.

## 14. Know which PC network address to use

On this PC, the Wi-Fi IPv4 address is:

``` text
192.168.1.9
```

The address list may also show:

``` text
172.22.201.149
```

That second address belongs to another/virtual network adapter. For a
phone connected to the same normal Wi-Fi network as this PC, use the
`192.168.1.9` network path.

To check the PC's current IPv4 addresses later, run:

``` powershell
ipconfig
```

Find the **Wireless LAN adapter Wi-Fi** section and read its **IPv4
Address**. Your address may change if your router assigns a new one.

## 15. Restart the Agent after enabling Wi-Fi

If the Agent is running in PowerShell:

1.  Click its PowerShell window.

2.  Press **Ctrl + C**.

3.  Start it again:

    ``` powershell
    & "$env:USERPROFILE\Downloads\PhoneBoxAgent\PhoneBoxAgent.exe"
    ```

4.  Keep the window open.

## 16. Pair the phone over USB

PhoneBox's Wi-Fi setup uses USB for the first pairing.

1.  Keep the phone connected to the PC by USB.
2.  Keep the phone unlocked.
3.  Make sure PhoneBox is open on the phone.
4.  Make sure Wi-Fi/LAN access is enabled in the PC dashboard.
5.  Restart the Agent if you have not already done so.
6.  Follow the pairing prompt shown by PhoneBox and wait for the secure
    pairing to finish.

During pairing, PhoneBox gives the phone the PC's connection
information, a secret pairing key, and the fingerprint of the PC's HTTPS
certificate. This is why you should not manually invent a URL or copy a
token into random fields.

### Windows Firewall prompt

If Windows Firewall asks whether to allow PhoneBox:

-   Allow it on **Private networks** when you are using your trusted
    home Wi-Fi.
-   Do not enable Public network access unless you specifically need it.
-   If you use Tailscale, allow the relevant network profile as
    appropriate.

## 17. Test the wireless connection

After the initial USB pairing completes:

1.  Keep the PC and phone connected to the same Wi-Fi network.
2.  Confirm the Windows Agent is still running.
3.  Disconnect the USB cable.
4.  Open or refresh PhoneBox on the phone.
5.  Check that the panel remains connected and updates.

The phone should now use the paired secure HTTPS connection on port
`8766`. It can fall back to USB when the cable is connected.

------------------------------------------------------------------------

# Part F --- Optional features

## 18. Tailscale (optional)

You do not need Tailscale if the phone and PC are on the same local
Wi-Fi network.

Use Tailscale if you want the phone to connect while it and the PC are
on different networks or in different locations.

1.  Install Tailscale on the PC and Android phone.
2.  Sign both devices into the same Tailscale network.
3.  Enable PhoneBox Wi-Fi/Tailscale access.
4.  Pair the phone over USB once if it has not already been paired.
5.  Allow the relevant Tailscale network profile in Windows Firewall if
    prompted.

## 19. Start PhoneBox automatically when Windows starts

The source installer supports an autostart option:

``` powershell
.\scripts\install.ps1 -Apk -Autostart
```

That command is for the **source-code installation route** described in
Part G; it is not a command to run from the extracted release ZIP
folder.

If you installed the prebuilt release ZIP manually, first check whether
the release or tray/settings UI offers an autostart option. Otherwise,
you can create a Windows Startup shortcut to the Agent executable. Do
not create multiple startup entries if one already exists.

## 20. Customize the dashboard

Open the PC settings page:

<http://127.0.0.1:8765>

The editor lets you:

-   Add, remove, and arrange dashboard pages.
-   Start with built-in page templates.
-   Add widgets for CPU, GPU, RAM, storage, network, weather, clock,
    media, and more.
-   Configure shortcuts and webhooks.
-   Change the theme, layout, orientation, and page switching behavior.
-   Configure optional integrations such as GitHub, Claude Code, Codex,
    OBS, and Home Assistant.

The phone displays the pages configured from the PC editor.

------------------------------------------------------------------------

# Part G --- Optional: install from source instead of using release files

Use this section only if you want to build/install PhoneBox from its
GitHub source. It is not required if you already downloaded the official
Agent ZIP and APK.

The official Windows quick-start uses .NET 10 SDK, Git, and ADB.

## 21. Install the source-build requirements

Open PowerShell and run:

``` powershell
winget install Microsoft.DotNet.SDK.10
```

``` powershell
winget install Google.PlatformTools
```

Install Git for Windows if it is not already installed:

<https://git-scm.com/download/win>

Close and reopen PowerShell after installing tools.

Check:

``` powershell
dotnet --version
```

``` powershell
adb version
```

## 22. Download the source code

Run:

``` powershell
cd "$env:USERPROFILE\OneDrive\Projects"
```

Then:

``` powershell
git clone https://github.com/ersinkoc/phonebox
```

Enter the project folder:

``` powershell
cd phonebox
```

If the folder already exists, do not clone it again. Enter the existing
folder instead.

## 23. Run the official Windows installer

Connect the phone, unlock it, enable USB debugging, and authorize the PC
first. Then run:

``` powershell
.\scripts\install.ps1 -Apk -Autostart
```

This installs the Agent and phone app and configures startup. If
multiple Android devices are connected, the README says to specify the
intended device using `-Serial <id>`.

**Smart App Control note:** On this PC, an agent built locally was
previously blocked by Windows Code Integrity. The official prebuilt
release agent ran successfully. If a locally built executable is
blocked, do not disable Smart App Control as a workaround; use the
official release binaries instead.

------------------------------------------------------------------------

# Part H --- Troubleshooting

## 24. The dashboard does not open

Check that the Agent is running in PowerShell. If the window was closed,
start it again:

``` powershell
& "$env:USERPROFILE\Downloads\PhoneBoxAgent\PhoneBoxAgent.exe"
```

Then open:

<http://127.0.0.1:8765>

## 25. `adb devices` shows `unauthorized`

1.  Unlock the phone.

2.  Look for the **Allow USB debugging?** dialog.

3.  Approve the prompt for your PC.

4.  Run:

    ``` powershell
    adb devices
    ```

If the prompt does not appear, disconnect/reconnect USB. You can also
use Developer Options → Revoke USB debugging authorizations, reconnect,
and approve the new prompt.

## 26. `adb devices` shows no device

-   Try a different USB cable that supports data.
-   Try another USB port directly on the PC.
-   Unlock the phone.
-   Select a USB data mode if Android requests one.
-   Re-run `adb devices`.

## 27. The phone works on USB but not Wi-Fi

Check these items in order:

1.  The main Wi-Fi/LAN access switch is On.
2.  The port is `8766`.
3.  The phone and PC are on the same Wi-Fi network.
4.  The Agent was restarted after Wi-Fi was enabled.
5.  The phone completed its first USB pairing.
6.  Windows Firewall allowed PhoneBox on the Private network.
7.  The PC's current Wi-Fi IPv4 address is still the one offered to the
    phone.

Do not use the dashboard URL/port (`127.0.0.1:8765`) as the phone's
Wi-Fi endpoint.

## 28. The Agent window was closed

That stops the Agent. Open PowerShell and run:

``` powershell
& "$env:USERPROFILE\Downloads\PhoneBoxAgent\PhoneBoxAgent.exe"
```

Keep the window open while using PhoneBox.

## 29. Windows blocks the executable

The official release files can sometimes carry a downloaded-file
security mark. If you downloaded the ZIP from the official PhoneBox
GitHub releases page, you can unblock the extracted files with:

``` powershell
Get-ChildItem "$env:USERPROFILE\Downloads\PhoneBoxAgent" -File | Unblock-File
```

Do not disable Smart App Control, Defender, or Windows security
protections to run an untrusted executable. If Windows still blocks the
official release, record the exact warning or Code Integrity event and
investigate that message.

## 30. The phone loses its pairing

In PhoneBox settings, **New key (unpair phones)** is intended to rotate
the pairing key and unpair existing phones. Do not use it as a routine
troubleshooting toggle. If you intentionally generate a new key, pair
the phone again over USB.

------------------------------------------------------------------------

# Part I --- Security and maintenance

## 31. Security basics

-   Download the Agent and APK only from the official PhoneBox GitHub
    releases page.
-   Keep the Agent updated.
-   Do not share pairing keys, LAN tokens, webhook tokens, or
    configuration files containing secrets.
-   Do not expose the dashboard or LAN service directly to the public
    internet.
-   Allow Windows Firewall access only on the network profiles you
    actually use.
-   Leave **New key (unpair phones)** Off unless you intend to unpair
    devices and rotate the pairing key.
-   If a secret was accidentally exposed, rotate it in the PhoneBox
    settings where supported.

## 32. Where PhoneBox stores its settings

On Windows, PhoneBox settings are stored at:

``` text
%APPDATA%\PhoneBox\config.json
```

You can open the folder from PowerShell with:

``` powershell
explorer "$env:APPDATA\PhoneBox"
```

Use the dashboard's settings screen for normal configuration. Avoid
editing raw JSON unless you know what a setting does. Never post the
config file publicly because it can contain secret tokens.

------------------------------------------------------------------------

# Final installation checklist

-   [ ] Windows 10/11 ready
-   [ ] .NET 10 Desktop Runtime installed
-   [ ] ADB / Android Platform Tools installed
-   [ ] Android 9+ phone ready
-   [ ] Developer Options enabled
-   [ ] USB debugging enabled
-   [ ] Phone authorized this PC
-   [ ] Official PhoneBox APK installed
-   [ ] Official Windows Agent ZIP extracted
-   [ ] Agent starts in PowerShell
-   [ ] Dashboard opens at `http://127.0.0.1:8765`
-   [ ] Phone connects over USB
-   [ ] Wi-Fi/LAN access enabled
-   [ ] LAN port is `8766`
-   [ ] Correct PC Wi-Fi address is offered
-   [ ] Agent restarted after enabling Wi-Fi
-   [ ] Initial USB pairing completed
-   [ ] Windows Firewall allowed Private network access if prompted
-   [ ] Phone stays connected after USB is unplugged

------------------------------------------------------------------------

## This PC's known setup details

These are the values observed during the previous setup session. If you
reinstall Windows, change routers, or move to another network, check
them again.

  Setting                         Known value
  ------------------------------- ------------------------------------------
  Windows user folder             `C:\Users\ankan`
  Agent folder                    `C:\Users\ankan\Downloads\PhoneBoxAgent`
  Agent executable                `PhoneBoxAgent.exe`
  PC Wi-Fi IPv4 address           `192.168.1.9`
  Router/gateway                  `192.168.1.1`
  PhoneBox dashboard              `http://127.0.0.1:8765`
  PhoneBox secure LAN port        `8766`
  Other offered adapter address   `172.22.201.149`
  Windows Smart App Control       On

**Remember:** The Agent must be running on the PC. If you close its
PowerShell window and have not configured autostart, the phone will lose
its connection to the PC.
