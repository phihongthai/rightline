using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using KidSafe.Agent.Core;

namespace KidSafe.Agent.Helper;

public partial class LockWindow : Window
{
    private readonly HelperHost _host;
    private readonly DispatcherTimer _reassert;
    private bool _allowClose;

    public LockWindow(HelperHost host)
    {
        InitializeComponent();
        _host = host;
        // Cover every monitor.
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        // Keep on top / focused even if something steals the foreground.
        _reassert = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _reassert.Tick += (_, _) =>
        {
            Topmost = false; Topmost = true;
            if (!IsActive && !MinutesBox.IsKeyboardFocusWithin && !ReasonBox.IsKeyboardFocusWithin) Activate();
        };
        _reassert.Start();
    }

    public void Update(HelperState s)
    {
        ReasonText.Text = s.Message;
        UsageText.Text = s.DailyLimitMinutes is { } l ? $"Hôm nay đã dùng {s.ActiveMinutes} / {l} phút" : "";
        if (!s.Online) StatusText.Text = StatusText.Text.Length == 0 ? "Đang offline — vẫn áp dụng lịch đã lưu." : StatusText.Text;
    }

    public void ShowRequestResult(ResultPayload r)
    {
        SendButton.IsEnabled = true;
        StatusText.Text = r.Ok
            ? "Đã gửi yêu cầu cho phụ huynh. Vui lòng chờ."
            : "Không có kết nối mạng nên chưa gửi được yêu cầu.";
    }

    public void Dismiss()
    {
        _allowClose = true;
        _reassert.Stop();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose) e.Cancel = true;      // Alt+F4 etc.
        base.OnClosing(e);
    }

    private void OpenRequest_Click(object sender, RoutedEventArgs e)
    {
        OpenRequestButton.Visibility = Visibility.Collapsed;
        RequestPanel.Visibility = Visibility.Visible;
        MinutesBox.Focus();
    }

    private static readonly Regex Digits = new("^[0-9]+$");
    private void Minutes_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !Digits.IsMatch(e.Text);

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MinutesBox.Text, out var minutes) || minutes < 1 || minutes > 480)
        {
            StatusText.Text = "Nhập số phút từ 1 đến 480.";
            return;
        }
        SendButton.IsEnabled = false;
        StatusText.Text = "Đang gửi…";
        await _host.SendAsync(IpcNames.TimeRequest, new TimeRequestPayload(minutes, ReasonBox.Text));
    }
}
