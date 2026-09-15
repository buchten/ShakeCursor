// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shake Cursor contributors

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ShakeCursor
{
    internal static class Program
    {
        private const string MutexName = "Local\\ShakeCursor_2B8C588D_81C9_42AF_93F2_D9B94099801F";

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--watchdog")
            {
                Watchdog.Run(args[1]);
                return;
            }

            bool ownsMutex;
            using (Mutex mutex = new Mutex(true, MutexName, out ownsMutex))
            {
                if (!ownsMutex)
                {
                    MessageBox.Show(
                        "Shake Cursor is already running. Use its notification-area icon to open settings.",
                        "Shake Cursor",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                NativeMethods.TryEnablePerMonitorDpiAwareness();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                {
                    SystemCursorController.EmergencyRestore();
                    MessageBox.Show(
                        "Shake Cursor stopped because of an unexpected error.\r\n\r\n" + e.Exception.Message,
                        "Shake Cursor",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    Application.Exit();
                };
                AppDomain.CurrentDomain.UnhandledException += delegate
                {
                    SystemCursorController.EmergencyRestore();
                };

                Watchdog.StartForCurrentProcess();

                try
                {
                    using (ShakeApplicationContext context = new ShakeApplicationContext())
                    {
                        Application.Run(context);
                    }
                }
                catch (Exception ex)
                {
                    SystemCursorController.EmergencyRestore();
                    MessageBox.Show(
                        "Shake Cursor could not start.\r\n\r\n" + ex.Message,
                        "Shake Cursor",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                finally
                {
                    SystemCursorController.EmergencyRestore();
                }
            }
        }
    }

    internal sealed class ShakeApplicationContext : ApplicationContext
    {
        private AppSettings _settings;
        private readonly MotionDetector _detector;
        private readonly MouseHook _mouseHook;
        private readonly ShakeOverlay _overlay;
        private readonly SystemCursorController _cursorController;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly NotifyIcon _notifyIcon;
        private readonly Icon _trayIcon;
        private readonly ToolStripMenuItem _enabledItem;
        private readonly ToolStripMenuItem _startupItem;
        private bool _disposed;

        public ShakeApplicationContext()
        {
            _settings = AppSettings.Load();
            _detector = new MotionDetector(_settings);
            _overlay = new ShakeOverlay();
            _cursorController = new SystemCursorController();

            ContextMenuStrip menu = new ContextMenuStrip();
            _enabledItem = new ToolStripMenuItem("Enabled");
            _enabledItem.Checked = _settings.Enabled;
            _enabledItem.CheckOnClick = true;
            _enabledItem.Click += EnabledClicked;
            menu.Items.Add(_enabledItem);

            ToolStripMenuItem settingsItem = new ToolStripMenuItem("Settings...");
            settingsItem.Click += delegate { ShowSettings(); };
            menu.Items.Add(settingsItem);

            _startupItem = new ToolStripMenuItem("Start with Windows");
            _startupItem.Checked = StartupManager.IsEnabled();
            _startupItem.CheckOnClick = true;
            _startupItem.Click += StartupClicked;
            menu.Items.Add(_startupItem);

            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem aboutItem = new ToolStripMenuItem("About");
            aboutItem.Click += delegate { ShowAbout(); };
            menu.Items.Add(aboutItem);
            ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += delegate { ExitThread(); };
            menu.Items.Add(exitItem);

            _notifyIcon = new NotifyIcon();
            _trayIcon = LoadApplicationIcon();
            _notifyIcon.Icon = _trayIcon;
            _notifyIcon.Text = "Shake Cursor";
            _notifyIcon.ContextMenuStrip = menu;
            _notifyIcon.Visible = true;
            _notifyIcon.DoubleClick += delegate { ShowSettings(); };

            _mouseHook = new MouseHook();
            _mouseHook.MouseMoved += MouseMoved;
            _mouseHook.Install();

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 16;
            _timer.Tick += TimerTick;
            _timer.Start();

        }

        private void MouseMoved(object sender, MouseMovedEventArgs e)
        {
            if (_settings.Enabled)
                _detector.AddPoint(e.Location, e.TimestampMilliseconds);
        }

        private void TimerTick(object sender, EventArgs e)
        {
            if (!_settings.Enabled)
            {
                StopEffect();
                return;
            }

            double now = Clock.NowMilliseconds();
            double energy = _detector.Advance(now);
            if (energy < 0.018)
            {
                StopEffect();
                return;
            }

            Point pointerPosition;
            if (!NativeMethods.GetCursorPos(out pointerPosition))
            {
                StopEffect();
                return;
            }

            if (_settings.HideOriginalCursor)
                _cursorController.HideSystemCursors();
            else
                _cursorController.RestoreSystemCursors();

            IntPtr displayCursor;
            if (!_cursorController.TryGetDisplayCursor(out displayCursor))
            {
                StopEffect();
                return;
            }

            double eased = energy * energy * (3.0 - (2.0 * energy));
            double scale = 1.0 + ((_settings.MaximumScale - 1.0) * eased);
            _overlay.RenderAt(pointerPosition, displayCursor, scale);
        }

        private void EnabledClicked(object sender, EventArgs e)
        {
            _settings.Enabled = _enabledItem.Checked;
            _settings.Save();
            if (!_settings.Enabled)
            {
                _detector.Reset();
                StopEffect();
            }
        }

        private void StartupClicked(object sender, EventArgs e)
        {
            try
            {
                StartupManager.SetEnabled(_startupItem.Checked);
            }
            catch (Exception ex)
            {
                _startupItem.Checked = StartupManager.IsEnabled();
                MessageBox.Show(
                    "The startup setting could not be changed.\r\n\r\n" + ex.Message,
                    "Shake Cursor",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void ShowSettings()
        {
            using (SettingsForm form = new SettingsForm(_settings, StartupManager.IsEnabled()))
            {
                if (form.ShowDialog() != DialogResult.OK)
                    return;

                _settings = form.ResultSettings;
                _settings.Save();
                _enabledItem.Checked = _settings.Enabled;
                _detector.UpdateSettings(_settings);
                _detector.Reset();
                StopEffect();

                try
                {
                    StartupManager.SetEnabled(form.StartWithWindows);
                    _startupItem.Checked = form.StartWithWindows;
                }
                catch (Exception ex)
                {
                    _startupItem.Checked = StartupManager.IsEnabled();
                    MessageBox.Show(
                        "The other settings were saved, but the startup setting could not be changed.\r\n\r\n" + ex.Message,
                        "Shake Cursor",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
        }

        private static void ShowAbout()
        {
            MessageBox.Show(
                "Shake Cursor 1.4.1\r\n\r\n" +
                "A KDE-style shake-to-enlarge pointer locator for Windows 10 and 11.\r\n\r\n" +
                "Mouse coordinates are processed locally and are never recorded or transmitted.\r\n\r\n" +
                "Licensed under GNU GPL v3 or later.",
                "About Shake Cursor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void StopEffect()
        {
            _overlay.Deactivate();
            _cursorController.RestoreSystemCursors();
        }

        protected override void ExitThreadCore()
        {
            StopEffect();
            _notifyIcon.Visible = false;
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                StopEffect();
                _timer.Stop();
                _timer.Dispose();
                _mouseHook.Dispose();
                _overlay.Dispose();
                _cursorController.Dispose();
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _trayIcon.Dispose();
            }

            _disposed = true;
            base.Dispose(disposing);
        }

        private static Icon LoadApplicationIcon()
        {
            try
            {
                Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (icon != null)
                    return icon;
            }
            catch
            {
            }
            return (Icon)SystemIcons.Application.Clone();
        }
    }

    internal sealed class AppSettings
    {
        private const string RegistryPath = @"Software\ShakeCursor";

        public int Sensitivity { get; set; }
        public double MaximumScale { get; set; }
        public int GrowthMilliseconds { get; set; }
        public int HoldMilliseconds { get; set; }
        public int FadeMilliseconds { get; set; }
        public bool HideOriginalCursor { get; set; }
        public bool Enabled { get; set; }

        public static AppSettings Defaults()
        {
            AppSettings settings = new AppSettings();
            settings.Sensitivity = 6;
            settings.MaximumScale = 15.0;
            settings.GrowthMilliseconds = 3000;
            settings.HoldMilliseconds = 2000;
            settings.FadeMilliseconds = 400;
            settings.HideOriginalCursor = true;
            settings.Enabled = true;
            return settings;
        }

        public AppSettings Clone()
        {
            return new AppSettings
            {
                Sensitivity = Sensitivity,
                MaximumScale = MaximumScale,
                GrowthMilliseconds = GrowthMilliseconds,
                HoldMilliseconds = HoldMilliseconds,
                FadeMilliseconds = FadeMilliseconds,
                HideOriginalCursor = HideOriginalCursor,
                Enabled = Enabled
            };
        }

        public static AppSettings Load()
        {
            AppSettings settings = Defaults();
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath, false))
                {
                    if (key == null)
                        return settings;

                    int settingsVersion = ReadInt(key, "SettingsVersion", 1);
                    int maximumScalePercent = ReadInt(key, "MaximumScalePercent", 1500);
                    int growthMilliseconds = ReadInt(key, "GrowthMilliseconds", settings.GrowthMilliseconds);
                    int holdMilliseconds = ReadInt(key, "HoldMilliseconds", settings.HoldMilliseconds);
                    int fadeMilliseconds = ReadInt(key, "FadeMilliseconds", settings.FadeMilliseconds);

                    // Upgrade the original defaults without overwriting values the user customized.
                    if (settingsVersion < 2)
                    {
                        if (maximumScalePercent == 500)
                            maximumScalePercent = 1500;
                        if (growthMilliseconds == 1100)
                            growthMilliseconds = 3000;
                        if (holdMilliseconds == 180)
                            holdMilliseconds = 2000;
                        if (fadeMilliseconds == 280)
                            fadeMilliseconds = 400;
                    }

                    settings.Sensitivity = Clamp(ReadInt(key, "Sensitivity", settings.Sensitivity), 1, 10);
                    settings.MaximumScale = Clamp(maximumScalePercent, 200, 3000) / 100.0;
                    settings.GrowthMilliseconds = Clamp(growthMilliseconds, 300, 10000);
                    settings.HoldMilliseconds = Clamp(holdMilliseconds, 0, 5000);
                    settings.FadeMilliseconds = Clamp(fadeMilliseconds, 100, 2000);
                    settings.HideOriginalCursor = ReadInt(key, "HideOriginalCursor", 1) != 0;
                    settings.Enabled = ReadInt(key, "Enabled", 1) != 0;
                }
            }
            catch
            {
                return Defaults();
            }
            return settings;
        }

        public void Save()
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath))
            {
                if (key == null)
                    return;

                key.SetValue("Sensitivity", Sensitivity, RegistryValueKind.DWord);
                key.SetValue("SettingsVersion", 2, RegistryValueKind.DWord);
                key.SetValue("MaximumScalePercent", (int)Math.Round(MaximumScale * 100.0), RegistryValueKind.DWord);
                key.SetValue("GrowthMilliseconds", GrowthMilliseconds, RegistryValueKind.DWord);
                key.SetValue("HoldMilliseconds", HoldMilliseconds, RegistryValueKind.DWord);
                key.SetValue("FadeMilliseconds", FadeMilliseconds, RegistryValueKind.DWord);
                key.SetValue("HideOriginalCursor", HideOriginalCursor ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("Enabled", Enabled ? 1 : 0, RegistryValueKind.DWord);
            }
        }

        private static int ReadInt(RegistryKey key, string name, int fallback)
        {
            object value = key.GetValue(name, fallback);
            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return fallback;
            }
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }

    internal sealed class MotionDetector
    {
        private sealed class MotionPoint
        {
            public Point Location;
            public double Time;
        }

        private readonly List<MotionPoint> _points = new List<MotionPoint>();
        private AppSettings _settings;
        private double _energy;
        private double _activeUntil;
        private double _lastAdvance;

        public MotionDetector(AppSettings settings)
        {
            UpdateSettings(settings);
            Reset();
        }

        public void UpdateSettings(AppSettings settings)
        {
            _settings = settings.Clone();
        }

        public void AddPoint(Point point, double now)
        {
            Advance(now);

            if (_points.Count > 0)
            {
                Point previous = _points[_points.Count - 1].Location;
                double dx = point.X - previous.X;
                double dy = point.Y - previous.Y;
                if ((dx * dx) + (dy * dy) < 4.0)
                    return;
            }

            _points.Add(new MotionPoint { Location = point, Time = now });
            Trim(now);

            if (IsRapidReversal())
                _activeUntil = now + 125.0;
        }

        public double Advance(double now)
        {
            if (_lastAdvance <= 0.0)
            {
                _lastAdvance = now;
                return _energy;
            }

            double elapsed = Math.Max(0.0, Math.Min(100.0, now - _lastAdvance));
            _lastAdvance = now;

            if (now <= _activeUntil)
            {
                _energy += elapsed / _settings.GrowthMilliseconds;
            }
            else if (now > _activeUntil + _settings.HoldMilliseconds)
            {
                _energy -= elapsed / _settings.FadeMilliseconds;
            }

            _energy = Math.Max(0.0, Math.Min(1.0, _energy));
            Trim(now);
            return _energy;
        }

        public void Reset()
        {
            _points.Clear();
            _energy = 0.0;
            _activeUntil = 0.0;
            _lastAdvance = Clock.NowMilliseconds();
        }

        private void Trim(double now)
        {
            int remove = 0;
            while (remove < _points.Count && now - _points[remove].Time > 360.0)
                remove++;
            if (remove > 0)
                _points.RemoveRange(0, remove);
        }

        private bool IsRapidReversal()
        {
            if (_points.Count < 4)
                return false;

            MotionPoint first = _points[0];
            MotionPoint last = _points[_points.Count - 1];
            double elapsedSeconds = (last.Time - first.Time) / 1000.0;
            if (elapsedSeconds < 0.055)
                return false;

            double totalDistance = 0.0;
            int reversals = 0;
            bool havePreviousDirection = false;
            double previousDirectionX = 0.0;
            double previousDirectionY = 0.0;

            for (int i = 1; i < _points.Count; i++)
            {
                double dx = _points[i].Location.X - _points[i - 1].Location.X;
                double dy = _points[i].Location.Y - _points[i - 1].Location.Y;
                double length = Math.Sqrt((dx * dx) + (dy * dy));
                if (length < 1.5)
                    continue;

                totalDistance += length;
                double directionX = dx / length;
                double directionY = dy / length;
                if (havePreviousDirection)
                {
                    double dot = (directionX * previousDirectionX) + (directionY * previousDirectionY);
                    if (dot < -0.10)
                        reversals++;
                }
                previousDirectionX = directionX;
                previousDirectionY = directionY;
                havePreviousDirection = true;
            }

            double netX = last.Location.X - first.Location.X;
            double netY = last.Location.Y - first.Location.Y;
            double netDistance = Math.Sqrt((netX * netX) + (netY * netY));
            double averageSpeed = totalDistance / elapsedSeconds;
            double speedThreshold = 1900.0 - ((_settings.Sensitivity - 1) * (1300.0 / 9.0));

            return reversals >= 2 &&
                   totalDistance >= 52.0 &&
                   averageSpeed >= speedThreshold &&
                   netDistance <= totalDistance * 0.80;
        }
    }

    internal sealed class MouseMovedEventArgs : EventArgs
    {
        public Point Location { get; private set; }
        public double TimestampMilliseconds { get; private set; }

        public MouseMovedEventArgs(Point location, double timestampMilliseconds)
        {
            Location = location;
            TimestampMilliseconds = timestampMilliseconds;
        }
    }

    internal sealed class MouseHook : IDisposable
    {
        private NativeMethods.LowLevelMouseProc _callback;
        private IntPtr _hook;

        public event EventHandler<MouseMovedEventArgs> MouseMoved;

        public void Install()
        {
            if (_hook != IntPtr.Zero)
                return;

            _callback = HookCallback;
            using (Process process = Process.GetCurrentProcess())
            using (ProcessModule module = process.MainModule)
            {
                IntPtr moduleHandle = NativeMethods.GetModuleHandle(module.ModuleName);
                _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _callback, moduleHandle, 0);
            }

            if (_hook == IntPtr.Zero)
                throw new InvalidOperationException("The global mouse hook could not be installed.");
        }

        private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && message.ToInt32() == NativeMethods.WM_MOUSEMOVE)
            {
                NativeMethods.MSLLHOOKSTRUCT mouse = (NativeMethods.MSLLHOOKSTRUCT)Marshal.PtrToStructure(
                    data,
                    typeof(NativeMethods.MSLLHOOKSTRUCT));
                EventHandler<MouseMovedEventArgs> handler = MouseMoved;
                if (handler != null)
                    handler(this, new MouseMovedEventArgs(mouse.pt, Clock.NowMilliseconds()));
            }

            return NativeMethods.CallNextHookEx(_hook, code, message, data);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
            _callback = null;
        }
    }

    internal sealed class ShakeOverlay : Form
    {
        private IntPtr _cursor = IntPtr.Zero;
        private IntPtr _lastSourceCursor = IntPtr.Zero;
        private IntPtr _memoryDeviceContext = IntPtr.Zero;
        private IntPtr _stockBitmap = IntPtr.Zero;
        private IntPtr _layerBitmap = IntPtr.Zero;
        private int _layerWidth;
        private int _layerHeight;
        private bool _isShown;
        private int _cursorWidth = 32;
        private int _cursorHeight = 32;
        private int _hotspotX;
        private int _hotspotY;

        public ShakeOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= NativeMethods.WS_EX_TOPMOST |
                                      NativeMethods.WS_EX_LAYERED |
                                      NativeMethods.WS_EX_TRANSPARENT |
                                      NativeMethods.WS_EX_TOOLWINDOW |
                                      NativeMethods.WS_EX_NOACTIVATE;
                return parameters;
            }
        }

        public void RenderAt(Point pointerPosition, IntPtr sourceCursor, double scale)
        {
            if (sourceCursor == IntPtr.Zero)
                return;

            if (_cursor == IntPtr.Zero || sourceCursor != _lastSourceCursor)
                ReplaceCursor(sourceCursor);
            if (_cursor == IntPtr.Zero)
                return;

            int width = Math.Max(1, (int)Math.Round(_cursorWidth * scale));
            int height = Math.Max(1, (int)Math.Round(_cursorHeight * scale));
            int left = pointerPosition.X - (int)Math.Round(_hotspotX * scale);
            int top = pointerPosition.Y - (int)Math.Round(_hotspotY * scale);

            if (!EnsureLayerBitmap(width, height))
                return;

            Point destination = new Point(left, top);
            Point source = new Point(0, 0);
            NativeMethods.SIZE size = new NativeMethods.SIZE(width, height);
            NativeMethods.BLENDFUNCTION blend = new NativeMethods.BLENDFUNCTION();
            blend.BlendOp = NativeMethods.AC_SRC_OVER;
            blend.BlendFlags = 0;
            blend.SourceConstantAlpha = 255;
            blend.AlphaFormat = NativeMethods.AC_SRC_ALPHA;

            IntPtr screenDeviceContext = NativeMethods.GetDC(IntPtr.Zero);
            bool updated;
            try
            {
                updated = NativeMethods.UpdateLayeredWindow(
                    Handle,
                    screenDeviceContext,
                    ref destination,
                    ref size,
                    _memoryDeviceContext,
                    ref source,
                    0,
                    ref blend,
                    NativeMethods.ULW_ALPHA);
            }
            finally
            {
                if (screenDeviceContext != IntPtr.Zero)
                    NativeMethods.ReleaseDC(IntPtr.Zero, screenDeviceContext);
            }

            if (updated)
            {
                if (!_isShown)
                {
                    NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
                    _isShown = true;
                }

                // The Windows taskbar is itself a topmost window. Reassert this
                // overlay's existing topmost status after each atomic redraw so a
                // very large cursor is not occasionally placed behind the taskbar.
                NativeMethods.SetWindowPos(
                    Handle,
                    NativeMethods.HWND_TOPMOST,
                    0,
                    0,
                    0,
                    0,
                    NativeMethods.SWP_NOMOVE |
                    NativeMethods.SWP_NOSIZE |
                    NativeMethods.SWP_NOACTIVATE |
                    NativeMethods.SWP_NOOWNERZORDER);
            }
        }

        private void ReplaceCursor(IntPtr sourceCursor)
        {
            IntPtr copy = NativeMethods.CopyIcon(sourceCursor);
            if (copy == IntPtr.Zero)
                return;

            if (_cursor != IntPtr.Zero)
                NativeMethods.DestroyIcon(_cursor);

            _cursor = copy;
            _lastSourceCursor = sourceCursor;
            CursorMetrics metrics = CursorMetrics.Read(copy);
            _cursorWidth = metrics.Width;
            _cursorHeight = metrics.Height;
            _hotspotX = metrics.HotspotX;
            _hotspotY = metrics.HotspotY;
            ReleaseLayerBitmap();
        }

        private bool EnsureLayerBitmap(int width, int height)
        {
            if (_layerBitmap != IntPtr.Zero && width == _layerWidth && height == _layerHeight)
                return true;

            using (Bitmap layer = CreateNativeRenderedLayer(width, height))
            {
                IntPtr newBitmap = layer.GetHbitmap(Color.FromArgb(0));
                if (newBitmap == IntPtr.Zero)
                    return false;

                if (_memoryDeviceContext == IntPtr.Zero)
                {
                    IntPtr screenDeviceContext = NativeMethods.GetDC(IntPtr.Zero);
                    _memoryDeviceContext = NativeMethods.CreateCompatibleDC(screenDeviceContext);
                    if (screenDeviceContext != IntPtr.Zero)
                        NativeMethods.ReleaseDC(IntPtr.Zero, screenDeviceContext);

                    if (_memoryDeviceContext == IntPtr.Zero)
                    {
                        NativeMethods.DeleteObject(newBitmap);
                        return false;
                    }
                }

                IntPtr previouslySelected = NativeMethods.SelectObject(_memoryDeviceContext, newBitmap);
                if (_stockBitmap == IntPtr.Zero)
                    _stockBitmap = previouslySelected;

                if (_layerBitmap != IntPtr.Zero)
                    NativeMethods.DeleteObject(_layerBitmap);

                _layerBitmap = newBitmap;
                _layerWidth = width;
                _layerHeight = height;
                return true;
            }
        }

        private Bitmap CreateNativeRenderedLayer(int width, int height)
        {
            Rectangle bounds = new Rectangle(0, 0, width, height);
            using (Bitmap onBlack = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            using (Bitmap onWhite = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                DrawCursorOnBackground(onBlack, Color.Black, width, height);
                DrawCursorOnBackground(onWhite, Color.White, width, height);

                Bitmap result = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
                BitmapData blackData = null;
                BitmapData whiteData = null;
                BitmapData resultData = null;
                bool completed = false;
                try
                {
                    blackData = onBlack.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    whiteData = onWhite.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    resultData = result.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);

                    byte[] blackPixels = new byte[Math.Abs(blackData.Stride) * height];
                    byte[] whitePixels = new byte[Math.Abs(whiteData.Stride) * height];
                    byte[] resultPixels = new byte[Math.Abs(resultData.Stride) * height];
                    Marshal.Copy(blackData.Scan0, blackPixels, 0, blackPixels.Length);
                    Marshal.Copy(whiteData.Scan0, whitePixels, 0, whitePixels.Length);

                    for (int y = 0; y < height; y++)
                    {
                        int blackRow = blackData.Stride >= 0 ? y * blackData.Stride : (height - 1 - y) * -blackData.Stride;
                        int whiteRow = whiteData.Stride >= 0 ? y * whiteData.Stride : (height - 1 - y) * -whiteData.Stride;
                        int resultRow = resultData.Stride >= 0 ? y * resultData.Stride : (height - 1 - y) * -resultData.Stride;

                        for (int x = 0; x < width; x++)
                        {
                            int blackIndex = blackRow + (x * 4);
                            int whiteIndex = whiteRow + (x * 4);
                            int resultIndex = resultRow + (x * 4);

                            int blueDifference = whitePixels[whiteIndex] - blackPixels[blackIndex];
                            int greenDifference = whitePixels[whiteIndex + 1] - blackPixels[blackIndex + 1];
                            int redDifference = whitePixels[whiteIndex + 2] - blackPixels[blackIndex + 2];
                            int transparency = Math.Max(blueDifference, Math.Max(greenDifference, redDifference));
                            int alpha = Math.Max(0, Math.Min(255, 255 - transparency));

                            resultPixels[resultIndex] = (byte)Math.Min(alpha, blackPixels[blackIndex]);
                            resultPixels[resultIndex + 1] = (byte)Math.Min(alpha, blackPixels[blackIndex + 1]);
                            resultPixels[resultIndex + 2] = (byte)Math.Min(alpha, blackPixels[blackIndex + 2]);
                            resultPixels[resultIndex + 3] = (byte)alpha;
                        }
                    }

                    Marshal.Copy(resultPixels, 0, resultData.Scan0, resultPixels.Length);
                    completed = true;
                }
                finally
                {
                    if (blackData != null)
                        onBlack.UnlockBits(blackData);
                    if (whiteData != null)
                        onWhite.UnlockBits(whiteData);
                    if (resultData != null)
                        result.UnlockBits(resultData);
                    if (!completed)
                        result.Dispose();
                }
                return result;
            }
        }

        private void DrawCursorOnBackground(Bitmap target, Color background, int width, int height)
        {
            using (Graphics graphics = Graphics.FromImage(target))
            {
                graphics.Clear(background);
                IntPtr deviceContext = graphics.GetHdc();
                try
                {
                    NativeMethods.DrawIconEx(
                        deviceContext,
                        0,
                        0,
                        _cursor,
                        width,
                        height,
                        0,
                        IntPtr.Zero,
                        NativeMethods.DI_NORMAL);
                }
                finally
                {
                    graphics.ReleaseHdc(deviceContext);
                }
            }
        }

        public void Deactivate()
        {
            if (_isShown && IsHandleCreated)
            {
                NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);
                _isShown = false;
            }

            if (_cursor != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(_cursor);
                _cursor = IntPtr.Zero;
                _lastSourceCursor = IntPtr.Zero;
            }
            ReleaseLayerBitmap();
        }

        private void ReleaseLayerBitmap()
        {
            if (_layerBitmap != IntPtr.Zero)
            {
                if (_memoryDeviceContext != IntPtr.Zero && _stockBitmap != IntPtr.Zero)
                    NativeMethods.SelectObject(_memoryDeviceContext, _stockBitmap);
                NativeMethods.DeleteObject(_layerBitmap);
                _layerBitmap = IntPtr.Zero;
                _layerWidth = 0;
                _layerHeight = 0;
            }
        }

        protected override void Dispose(bool disposing)
        {
            ReleaseLayerBitmap();
            if (_memoryDeviceContext != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(_memoryDeviceContext);
                _memoryDeviceContext = IntPtr.Zero;
                _stockBitmap = IntPtr.Zero;
            }
            if (_cursor != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(_cursor);
                _cursor = IntPtr.Zero;
            }
            base.Dispose(disposing);
        }
    }

    internal struct CursorMetrics
    {
        public int Width;
        public int Height;
        public int HotspotX;
        public int HotspotY;

        public static CursorMetrics Read(IntPtr cursor)
        {
            CursorMetrics result = new CursorMetrics();
            result.Width = Math.Max(1, SystemInformation.CursorSize.Width);
            result.Height = Math.Max(1, SystemInformation.CursorSize.Height);

            NativeMethods.ICONINFO info;
            if (!NativeMethods.GetIconInfo(cursor, out info))
                return result;

            try
            {
                result.HotspotX = (int)info.xHotspot;
                result.HotspotY = (int)info.yHotspot;

                NativeMethods.BITMAP bitmap;
                if (info.hbmColor != IntPtr.Zero &&
                    NativeMethods.GetObject(info.hbmColor, Marshal.SizeOf(typeof(NativeMethods.BITMAP)), out bitmap) != 0)
                {
                    result.Width = Math.Abs(bitmap.bmWidth);
                    result.Height = Math.Abs(bitmap.bmHeight);
                }
                else if (info.hbmMask != IntPtr.Zero &&
                         NativeMethods.GetObject(info.hbmMask, Marshal.SizeOf(typeof(NativeMethods.BITMAP)), out bitmap) != 0)
                {
                    result.Width = Math.Abs(bitmap.bmWidth);
                    result.Height = Math.Max(1, Math.Abs(bitmap.bmHeight) / 2);
                }
            }
            finally
            {
                if (info.hbmColor != IntPtr.Zero)
                    NativeMethods.DeleteObject(info.hbmColor);
                if (info.hbmMask != IntPtr.Zero)
                    NativeMethods.DeleteObject(info.hbmMask);
            }
            return result;
        }
    }

    internal sealed class SystemCursorController : IDisposable
    {
        private sealed class CursorEntry
        {
            public uint Id;
            public IntPtr Original;
            public bool Replaced;
        }

        private static readonly uint[] CursorIds =
        {
            32512, 32513, 32514, 32515, 32516,
            32642, 32643, 32644, 32645, 32646,
            32648, 32649, 32650, 32651
        };

        private readonly List<CursorEntry> _entries = new List<CursorEntry>();
        private readonly Dictionary<IntPtr, IntPtr> _replacementToOriginal = new Dictionary<IntPtr, IntPtr>();
        private bool _hidden;

        public bool HideSystemCursors()
        {
            if (_hidden)
                return true;

            ClearCopies();
            foreach (uint id in CursorIds)
            {
                IntPtr shared = NativeMethods.LoadCursor(IntPtr.Zero, new IntPtr(id));
                if (shared == IntPtr.Zero)
                    continue;

                IntPtr original = NativeMethods.CopyIcon(shared);
                if (original != IntPtr.Zero)
                    _entries.Add(new CursorEntry { Id = id, Original = original, Replaced = false });
            }

            int replacedCount = 0;
            foreach (CursorEntry entry in _entries)
            {
                IntPtr blank = CreateTransparentCursor();
                if (blank == IntPtr.Zero)
                    continue;

                if (NativeMethods.SetSystemCursor(blank, entry.Id))
                {
                    entry.Replaced = true;
                    replacedCount++;
                }
                else
                {
                    NativeMethods.DestroyCursor(blank);
                }
            }

            if (replacedCount == 0)
            {
                EmergencyRestore();
                ClearCopies();
                return false;
            }

            foreach (CursorEntry entry in _entries)
            {
                if (!entry.Replaced)
                    continue;
                IntPtr replacement = NativeMethods.LoadCursor(IntPtr.Zero, new IntPtr(entry.Id));
                if (replacement != IntPtr.Zero)
                    _replacementToOriginal[replacement] = entry.Original;
            }

            _hidden = true;
            return true;
        }

        public bool TryGetDisplayCursor(out IntPtr cursor)
        {
            cursor = IntPtr.Zero;
            NativeMethods.CURSORINFO info = new NativeMethods.CURSORINFO();
            info.cbSize = Marshal.SizeOf(typeof(NativeMethods.CURSORINFO));
            if (!NativeMethods.GetCursorInfo(ref info) || (info.flags & NativeMethods.CURSOR_SHOWING) == 0)
                return false;

            IntPtr mapped;
            if (_hidden && _replacementToOriginal.TryGetValue(info.hCursor, out mapped))
                cursor = mapped;
            else
                cursor = info.hCursor;
            return cursor != IntPtr.Zero;
        }

        public void RestoreSystemCursors()
        {
            if (!_hidden && _entries.Count == 0)
                return;

            EmergencyRestore();
            _hidden = false;
            ClearCopies();
        }

        private static IntPtr CreateTransparentCursor()
        {
            int width = Math.Max(32, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXCURSOR));
            int height = Math.Max(32, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYCURSOR));
            int bytesPerRow = ((width + 15) / 16) * 2;
            byte[] andMask = new byte[bytesPerRow * height];
            byte[] xorMask = new byte[bytesPerRow * height];
            for (int i = 0; i < andMask.Length; i++)
                andMask[i] = 0xFF;
            return NativeMethods.CreateCursor(IntPtr.Zero, 0, 0, width, height, andMask, xorMask);
        }

        private void ClearCopies()
        {
            foreach (CursorEntry entry in _entries)
            {
                if (entry.Original != IntPtr.Zero)
                    NativeMethods.DestroyIcon(entry.Original);
            }
            _entries.Clear();
            _replacementToOriginal.Clear();
        }

        public static void EmergencyRestore()
        {
            NativeMethods.SystemParametersInfo(
                NativeMethods.SPI_SETCURSORS,
                0,
                IntPtr.Zero,
                NativeMethods.SPIF_SENDCHANGE);
        }

        public void Dispose()
        {
            RestoreSystemCursors();
        }
    }

    internal static class StartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "ShakeCursor";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                    return key != null && key.GetValue(ValueName) != null;
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key == null)
                    throw new InvalidOperationException("The current user's startup registry key is unavailable.");

                if (enabled)
                    key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
                else
                    key.DeleteValue(ValueName, false);
            }
        }
    }

    internal sealed class SettingsForm : Form
    {
        private readonly NumericUpDown _sensitivity;
        private readonly NumericUpDown _maximumScale;
        private readonly NumericUpDown _growth;
        private readonly NumericUpDown _hold;
        private readonly NumericUpDown _fade;
        private readonly CheckBox _hideOriginal;
        private readonly CheckBox _enabled;
        private readonly CheckBox _startWithWindows;

        public AppSettings ResultSettings { get; private set; }
        public bool StartWithWindows { get; private set; }

        public SettingsForm(AppSettings current, bool startWithWindows)
        {
            Text = "Shake Cursor settings";
            ClientSize = new Size(620, 465);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;

            Label heading = AddLabel("Shake detection and pointer growth", 20, 18, 440, 25);
            heading.Font = new Font(Font, FontStyle.Bold);

            _sensitivity = AddNumber(215, 57, 1, 10, 1, 0);
            AddSettingRow("Sensitivity", "1 requires a harder shake; 10 triggers most easily.", 57);

            _maximumScale = AddNumber(215, 103, 2, 30, 0.5M, 1);
            AddSettingRow("Maximum size", "Multiplier relative to the current system cursor.", 103);

            _growth = AddNumber(215, 149, 300, 10000, 100, 0);
            AddSettingRow("Time to maximum (ms)", "A longer shake keeps growing until this time is reached.", 149);

            _hold = AddNumber(215, 195, 0, 5000, 50, 0);
            AddSettingRow("Hold after shaking (ms)", "How long the enlarged pointer stays at its current size.", 195);

            _fade = AddNumber(215, 241, 100, 2000, 50, 0);
            AddSettingRow("Shrink time (ms)", "How quickly the pointer returns to normal size.", 241);

            _hideOriginal = new CheckBox();
            _hideOriginal.Text = "Hide the normal-size system pointer while enlarged";
            _hideOriginal.SetBounds(20, 294, 570, 24);
            Controls.Add(_hideOriginal);

            Label safety = AddLabel(
                "This gives a seamless result. A watchdog restores the normal cursor scheme if the app stops unexpectedly.",
                40,
                320,
                555,
                34);
            safety.ForeColor = SystemColors.GrayText;

            _enabled = new CheckBox();
            _enabled.Text = "Enable shake detection";
            _enabled.SetBounds(20, 360, 250, 24);
            Controls.Add(_enabled);

            _startWithWindows = new CheckBox();
            _startWithWindows.Text = "Start with Windows";
            _startWithWindows.SetBounds(300, 360, 250, 24);
            Controls.Add(_startWithWindows);

            Button ok = new Button();
            ok.Text = "OK";
            ok.SetBounds(514, 415, 86, 30);
            ok.Click += OkClicked;
            Controls.Add(ok);
            AcceptButton = ok;

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(418, 415, 86, 30);
            Controls.Add(cancel);
            CancelButton = cancel;

            Button defaults = new Button();
            defaults.Text = "Defaults";
            defaults.SetBounds(20, 415, 90, 30);
            defaults.Click += delegate { LoadIntoControls(AppSettings.Defaults()); };
            Controls.Add(defaults);

            LoadIntoControls(current);
            _startWithWindows.Checked = startWithWindows;
        }

        private void AddSettingRow(string title, string explanation, int y)
        {
            AddLabel(title, 20, y + 3, 190, 22);
            Label hint = AddLabel(explanation, 345, y - 1, 255, 39);
            hint.ForeColor = SystemColors.GrayText;
        }

        private Label AddLabel(string text, int x, int y, int width, int height)
        {
            Label label = new Label();
            label.Text = text;
            label.SetBounds(x, y, width, height);
            Controls.Add(label);
            return label;
        }

        private NumericUpDown AddNumber(int x, int y, decimal minimum, decimal maximum, decimal increment, int decimals)
        {
            NumericUpDown number = new NumericUpDown();
            number.SetBounds(x, y, 110, 26);
            number.Minimum = minimum;
            number.Maximum = maximum;
            number.Increment = increment;
            number.DecimalPlaces = decimals;
            Controls.Add(number);
            return number;
        }

        private void LoadIntoControls(AppSettings settings)
        {
            _sensitivity.Value = settings.Sensitivity;
            _maximumScale.Value = (decimal)settings.MaximumScale;
            _growth.Value = settings.GrowthMilliseconds;
            _hold.Value = settings.HoldMilliseconds;
            _fade.Value = settings.FadeMilliseconds;
            _hideOriginal.Checked = settings.HideOriginalCursor;
            _enabled.Checked = settings.Enabled;
        }

        private void OkClicked(object sender, EventArgs e)
        {
            ResultSettings = new AppSettings
            {
                Sensitivity = (int)_sensitivity.Value,
                MaximumScale = (double)_maximumScale.Value,
                GrowthMilliseconds = (int)_growth.Value,
                HoldMilliseconds = (int)_hold.Value,
                FadeMilliseconds = (int)_fade.Value,
                HideOriginalCursor = _hideOriginal.Checked,
                Enabled = _enabled.Checked
            };
            StartWithWindows = _startWithWindows.Checked;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    internal static class Watchdog
    {
        public static void StartForCurrentProcess()
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = Application.ExecutablePath;
                info.Arguments = "--watchdog " + Process.GetCurrentProcess().Id;
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(info);
            }
            catch
            {
                // The main process still restores cursors during normal shutdown.
            }
        }

        public static void Run(string processIdText)
        {
            int processId;
            if (!int.TryParse(processIdText, out processId))
                return;

            IntPtr process = NativeMethods.OpenProcess(NativeMethods.SYNCHRONIZE, false, processId);
            if (process != IntPtr.Zero)
            {
                NativeMethods.WaitForSingleObject(process, NativeMethods.INFINITE);
                NativeMethods.CloseHandle(process);
            }
            SystemCursorController.EmergencyRestore();
        }
    }

    internal static class Clock
    {
        public static double NowMilliseconds()
        {
            return Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
        }
    }

    internal static class NativeMethods
    {
        public const int WH_MOUSE_LL = 14;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int SW_HIDE = 0;
        public const int SW_SHOWNOACTIVATE = 4;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_NOOWNERZORDER = 0x0200;
        public const byte AC_SRC_OVER = 0;
        public const byte AC_SRC_ALPHA = 1;
        public const uint ULW_ALPHA = 0x00000002;
        public const uint DI_NORMAL = 0x0003;
        public const int SM_CXCURSOR = 13;
        public const int SM_CYCURSOR = 14;
        public const int CURSOR_SHOWING = 0x00000001;
        public const uint SPI_SETCURSORS = 0x0057;
        public const uint SPIF_SENDCHANGE = 0x0002;
        public const uint SYNCHRONIZE = 0x00100000;
        public const uint INFINITE = 0xFFFFFFFF;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public Point pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public Point ptScreenPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ICONINFO
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool fIcon;
            public uint xHotspot;
            public uint yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAP
        {
            public int bmType;
            public int bmWidth;
            public int bmHeight;
            public int bmWidthBytes;
            public ushort bmPlanes;
            public ushort bmBitsPixel;
            public IntPtr bmBits;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int cx;
            public int cy;

            public SIZE(int width, int height)
            {
                cx = width;
                cy = height;
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int hookId, LowLevelMouseProc callback, IntPtr module, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorInfo(ref CURSORINFO info);

        [DllImport("user32.dll")]
        public static extern IntPtr CopyIcon(IntPtr icon);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyCursor(IntPtr cursor);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);

        [DllImport("gdi32.dll")]
        public static extern int GetObject(IntPtr handle, int bytes, out BITMAP bitmap);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr handle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DrawIconEx(
            IntPtr deviceContext,
            int x,
            int y,
            IntPtr icon,
            int width,
            int height,
            uint animationStep,
            IntPtr flickerFreeBrush,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateLayeredWindow(
            IntPtr window,
            IntPtr destinationDeviceContext,
            ref Point destination,
            ref SIZE size,
            IntPtr sourceDeviceContext,
            ref Point source,
            int colorKey,
            ref BLENDFUNCTION blend,
            uint flags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(IntPtr deviceContext);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);

        [DllImport("user32.dll")]
        public static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetSystemCursor(IntPtr cursor, uint id);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr CreateCursor(
            IntPtr instance,
            int hotspotX,
            int hotspotY,
            int width,
            int height,
            byte[] andMask,
            byte[] xorMask);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SystemParametersInfo(uint action, uint parameter, IntPtr data, uint updateFlags);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll", EntryPoint = "SetProcessDpiAwarenessContext")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessDPIAware();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll")]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        public static void TryEnablePerMonitorDpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(new IntPtr(-4)))
                    return;
            }
            catch (EntryPointNotFoundException)
            {
            }

            try
            {
                SetProcessDPIAware();
            }
            catch
            {
            }
        }
    }
}
