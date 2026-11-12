using System.Windows;
using KidSafe.Agent.Core;

namespace KidSafe.Agent.Helper;

public partial class PairWindow : Window
{
    private readonly HelperHost _host;

    public PairWindow(HelperHost host)
    {
        InitializeComponent();
        _host = host;
        NameBox.Text = Environment.MachineName;
        Loaded += (_, _) => CodeBox.Focus();
    }

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text.Trim();
        if (code.Length != 6) { StatusText.Text = "Mã gồm đúng 6 ký tự."; return; }
        PairButton.IsEnabled = false;
        StatusText.Text = "";
        await _host.SendAsync(IpcNames.PairSubmit, new PairSubmitPayload(code, NameBox.Text.Trim()));
    }

    /// <summary>Success closes the window via the next "state" message (Paired = true).</summary>
    public void ShowResult(ResultPayload r)
    {
        PairButton.IsEnabled = true;
        if (r.Ok) return;
        StatusText.Text = r.Error switch
        {
            "invalid_or_expired_code" => "Mã không đúng hoặc đã hết hạn. Hãy tạo mã mới trên ứng dụng phụ huynh.",
            "rate_limited" => "Thử quá nhiều lần. Vui lòng đợi một phút.",
            "network" => "Không kết nối được máy chủ. Kiểm tra mạng.",
            _ => "Ghép nối thất bại: " + r.Error,
        };
    }
}
