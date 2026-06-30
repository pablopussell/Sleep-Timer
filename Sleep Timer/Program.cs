using Microsoft.Win32;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;

class Config
{
    public int brightness { get; set; } = 4;
    public int volume { get; set; } = 12;
    public int idleMinutes { get; set; } = 2;
    public int warnSecondsBefore { get; set; } = 60;
}

class Program : Form
{
    private static NotifyIcon? trayIcon;
    private WarningOverlay? overlay;
    private Config config = new Config();
    private bool standbyTriggered = false;
    private bool warningShown = false;
    private bool countdownFinished = false;
    private System.Windows.Forms.Timer? idleTimer;
    private System.Windows.Forms.Timer? brightnessTimer;
    private System.Windows.Forms.Timer? volumeTimer;

    private float initialVolumeScalar;
    private int initialBrightness;

    private ToolStripMenuItem? brightnessSubMenu;
    private ToolStripMenuItem? volumeSubMenu;
    private ToolStripMenuItem? idleSubMenu;
    private ToolStripMenuItem? warnSubMenu;

    private ToolStripTextBox? brightnessTextBox;
    private ToolStripTextBox? volumeTextBox;
    private ToolStripTextBox? idleTextBox;
    private ToolStripTextBox? warnTextBox;


    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("powrprof.dll", SetLastError = true)]
    static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr pNotify);
        int UnregisterControlChangeNotify(IntPtr pNotify);
        int GetChannelCount(out uint channelCount);
        int SetMasterVolumeLevel(float levelDB, Guid eventContext);
        int SetMasterVolumeLevelScalar(float level, Guid eventContext);
        int GetMasterVolumeLevel(out float levelDB);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channelNumber, float levelDB, Guid eventContext);
        int SetChannelVolumeLevelScalar(uint channelNumber, float level, Guid eventContext);
        int GetChannelVolumeLevel(uint channelNumber, out float levelDB);
        int GetChannelVolumeLevelScalar(uint channelNumber, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool isMuted, Guid eventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool isMuted);
        int GetVolumeStepInfo(out uint step, out uint stepCount);
        int VolumeStepUp(Guid eventContext);
        int VolumeStepDown(Guid eventContext);
        [PreserveSig]
        int GetVolumeRange(out float minDB, out float maxDB, out float incrementDB);
    }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int NotImpl1();
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr pActivationParams, out IAudioEndpointVolume ppInterface);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorComObject { }

    [STAThread]
    static void Main()
    {
        Application.Run(new Program());
    }

    public Program()
    {
        SaveInitialVolume();
        SaveInitialBrightness();
        LoadConfig();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        this.WindowState = FormWindowState.Minimized;
        this.ShowInTaskbar = false;
        this.Opacity = 0;

        trayIcon = new NotifyIcon();
        trayIcon.Text = "Sleep Timer";
        trayIcon.Icon = Sleep_Timer.Properties.Resources.moon_dark;
        trayIcon.Visible = true;

        BuildMenu();

        trayIcon.ShowBalloonTip(1000, "Active", $"Sleep Timer Set: {config.idleMinutes} min", ToolTipIcon.Info);

        idleTimer = new System.Windows.Forms.Timer();
        idleTimer.Interval = 2000;
        idleTimer.Tick += CheckIdle;
        idleTimer.Start();

        ApplySettings();
        UpdateMenuStates();
    }

    // =============================
    // CONFIG
    // =============================
    private void LoadConfig()
    {
        try
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

            if (!File.Exists(path))
            {
                config = new Config();
                File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path)) ?? new Config();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Error loading config.json: " + ex.Message);
            config = new Config();
        }
    }

    private void SaveConfig()
    {
        try
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Error saving config.json: " + ex.Message);
        }
    }

    private void ReloadConfig(object? sender, EventArgs e)
    {
        LoadConfig();
        ApplySettings();
        UpdateMenuStates();
        trayIcon?.ShowBalloonTip(1000, "Config", "Reloaded", ToolTipIcon.Info);
    }

    private void ApplySettings()
    {
        SetBrightness(config.brightness);
        SetVolume(config.volume);
    }

    // =============================
    // DYNAMIC CONTEXT MENU STRIP
    // =============================
    private void BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Opening += (s, e) => UpdateMenuStates();

        // 1. Header/Info
        var headerItem = new ToolStripMenuItem("Sleep Timer") { Enabled = false };
        menu.Items.Add(headerItem);
        menu.Items.Add(new ToolStripSeparator());

        // 2. Brightness Submenu
        brightnessSubMenu = new ToolStripMenuItem("Brightness");
        BuildBrightnessSubMenu();
        menu.Items.Add(brightnessSubMenu);

        // 3. Volume Submenu
        volumeSubMenu = new ToolStripMenuItem("Volume");
        BuildVolumeSubMenu();
        menu.Items.Add(volumeSubMenu);

        // 4. Idle Minutes Submenu
        idleSubMenu = new ToolStripMenuItem("Idle Minutes");
        BuildIdleSubMenu();
        menu.Items.Add(idleSubMenu);

        // 5. Warn Seconds Submenu
        warnSubMenu = new ToolStripMenuItem("Warning Delay");
        BuildWarnSubMenu();
        menu.Items.Add(warnSubMenu);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Reload config", null, ReloadConfig));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, Exit));

        if (trayIcon != null)
        {
            trayIcon.ContextMenuStrip = menu;
        }
    }

    private void BuildBrightnessSubMenu()
    {
        if (brightnessSubMenu == null) return;
        brightnessSubMenu.DropDownItems.Clear();

        brightnessTextBox = new ToolStripTextBox();
        brightnessTextBox.KeyDown += (s, e) => OnTextBoxKeyDown(s, e, val =>
        {
            config.brightness = Math.Clamp(val, 0, 100);
            SaveConfig();
            ApplySettings();
            UpdateMenuStates();
        });

        brightnessSubMenu.DropDownItems.Add(new ToolStripMenuItem("Custom (%) :") { Enabled = false });
        brightnessSubMenu.DropDownItems.Add(brightnessTextBox);
        brightnessSubMenu.DropDownItems.Add(new ToolStripSeparator());

        int[] presets = { 0, 5, 10, 15, 20, 25 };
        foreach (int p in presets)
        {
            var item = new ToolStripMenuItem($"{p}%") { Tag = p };
            item.Click += (s, e) =>
            {
                config.brightness = p;
                SaveConfig();
                ApplySettings();
                UpdateMenuStates();
            };
            brightnessSubMenu.DropDownItems.Add(item);
        }
    }

    private void BuildVolumeSubMenu()
    {
        if (volumeSubMenu == null) return;
        volumeSubMenu.DropDownItems.Clear();

        volumeTextBox = new ToolStripTextBox();
        volumeTextBox.KeyDown += (s, e) => OnTextBoxKeyDown(s, e, val =>
        {
            config.volume = Math.Clamp(val, 0, 100);
            SaveConfig();
            ApplySettings();
            UpdateMenuStates();
        });

        volumeSubMenu.DropDownItems.Add(new ToolStripMenuItem("Custom (%) :") { Enabled = false });
        volumeSubMenu.DropDownItems.Add(volumeTextBox);
        volumeSubMenu.DropDownItems.Add(new ToolStripSeparator());

        int[] presets = { 0, 5, 10, 15, 20, 25 };
        foreach (int p in presets)
        {
            var item = new ToolStripMenuItem($"{p}%") { Tag = p };
            item.Click += (s, e) =>
            {
                config.volume = p;
                SaveConfig();
                ApplySettings();
                UpdateMenuStates();
            };
            volumeSubMenu.DropDownItems.Add(item);
        }
    }

    private void BuildIdleSubMenu()
    {
        if (idleSubMenu == null) return;
        idleSubMenu.DropDownItems.Clear();

        idleTextBox = new ToolStripTextBox();
        idleTextBox.KeyDown += (s, e) => OnTextBoxKeyDown(s, e, val =>
        {
            config.idleMinutes = Math.Max(1, val);
            config.warnSecondsBefore = Math.Clamp(config.warnSecondsBefore, 0, Math.Max(0, config.idleMinutes * 60 - 1));
            SaveConfig();
            UpdateMenuStates();
        });

        idleSubMenu.DropDownItems.Add(new ToolStripMenuItem("Custom (min) :") { Enabled = false });
        idleSubMenu.DropDownItems.Add(idleTextBox);
        idleSubMenu.DropDownItems.Add(new ToolStripSeparator());

        int[] presets = { 15, 30, 45, 60, 90, 120 };
        foreach (int p in presets)
        {
            var item = new ToolStripMenuItem($"{p} min") { Tag = p };
            item.Click += (s, e) =>
            {
                config.idleMinutes = p;
                config.warnSecondsBefore = Math.Clamp(config.warnSecondsBefore, 0, Math.Max(0, config.idleMinutes * 60 - 1));
                SaveConfig();
                UpdateMenuStates();
            };
            idleSubMenu.DropDownItems.Add(item);
        }
    }

    private void BuildWarnSubMenu()
    {
        if (warnSubMenu == null) return;
        warnSubMenu.DropDownItems.Clear();

        warnTextBox = new ToolStripTextBox();
        warnTextBox.KeyDown += (s, e) => OnTextBoxKeyDown(s, e, val =>
        {
            config.warnSecondsBefore = Math.Clamp(val, 0, Math.Max(0, config.idleMinutes * 60 - 1));
            SaveConfig();
            UpdateMenuStates();
        });

        warnSubMenu.DropDownItems.Add(new ToolStripMenuItem("Custom (sec) :") { Enabled = false });
        warnSubMenu.DropDownItems.Add(warnTextBox);
        warnSubMenu.DropDownItems.Add(new ToolStripSeparator());

        int[] presets = { 15, 30, 45, 60, 120 };
        foreach (int p in presets)
        {
            var item = new ToolStripMenuItem($"{p} sec") { Tag = p };
            item.Click += (s, e) =>
            {
                config.warnSecondsBefore = Math.Clamp(p, 0, Math.Max(0, config.idleMinutes * 60 - 1));
                SaveConfig();
                UpdateMenuStates();
            };
            warnSubMenu.DropDownItems.Add(item);
        }
    }

    private void OnTextBoxKeyDown(object? sender, KeyEventArgs e, Action<int> updateAction)
    {
        if (e.KeyCode == Keys.Enter)
        {
            if (sender is ToolStripTextBox textBox)
            {
                if (int.TryParse(textBox.Text, out int val))
                {
                    updateAction(val);
                }
                else
                {
                    UpdateMenuStates();
                }
                trayIcon?.ContextMenuStrip?.Close();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }
    }

    private void UpdateMenuStates()
    {
        if (trayIcon?.ContextMenuStrip == null) return;

        if (trayIcon.ContextMenuStrip.Items.Count > 0 && trayIcon.ContextMenuStrip.Items[0] is ToolStripMenuItem header)
        {
            header.Text = $"Sleep Timer: {config.idleMinutes} min ({config.warnSecondsBefore}s warn)";
        }

        if (brightnessSubMenu != null)
        {
            brightnessSubMenu.Text = $"Brightness ({config.brightness}%)";
            if (brightnessTextBox != null && !brightnessTextBox.Focused)
            {
                brightnessTextBox.Text = config.brightness.ToString();
            }
            foreach (ToolStripItem item in brightnessSubMenu.DropDownItems)
            {
                if (item is ToolStripMenuItem presetItem && presetItem.Tag is int val)
                {
                    presetItem.Checked = (val == config.brightness);
                }
            }
        }

        if (volumeSubMenu != null)
        {
            volumeSubMenu.Text = $"Volume ({config.volume}%)";
            if (volumeTextBox != null && !volumeTextBox.Focused)
            {
                volumeTextBox.Text = config.volume.ToString();
            }
            foreach (ToolStripItem item in volumeSubMenu.DropDownItems)
            {
                if (item is ToolStripMenuItem presetItem && presetItem.Tag is int val)
                {
                    presetItem.Checked = (val == config.volume);
                }
            }
        }

        if (idleSubMenu != null)
        {
            idleSubMenu.Text = $"Idle Minutes ({config.idleMinutes} min)";
            if (idleTextBox != null && !idleTextBox.Focused)
            {
                idleTextBox.Text = config.idleMinutes.ToString();
            }
            foreach (ToolStripItem item in idleSubMenu.DropDownItems)
            {
                if (item is ToolStripMenuItem presetItem && presetItem.Tag is int val)
                {
                    presetItem.Checked = (val == config.idleMinutes);
                }
            }
        }

        if (warnSubMenu != null)
        {
            warnSubMenu.Text = $"Warning Delay ({config.warnSecondsBefore} sec)";
            if (warnTextBox != null && !warnTextBox.Focused)
            {
                warnTextBox.Text = config.warnSecondsBefore.ToString();
            }
            foreach (ToolStripItem item in warnSubMenu.DropDownItems)
            {
                if (item is ToolStripMenuItem presetItem && presetItem.Tag is int val)
                {
                    presetItem.Checked = (val == config.warnSecondsBefore);
                }
            }
        }
    }

    // ===============================================================
    // SAVE INITIAL VOLUME & BRIGHTNESS LEVELS FOR APP-EXIT RESTORE
    // ===============================================================

    private void SaveInitialVolume()
    {
        IMMDeviceEnumerator? enumerator = new MMDeviceEnumeratorComObject() as IMMDeviceEnumerator;
        IMMDevice? device = null;
        IAudioEndpointVolume? volume = null;

        try
        {
            if (enumerator == null) return;
            int hr = enumerator.GetDefaultAudioEndpoint(0, 1, out device);
            if (hr != 0 || device == null) return;

            Guid IID_IAudioEndpointVolume = typeof(IAudioEndpointVolume).GUID;
            hr = device.Activate(ref IID_IAudioEndpointVolume, 23, IntPtr.Zero, out volume);
            if (hr != 0 || volume == null) return;

            // Get current volume as scalar (0.0 → 1.0)
            volume.GetMasterVolumeLevelScalar(out initialVolumeScalar);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Error saving initial volume: " + ex.Message);
        }
    }

    private void SaveInitialBrightness()
    {
        try
        {
            var scope = new ManagementScope("root\\WMI");
            var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM WmiMonitorBrightness"));

            foreach (ManagementObject obj in searcher.Get())
            {
                initialBrightness = Convert.ToInt32(obj["CurrentBrightness"]);
                break; // We only need the first result
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Error saving initial brightness: " + ex.Message);
        }
    }

    // =============================
    // TIMER
    // =============================
    private static uint GetIdleTime()
    {
        LASTINPUTINFO lii = new LASTINPUTINFO();
        lii.cbSize = (uint)Marshal.SizeOf(lii);

        if (!GetLastInputInfo(ref lii))
            return 0;

        return ((uint)Environment.TickCount64 - lii.dwTime);
    }

    private void CheckIdle(object? sender, EventArgs e)
    {
        double idleSeconds = GetIdleTime() / 1000.0;
        double totalSec = config.idleMinutes * 60;
        double warnThreshold = totalSec - config.warnSecondsBefore;

        //Debug.WriteLine($"Idle: {idleSeconds}, WarnAt: {warnThreshold}, SleepAt: {totalSec}");

        //if (idleSeconds >= totalSec && !standbyTriggered && !warningShown)
        //{
        //    standbyTriggered = true;
        //
        //    if (overlay != null && !overlay.IsDisposed)
        //    {
        //        overlay.Close();
        //    }
        //
        //    overlay = null;
        //    return;
        //}

        if (idleSeconds < warnThreshold)
        {
            if (overlay != null && !overlay.IsDisposed)
            {
                overlay.Close();  // hides the overlay
                overlay = null;
                warningShown = false;

                // Show balloon tip because user reset idle
                trayIcon?.ShowBalloonTip(1000, "Active", "Sleep Timer Reset", ToolTipIcon.Info);
            }
            standbyTriggered = false;
            countdownFinished = false;
            return;
        }

        if (!warningShown)
        {
            warningShown = true;

            if (overlay == null || overlay.IsDisposed)
            {
                overlay = new WarningOverlay(config.warnSecondsBefore); // config.warnSecondsBefore countdown

                overlay.OnCancelSleep = () =>
                    {
                        trayIcon?.ShowBalloonTip(1000, "Active", "Sleep Timer Reset", ToolTipIcon.Info);
                    };

                overlay.OnCountdownFinished = async () =>
                {
                    countdownFinished = true;
                    if (!standbyTriggered)
                    {
                        standbyTriggered = true;
                        await IdleTriggeredShutdown();
                    }
                };

                overlay.FormClosed += (s, e) =>
                    {
                        overlay = null;
                        if (!countdownFinished)
                            warningShown = false;
                    };
            }
            if (!overlay.Visible)
            {
                overlay.Show();
            }
        }
        if (idleSeconds >= totalSec && !standbyTriggered && !warningShown)
        {
            standbyTriggered = true;
            _ = IdleTriggeredShutdown();
        }
    }

    // =============================
    // BRIGHTNESS
    // =============================
    private Task SetBrightness(int targetValue, int durationMs = 1000)
    {
        var tcs = new TaskCompletionSource<bool>();

        try
        {
            var scope = new ManagementScope("root\\WMI");
            var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM WmiMonitorBrightness"));

            int currentBrightness = 0;

            // Get current brightness first
            foreach (ManagementObject obj in searcher.Get())
            {
                currentBrightness = Convert.ToInt32(obj["CurrentBrightness"]);
                break;
            }

            targetValue = Math.Clamp(targetValue, 0, 100);

            int interval = 16;
            int steps = Math.Max(1, durationMs / interval);
            int stepCount = 0;

            brightnessTimer = new System.Windows.Forms.Timer
            {
                Interval = interval
            };

            brightnessTimer.Tick += (s, e) =>
            {
                stepCount++;

                float t = Math.Min(1f, stepCount / (float)steps);

                // Smooth easing (ease-out cubic)
                float eased = 1f - (float)Math.Pow(1f - t, 3);

                int newValue = (int)(currentBrightness + (targetValue - currentBrightness) * eased);
                newValue = Math.Clamp(newValue, 0, 100);

                // Apply instantly (we control the fade)
                var methodSearcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM WmiMonitorBrightnessMethods"));

                foreach (ManagementObject obj in methodSearcher.Get())
                {
                    obj.InvokeMethod("WmiSetBrightness", new object[] { 0, newValue });
                }

                if (stepCount >= steps)
                {
                    // Ensure exact final value
                    foreach (ManagementObject obj in methodSearcher.Get())
                    {
                        obj.InvokeMethod("WmiSetBrightness", new object[] { 0, targetValue });
                    }

                    brightnessTimer.Stop();
                    brightnessTimer.Dispose();
                    brightnessTimer = null;

                    tcs.TrySetResult(true);
                }
            };

            brightnessTimer.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Error during setting config-defined brightness procedure: " + ex.Message);
            tcs.TrySetResult(true);
        }

        return tcs.Task;
    }

    // =============================
    // VOLUME
    // =============================
    private Task SetVolume(int targetPercent, int durationMs = 1000)
    {
        var tcs = new TaskCompletionSource<bool>();

        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? volume = null;

        try
        {
            enumerator = new MMDeviceEnumeratorComObject() as IMMDeviceEnumerator;
            if (enumerator == null) return Task.CompletedTask;
            int hr = enumerator.GetDefaultAudioEndpoint(0, 1, out device);

            if (hr != 0 || device == null) return Task.CompletedTask;
            Guid IID_IAudioEndpointVolume = typeof(IAudioEndpointVolume).GUID;
            hr = device.Activate(ref IID_IAudioEndpointVolume, 23, IntPtr.Zero, out volume);
            if (hr != 0 || volume == null) return Task.CompletedTask;

            // Current and target volume scalar
            volume.GetMasterVolumeLevelScalar(out float currentScalar);
            float targetScalar = Math.Clamp(targetPercent / 100f, 0f, 1f);

            int interval = 16; // ~60 FPS
            int steps = Math.Max(1, durationMs / interval);
            int stepCount = 0;

            volumeTimer?.Stop();
            volumeTimer?.Dispose();

            volumeTimer = new System.Windows.Forms.Timer { Interval = interval };
            volumeTimer.Tick += (s, e) =>
            {
                stepCount++;
                float t = SafeClamp(stepCount / (float)steps, 0f, 1f);

                // Smooth easing (cubic)
                float eased = 1f - (float)Math.Pow(1f - t, 3);

                // Interpolate scalar
                float scalar = currentScalar + (targetScalar - currentScalar) * eased;
                scalar = SafeClamp(scalar, 0f, 1f);

                // Set scalar instead of dB
                volume.SetMasterVolumeLevelScalar(scalar, Guid.Empty);

                if (stepCount >= steps)
                {
                    // Ensure exact final value
                    volume.SetMasterVolumeLevelScalar(targetScalar, Guid.Empty);

                    volumeTimer.Stop();
                    volumeTimer.Dispose();
                    volumeTimer = null;

                    // Clean up COM
                    if (volume != null) Marshal.ReleaseComObject(volume);
                    if (device != null) Marshal.ReleaseComObject(device);
                    if (enumerator != null) Marshal.ReleaseComObject(enumerator);

                    tcs.TrySetResult(true);
                }
            };

            volumeTimer.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Error while setting volume: " + ex.Message);
            tcs.TrySetResult(true);
        }
        return tcs.Task;
    }
    private static float SafeClamp(float value, float min, float max)
    {
        if (min > max) (min, max) = (max, min);
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    // =============================
    // RESET ON POWER MODE CHANGE WHEN COMING BACK FROM IDLE
    // =============================
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            standbyTriggered = false;
        }
    }

    // =============================
    // SHUTDOWN PROCEDURES
    // =============================

    private async void Exit(object? sender, EventArgs e)
    {
        await ShutdownApp();
    }

    private bool _shuttingDown = false;
    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (_shuttingDown)
        {
            base.OnFormClosing(e);
            return;
        }

        e.Cancel = true;
        _shuttingDown = true;

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        await ShutdownApp();
    }



    private async Task ShutdownApp()
    {
        await Task.WhenAll(
            SetBrightness(initialBrightness, 300),
            SetVolume((int)(initialVolumeScalar * 100), 300)
        );
        try
        {
            idleTimer?.Stop();

            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }
            Application.ExitThread();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Error during app shutdown procedure: " + ex.Message);
            Environment.Exit(0);
        }
    }

    private async Task IdleTriggeredShutdown()
    {
        await Task.WhenAll(
            SetBrightness(initialBrightness, 300),
            SetVolume((int)(initialVolumeScalar * 100), 300)
        );
        try
        {
            idleTimer?.Stop();

            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }

            SetSuspendState(false, true, false);

            Application.Exit();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Error during app shutdown procedure: " + ex.Message);
            Environment.Exit(0);
        }
    }
}

