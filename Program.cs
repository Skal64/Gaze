using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

class Program
{
    private const int WH_MOUSE_LL = 14;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private static IntPtr _mouseHookID = IntPtr.Zero;
    private static IntPtr _keyboardHookID = IntPtr.Zero;
    private static LowLevelMouseProc _mouseProc = MouseHookCallback;
    private static LowLevelKeyboardProc _keyboardProc = KeyboardHookCallback;

    // Configurable Settings with Defaults
    public const float DefaultMaxZoom = 4.0f;
    public const float DefaultZoomSpeed = 0.35f;
    public const float DefaultZoomMultiplier = 1.25f;
    public const Keys DefaultHotkey = Keys.Menu; // Default is Alt (Left/Right Menu)

    public static float MaxZoom = DefaultMaxZoom;
    public static float ZoomSpeed = DefaultZoomSpeed;
    public static float ZoomMultiplier = DefaultZoomMultiplier;
    public static Keys CurrentHotkey = DefaultHotkey;

    // Zoom state
    private static float _zoomFactor = 1.0f;
    private static float _targetZoom = 1.0f;
    const float MIN_ZOOM = 1.0f;

    private static System.Windows.Forms.Timer _animTimer = null!;
    private static int _screenWidth;
    private static int _screenHeight;
    private static NotifyIcon _trayIcon = null!;

