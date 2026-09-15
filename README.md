# Shake Cursor for Windows

**Current version: 1.4.1**

Shake Cursor reproduces KDE Plasma's pointer-locator effect on Windows 10 and 11:

- Shake the mouse rapidly from side to side to enlarge the pointer.
- Activation requires repeated direction reversals, reducing accidental triggers during ordinary pointer movement.
- Keep shaking and it continues to grow, up to a configurable maximum.
- Stop shaking and it holds briefly, then shrinks smoothly.
- Sensitivity, growth time, maximum size, hold time, and shrink time are configurable.
- It supports the current Windows cursor theme and multi-monitor layouts.
- It runs in the notification area and can start automatically with Windows.
- The executable and notification-area icon use the included custom Shake Cursor artwork.
- The enlarged pointer uses Windows' native cursor renderer inside an atomic per-pixel-alpha overlay. This preserves the original cursor theme, color, and sharp edge style without rectangular background flashes.

## Install

1. Extract the ZIP to a normal local folder.
2. Double-click `Install.cmd`.
3. Shake the mouse rapidly from side to side.
4. Right-click the **Shake Cursor** notification-area icon to open **Settings**.

The installer compiles the source locally with the .NET Framework compiler included with most Windows 10/11 installations, copies the resulting application to `%LOCALAPPDATA%\ShakeCursor`, and starts it. No administrator permission is required.

If Windows reports that the compiler is missing, install Microsoft's **.NET Framework 4.8 Developer Pack**, then run `Install.cmd` again.

## Portable use

Run `Build.cmd`, then launch `ShakeCursor.exe` from the extracted folder. Nothing is installed unless you run `Install.cmd`.

## Settings

- **Sensitivity:** 1 requires a harder shake; 10 triggers most easily.
- **Maximum size:** 2–30 times the current system cursor size. The default is 15×.
- **Time to maximum:** how long continuous shaking takes to reach maximum size. The default is 3 seconds.
- **Hold after shaking:** delay before shrinking begins. The default is 2 seconds.
- **Shrink time:** how quickly the pointer returns to normal.
- **Hide the normal-size system pointer:** gives the cleanest result. While the effect is active, the application temporarily substitutes transparent system cursors and draws the enlarged cursor in a click-through overlay.

The separate watchdog process restores the normal Windows cursor scheme if the main application crashes or is forcibly stopped. Starting Shake Cursor also repairs a cursor scheme left altered by an abnormal shutdown.

## Privacy and permissions

The application uses a low-level mouse hook only to read pointer coordinates and timing. It does not read clicks or keystrokes, store activity, use the network, or require administrator access.

## Limitations

- Applications that draw their own cursor may show both their cursor and the enlarged overlay.
- Exclusive full-screen games can appear above ordinary desktop overlays.
- The executable is compiled locally and is not code-signed, so Windows may show a reputation warning.

## Remove

1. Right-click the notification-area icon, clear **Start with Windows**, and choose **Exit**.
2. Delete `%LOCALAPPDATA%\ShakeCursor`.

Saved preferences are under `HKEY_CURRENT_USER\Software\ShakeCursor` and may be deleted separately if desired.

## License

Shake Cursor is free software licensed under the **GNU General Public License v3.0 or later**. See `LICENSE` for the complete license text.
