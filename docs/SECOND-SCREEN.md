# Phone as a second screen

The **Second screen** widget shows one of the computer's monitors live on the phone. Point it at an *extra, virtual*
monitor and the phone becomes an extended screen: drag a window onto it on the computer, then watch it and tap it on
the phone, while your real monitors stay free for other work. Pair it with the **Keyboard** and **Trackpad** widgets
to type and point on that screen.

What it is and what it isn't:

- It is a picture of the monitor, a few frames a second (1 to 15, 8 by default), encoded as JPEG by ffmpeg (GStreamer
  on Wayland). It suits chat, a terminal, logs, a dashboard, documentation or a music player. It doesn't suit video or
  games.
- It works on **Windows**, on **Linux with X11**, and on **Linux with Wayland** through the desktop's screen-sharing
  portal (GNOME, KDE Plasma and others with xdg-desktop-portal). macOS can't be captured this way yet, and the widget
  says so.
- Sidelight doesn't install a display driver. The virtual monitor comes from one of the steps below.

## 1. Install ffmpeg (or GStreamer on Wayland)

The cameras use the same ffmpeg. Install it so it is on `PATH` (`winget install ffmpeg`, `sudo apt install ffmpeg`), or
set its full path in **Settings › Sources › Cameras › ffmpeg**.

On Wayland the picture comes from PipeWire, read with GStreamer and its PipeWire plugin: `gstreamer1.0-pipewire`
and `gstreamer1.0-plugins-good` on Debian and Ubuntu, `pipewire-gstreamer` and `gstreamer1-plugins-good` on Fedora,
`gst-plugin-pipewire` and `gst-plugins-good` on Arch. GNOME and KDE usually have them already. Clicking on a virtual
monitor on GNOME also uses libei (`libei1`), which GNOME 46 and later bring along.

## 2. Add a virtual monitor

### Windows

Windows creates an extra monitor only through a display driver. Sidelight uses the open-source **Virtual Display
Driver** (`github.com/VirtualDrivers/Virtual-Display-Driver`, MIT licence).

**One click:** open **Settings › Sources › Second screen** and press **Install the virtual display driver**. Sidelight
installs the driver's control app, VDD Control, with winget (`VirtualDrivers.Virtual-Display-Driver`; winget checks the
download against the hash in its manifest) and opens it. In VDD Control press **Install** and allow the Windows
administrator prompt, then add a monitor. The card shows whether the driver is there; press **Check again** after
installing. Later, **Open VDD Control** adds, removes or resizes the virtual monitor.

Without winget, download VDD Control from the project's Releases page and do the same by hand. Then:

1. Open **Settings › System › Display**. A new monitor appears. Choose **Extend these displays**.
2. Set its resolution to the phone's shape for a sharp picture, for example 1600 × 720 for a 20:9 phone in landscape.
   A lower resolution means bigger text on the phone.
3. Drag it in the arrangement to where you want it (usually right of the main monitor).

A spare monitor port with an HDMI "dummy plug" works too, and needs no driver.

### Linux (X11)

Many GPU drivers have an unused or virtual output. Add a mode and place it beside your monitor, for example with Intel's
`VIRTUAL1`:

```sh
cvt 1600 720                     # prints a "Modeline" line; pass everything after the word Modeline to --newmode
xrandr --newmode "1600x720_60.00" <numbers from cvt>
xrandr --addmode VIRTUAL1 1600x720_60.00
xrandr --output VIRTUAL1 --mode 1600x720_60.00 --right-of eDP-1
```

Without such an output, the `xf86-video-dummy` driver or an HDMI dummy plug does the same. `xrandr --listmonitors`
shows the monitors Sidelight will see.

### Linux (Wayland: GNOME, KDE Plasma)

On Wayland no program may read the screen or move the pointer by itself; the desktop asks you. Sidelight uses the
standard screen-sharing portal for both, so:

- **One click for a virtual monitor:** open **Settings › Sources › Second screen**, set the size (the phone's shape in
  landscape, 1600 × 720 by default) and press **Add a virtual monitor**. The desktop shows its sharing dialog on the
  computer; allow it. A new monitor appears in **Settings › Displays**, where you can place it beside your own. It
  stays while Sidelight runs and comes back when Sidelight starts again, without asking again. **Remove the virtual
  monitor** takes it away.
- **Or show a real monitor:** without the virtual monitor, the first time the widget shows the screen the desktop asks
  which monitor to share. The widget's **Monitor** choice doesn't apply on Wayland; the dialog's does.
- **The answer is remembered.** Sidelight keeps the portal's restore token, so the dialog isn't shown again. While
  something is shared the desktop shows its sharing indicator; stopping the sharing there, or refusing the dialog, is
  respected: nothing is asked again until you press **Allow again** in the settings card. **Choose the screen again**
  forgets the answer, so the dialog comes back.
- **Clicking** asks for the pointer only (not the keyboard), and only while a widget has **Click and drag on the
  screen** on. Turning that switch on or off starts a new sharing session, which may ask once more.
- While the computer is locked the desktop refuses screen sharing; the widget shows it again after you unlock.

## 3. Add the widget

1. In the layout editor add **Second screen** (Computer). A wide widget on its own page looks best.
2. Turn on **Show the screen on the phone**. Turn on **Click and drag on the screen** if you want to use it by touch.
3. **Monitor**: *Extended screen* picks the first monitor that isn't the main one, which is the virtual monitor from
   step 2. *1, 2, 3, 4* count the monitors from the left. (On Wayland the desktop's sharing dialog decides instead.)
4. Pick **Pictures a second** and **Quality** to match your connection. USB handles *High*. Over Wi-Fi start with
   *Normal*.

## Touch on the screen

| Touch | Does |
|---|---|
| Tap | Click |
| Drag | Move the pointer |
| Hold, then drag | Drag (pick up a window or select text) |
| Two-finger drag | Scroll |
| Two-finger tap | Right-click |

## Privacy

Both switches are off by default. The picture shows everything on that monitor, notifications included. It goes only
to the paired phone, or to a paired browser. Capture runs only while the widget is on screen. Nothing is stored. On
Wayland the desktop asks before anything is shared, and its restore token is kept in `wayland-screen.json` beside
`config.json`. See [PRIVACY.md](../PRIVACY.md).
