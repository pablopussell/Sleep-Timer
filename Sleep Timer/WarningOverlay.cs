using System.Diagnostics;
using Timer = System.Windows.Forms.Timer;

public class WarningOverlay : Form
{
    private Label countdownLabel = null!;
    private Timer countdownTimer;
    private Panel panel = null!;
    private Timer fadeTimer;
    private Stopwatch stopwatch = new Stopwatch();
    private int totalSeconds;
    private double elapsedSeconds;
    private int remainingSeconds;

    // Delegate to notify main app if user cancels sleep
    public Action? OnCancelSleep;

    // Delegate to notify main app if countdown finishes
    public Action? OnCountdownFinished;

    public WarningOverlay(int secondsUntilSleep)
    {
        totalSeconds = Math.Max(1, secondsUntilSleep); // total countdown duration
        remainingSeconds = totalSeconds;

        // Fullscreen dimming overlay
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Opacity = 0;
        TopMost = true;
        ShowInTaskbar = false;
        WindowState = FormWindowState.Maximized;

        // Container panel for centered content
        panel = new Panel()
        {
            Size = new Size(400, 150),
            BackColor = Color.Black, // always fully black
        };

        // Countdown label
        countdownLabel = new Label()
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Calibri", 25, FontStyle.Bold),
            ForeColor = Color.FromArgb(0, 255, 255, 255), // start transparent
            Text = $"Sleeping in {remainingSeconds} seconds..."
        };

        panel.Controls.Add(countdownLabel);
        Controls.Add(panel);

        // Click-to-cancel
        this.Click += WarningOverlay_Click;
        panel.Click += WarningOverlay_Click;
        countdownLabel.Click += WarningOverlay_Click;

        // Fade in background dimming effect //
        fadeTimer = new Timer { Interval = 16 };
        fadeTimer.Tick += FadeTimer_Tick;

        countdownTimer = new Timer { Interval = 1000 };
        countdownTimer.Tick += CountdownTimer_Tick;

        Shown += WarningOverlay_Shown;
    }

    private void WarningOverlay_Shown(object? sender, EventArgs e)
    {
        panel.Left = (ClientSize.Width - panel.Width) / 2;
        panel.Top = (ClientSize.Height - panel.Height) / 2;

        elapsedSeconds = 0;

        fadeTimer.Start();
        stopwatch.Start();
        countdownTimer.Start();
    }

    private void FadeTimer_Tick(object? sender, EventArgs e)
    {
        const double initialFadeDuration = 2.0;
        const double finalFadeDuration = 10.0;

        double middleFadeDuration = Math.Max(0.001, totalSeconds - initialFadeDuration - finalFadeDuration);
        double finalFadeStart = totalSeconds - finalFadeDuration;

        elapsedSeconds = stopwatch.Elapsed.TotalSeconds;

        double opacity;

        if (elapsedSeconds <= initialFadeDuration)
        {
            double t = Math.Clamp(elapsedSeconds / initialFadeDuration, 0, 1);
            double eased = Math.Sqrt(t);
            opacity = eased * 0.3; // 0 -> 0.3 in first 2 seconds
            countdownLabel.ForeColor = Color.FromArgb(255, 255, 255, 255);
        }
        else if (elapsedSeconds <= finalFadeStart)
        {
            double t = Math.Clamp((elapsedSeconds - initialFadeDuration) / middleFadeDuration,0, 1);
            double eased = t * t;
            opacity = 0.3 + (eased * 0.69999); // up to 0.99 until the last 10 seconds
        }
        else
        {
            opacity = 1.0;
            double t = Math.Clamp((elapsedSeconds - finalFadeStart) / finalFadeDuration, 0, 1);
            double eased = 1.0 - (t * t);
            int rgb = (int)(50 + (205 * eased)); // final rgb (50,50,50)
            countdownLabel.ForeColor = Color.FromArgb(255, rgb, rgb, rgb);
        }

        Opacity = Math.Clamp(opacity, 0, 1);
        //Debug.WriteLine($"Elapsed: {elapsedSeconds}");
        //Debug.WriteLine($"Label color: {countdownLabel.ForeColor}");

        if (elapsedSeconds >= totalSeconds)
        {
            Opacity = 1.0;
            fadeTimer.Stop();
            stopwatch.Stop();
        }
    }

    private void CountdownTimer_Tick(object? sender, EventArgs e)
    {
        remainingSeconds--;

        if (remainingSeconds <= 0)
        {
            countdownLabel.Text = "Sleeping now...";
            countdownTimer.Stop();
            OnCountdownFinished?.Invoke();
            countdownTimer?.Stop();
            stopwatch?.Stop();

            fadeTimer?.Dispose();
            countdownTimer?.Dispose();
            //Close();
            return;
        }

        countdownLabel.Text = $"Sleeping in {remainingSeconds} seconds...";

    }

    private void WarningOverlay_Click(object? sender, EventArgs e)
    {
        countdownTimer.Stop();
        fadeTimer.Stop();
        OnCancelSleep?.Invoke();
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        fadeTimer?.Stop();
        countdownTimer?.Stop();
        stopwatch?.Stop();

        fadeTimer?.Dispose();
        countdownTimer?.Dispose();

        OnCancelSleep = null;

        base.OnFormClosed(e);
    }
}