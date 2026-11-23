# Phone as a second screen

The **Second screen** widget shows one of the computer's monitors live on the phone. Point it at an *extra, virtual*
monitor and the phone becomes an extended screen: drag a window onto it on the computer, then watch it and tap it on
the phone, while your real monitors stay free for other work. Pair it with the **Keyboard** and **Trackpad** widgets
to type and point on that screen.

What it is and what it isn't:

- It is a picture of the monitor, a few frames a second (1 to 15, 8 by default), encoded as JPEG by ffmpeg. It suits
  chat, a terminal, logs, a dashboard, documentation or a music player. It doesn't suit video or games.
- It works on **Windows** and on **Linux with X11**. Wayland and macOS can't be captured this way yet, and the widget
  says so.
- Sidelight doesn't install a display driver. The virtual monitor comes from one of the steps below.

## 1. Install ffmpeg

The cameras use the same ffmpeg. Install it so it is on `PATH` (`winget install ffmpeg`, `sudo apt install ffmpeg`), or
set its full path in **Settings › Sources › Cameras › ffmpeg**.

## 2. Add a virtual monitor

### Windows

Windows creates an extra monitor only through a display driver. Install an *indirect display driver* (IDD), for
example the open-source **Virtual Display Driver** (`github.com/VirtualDrivers/Virtual-Display-Driver`). Then:

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

## 3. Add the widget

1. In the layout editor add **Second screen** (Computer). A wide widget on its own page looks best.
2. Turn on **Show the screen on the phone**. Turn on **Click and drag on the screen** if you want to use it by touch.
3. **Monitor**: *Extended screen* picks the first monitor that isn't the main one, which is the virtual monitor from
   step 2. *1, 2, 3, 4* count the monitors from the left.
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
to the paired phone, or to a paired browser. Capture runs only while the widget is on screen. Nothing is stored. See
[PRIVACY.md](../PRIVACY.md).