    [STAThread]
    static void Main(string[] args)
    {
        if (!MagInitialize())
        {
            MessageBox.Show("Failed to initialize Windows Magnification API.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _screenWidth = GetSystemMetrics(0);
        _screenHeight = GetSystemMetrics(1);

        // Setup System Tray Icon and Menu
        _trayIcon = new NotifyIcon();

        // Safely load custom system tray icon from running directory
        string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
        if (File.Exists(iconPath))
        {
            _trayIcon.Icon = new Icon(iconPath);
        }
        else
        {
            _trayIcon.Icon = SystemIcons.Application; // Fallback if missing
        }

        _trayIcon.Text = "Gaze Zoom Engine";
        _trayIcon.Visible = true;

        ContextMenuStrip contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Settings...", null, (s, e) => OpenSettingsWindow());
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, (s, e) => {
            _trayIcon.Visible = false;
            Application.Exit();
        });
        _trayIcon.ContextMenuStrip = contextMenu;
        _trayIcon.DoubleClick += (s, e) => OpenSettingsWindow();

        // Animation Timer (0% CPU when idle)
        _animTimer = new System.Windows.Forms.Timer();
        _animTimer.Interval = 15;
        _animTimer.Tick += AnimationTick;

        _mouseHookID = SetMouseHook(_mouseProc);
        _keyboardHookID = SetKeyboardHook(_keyboardProc);

        Application.Run();

        // Cleanup
        _animTimer.Stop();
        _trayIcon.Dispose();
        MagSetFullscreenTransform(1.0f, 0, 0);
        UnhookWindowsHookEx(_mouseHookID);
        UnhookWindowsHookEx(_keyboardHookID);
        MagUninitialize();
    }

    private static void OpenSettingsWindow()
    {
        Form settingsForm = new Form
        {
            Text = "Gaze Zoom Preferences",
            Width = 440,
            Height = 590,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen,
            BackColor = Color.FromArgb(245, 245, 247),
            ForeColor = Color.FromArgb(30, 30, 35)
        };

        // Title
        Label lblTitle = new Label
        {
            Text = "Gaze Zoom Settings",
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            Location = new Point(24, 20),
            AutoSize = true
        };
        settingsForm.Controls.Add(lblTitle);

        int currentY = 70;

        // --- Helper to create structured slider sections ---
        Action<string, string, int, int, int, Action<TrackBar, Label>> addSliderSection = (labelTitle, initialValueStr, min, max, val, onScroll) =>
        {
            Label lblHeader = new Label
            {
                Text = labelTitle,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Location = new Point(24, currentY),
                AutoSize = true
            };

            Label lblVal = new Label
            {
                Text = initialValueStr,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
                ForeColor = Color.FromArgb(100, 100, 110),
                Location = new Point(320, currentY),
                TextAlign = ContentAlignment.TopRight,
                Width = 80
            };

            currentY += 25;

            TrackBar tb = new TrackBar
            {
                Minimum = min,
                Maximum = max,
                Value = val,
                Location = new Point(20, currentY),
                Width = 380,
                TickStyle = TickStyle.None
            };

            tb.Scroll += (s, e) => onScroll(tb, lblVal);

            settingsForm.Controls.Add(lblHeader);
            settingsForm.Controls.Add(lblVal);
            settingsForm.Controls.Add(tb);

            currentY += 50;
        };

        // Max Zoom Slider
        addSliderSection("Max Zoom Level", $"{MaxZoom:0.0}x", 2, 10, (int)(MaxZoom * 2), (tb, lbl) => {
            lbl.Text = $"{tb.Value / 2.0f:0.0}x";
        });

        // Zoom Speed Slider
        addSliderSection("Zoom Smoothing Speed", $"{ZoomSpeed:0.00}", 5, 90, (int)(ZoomSpeed * 100), (tb, lbl) => {
            lbl.Text = $"{tb.Value / 100.0f:0.00}";
        });

        // Zoom Multiplier Slider
        addSliderSection("Scroll Step Multiplier", $"{ZoomMultiplier:0.00}x", 10, 20, (int)(ZoomMultiplier * 10), (tb, lbl) => {
            lbl.Text = $"{tb.Value / 10.0f:0.00}x";
        });

        // Hotkey Section
        Label lblHotkeyHeader = new Label
        {
            Text = "Activation Hotkey",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Location = new Point(24, currentY),
            AutoSize = true
        };
        settingsForm.Controls.Add(lblHotkeyHeader);

        currentY += 25;

        Button btnHotkey = new Button
        {
            Text = CurrentHotkey.ToString(),
            Location = new Point(24, currentY),
            Width = 140,
            Height = 32,
            BackColor = Color.FromArgb(230, 230, 235),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        btnHotkey.FlatAppearance.BorderSize = 0;
        
        Keys tempKey = CurrentHotkey;
        btnHotkey.Click += (s, e) => {
            btnHotkey.Text = "Press any key...";
            btnHotkey.BackColor = Color.FromArgb(0, 120, 215);
            btnHotkey.ForeColor = Color.White;

            KeyEventHandler? keyHandler = null;
            keyHandler = (sender, eventArgs) => {
                tempKey = eventArgs.KeyCode;
                btnHotkey.Text = tempKey.ToString();
                btnHotkey.BackColor = Color.FromArgb(230, 230, 235);
                btnHotkey.ForeColor = Color.FromArgb(30, 30, 35);
                settingsForm.KeyDown -= keyHandler;
                eventArgs.Handled = true;
            };
            settingsForm.KeyPreview = true;
            settingsForm.KeyDown += keyHandler;
        };
        settingsForm.Controls.Add(btnHotkey);

        currentY += 50;

        // Startup Checkbox
        CheckBox chkStartup = new CheckBox
        {
            Text = "Start with Windows",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Location = new Point(24, currentY),
            AutoSize = true,
            Checked = IsStartupEnabled()
        };
        chkStartup.CheckedChanged += (s, e) => SetStartup(chkStartup.Checked);
        settingsForm.Controls.Add(chkStartup);

        currentY += 50;

        // Save Button
        Button btnSave = new Button
        {
            Text = "Save Changes",
            Location = new Point(204, currentY + 10),
            Width = 110,
            Height = 36,
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        btnSave.FlatAppearance.BorderSize = 0;
        btnSave.Click += (s, e) => {
            MaxZoom = ((TrackBar)settingsForm.Controls[3]).Value / 2.0f;
            ZoomSpeed = ((TrackBar)settingsForm.Controls[6]).Value / 100.0f;
            ZoomMultiplier = ((TrackBar)settingsForm.Controls[9]).Value / 10.0f;
            CurrentHotkey = tempKey;
            settingsForm.Close();
        };

        // Cancel Button
        Button btnCancel = new Button
        {
            Text = "Cancel",
            Location = new Point(324, currentY + 10),
            Width = 76,
            Height = 36,
            BackColor = Color.FromArgb(220, 220, 225),
            ForeColor = Color.FromArgb(50, 50, 55),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        btnCancel.FlatAppearance.BorderSize = 0;
        btnCancel.Click += (s, e) => settingsForm.Close();

        settingsForm.Controls.Add(btnSave);
        settingsForm.Controls.Add(btnCancel);

        settingsForm.ShowDialog();
    }

    private static bool IsHotkeyHeld()
    {
        if (CurrentHotkey == Keys.Menu)
        {
            return (GetAsyncKeyState(0x12) & 0x8000) != 0; // Alt
        }
        else if (CurrentHotkey == Keys.ControlKey)
        {
            return (GetAsyncKeyState(0x11) & 0x8000) != 0; // Ctrl
        }
        else if (CurrentHotkey == Keys.ShiftKey)
        {
            return (GetAsyncKeyState(0x10) & 0x8000) != 0; // Shift
        }
        else
        {
            return (GetAsyncKeyState((int)CurrentHotkey) & 0x8000) != 0;
        }
    }

    private static bool IsStartupEnabled()
    {
        string runKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(runKeyPath, false))
        {
            return key?.GetValue("GazeZoom") != null;
        }
    }

    private static void SetStartup(bool enable)
    {
        string runKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        string appName = "GazeZoom";

        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(runKeyPath, true))
        {
            if (key != null)
            {
                if (enable)
                {
                    string exePath = Application.ExecutablePath;
                    key.SetValue(appName, $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue(appName, false);
                }
            }
        }
    }

    private static void StartTimerIfNeeded()
    {
        if (!_animTimer.Enabled)
        {
            _animTimer.Start();
        }
    }

    private static void AnimationTick(object? sender, EventArgs e)
    {
        bool isHotkeyPressed = IsHotkeyHeld();
        if (!isHotkeyPressed && _targetZoom > 1.0f)
        {
            _targetZoom = 1.0f; // Glide back to normal
        }

        if (Math.Abs(_targetZoom - _zoomFactor) > 0.001f)
        {
            _zoomFactor += (_targetZoom - _zoomFactor) * ZoomSpeed;
            UpdateMagnificationTransform();
        }
        else
        {
            _zoomFactor = _targetZoom;
            UpdateMagnificationTransform();
            
            if (_zoomFactor <= 1.001f)
            {
                MagSetFullscreenTransform(1.0f, 0, 0);
                _animTimer.Stop();
            }
        }
    }

    private static void UpdateMagnificationTransform()
    {
        if (_zoomFactor > 1.001f)
        {
            POINT pt;
            GetCursorPos(out pt);

            int xOffset = (int)(pt.x - (_screenWidth / (2.0f * _zoomFactor)));
            int yOffset = (int)(pt.y - (_screenHeight / (2.0f * _zoomFactor)));

            int maxPossibleX = (int)(_screenWidth * (1.0f - (1.0f / _zoomFactor)));
            int maxPossibleY = (int)(_screenHeight * (1.0f - (1.0f / _zoomFactor)));

            xOffset = Math.Clamp(xOffset, 0, Math.Max(0, maxPossibleX));
            yOffset = Math.Clamp(yOffset, 0, Math.Max(0, maxPossibleY));

            MagSetFullscreenTransform(_zoomFactor, xOffset, yOffset);
        }
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private static IntPtr SetMouseHook(LowLevelMouseProc proc)
    {
        using (Process curProcess = Process.GetCurrentProcess())
        using (ProcessModule curModule = curProcess.MainModule!)
        {
            return SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
        }
    }

    private static IntPtr SetKeyboardHook(LowLevelKeyboardProc proc)
    {
        using (Process curProcess = Process.GetCurrentProcess())
        using (ProcessModule curModule = curProcess.MainModule!)
        {
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private static IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            bool isHotkeyPressed = IsHotkeyHeld();

            if (isHotkeyPressed)
            {
                MSLLHOOKSTRUCT hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                int msg = wParam.ToInt32();

                if (msg == WM_MOUSEWHEEL)
                {
                    int wheelDelta = (short)((hookStruct.mouseData >> 16) & 0xFFFF);

                    if (wheelDelta > 0)
                    {
                        _targetZoom = Math.Min(_targetZoom * ZoomMultiplier, MaxZoom);
                    }
                    else
                    {
                        _targetZoom = Math.Max(_targetZoom / ZoomMultiplier, MIN_ZOOM);
                    }

                    StartTimerIfNeeded();
                    return (IntPtr)1; 
                }
                else if (msg == WM_MOUSEMOVE && _zoomFactor > 1.0f)
                {
                    UpdateMagnificationTransform();
                }
            }
            else
            {
                if (_zoomFactor > 1.0f && _targetZoom > 1.0f)
                {
                    _targetZoom = 1.0f;
                    StartTimerIfNeeded();
                }
            }
        }

        return CallNextHookEx(_mouseHookID, nCode, wParam, lParam);
    }

    private static IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        return CallNextHookEx(_keyboardHookID, nCode, wParam, lParam);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, Delegate lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("Magnification.dll")]
    private static extern bool MagInitialize();

    [DllImport("Magnification.dll")]
    private static extern bool MagUninitialize();

    [DllImport("Magnification.dll")]
    private static extern bool MagSetFullscreenTransform(float magLevel, int xOffset, int yOffset);
}