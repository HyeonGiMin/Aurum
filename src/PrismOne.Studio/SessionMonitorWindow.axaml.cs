using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PrismOne.Db.Core;

namespace PrismOne.Studio;

/// <summary>pg_stat_activity 세션 모니터 (Tools > Session Monitor).</summary>
public partial class SessionMonitorWindow : Window
{
    private readonly ConnectionProfile _profile;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

    public SessionMonitorWindow() : this(ConnectionProfile.Default) { }

    public SessionMonitorWindow(ConnectionProfile profile)
    {
        InitializeComponent();
        _profile = profile;
        _timer.Tick += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _timer.Stop();
        Opened += async (_, _) => await RefreshAsync();
    }

    private async System.Threading.Tasks.Task RefreshAsync()
    {
        try
        {
            var rows = await SessionMonitor.GetActivityAsync(_profile);
            ActivityGrid.ItemsSource = rows;
            MonitorStatus.Text = $"{rows.Count} session(s) · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            MonitorStatus.Text = $"조회 실패: {ex.Message}";
        }
    }

    private async void OnRefresh(object? sender, RoutedEventArgs e) => await RefreshAsync();

    private void OnAutoRefreshChanged(object? sender, RoutedEventArgs e)
    {
        if (AutoRefreshBox.IsChecked == true) _timer.Start();
        else _timer.Stop();
    }

    private async void OnCancelQuery(object? sender, RoutedEventArgs e)
    {
        if (ActivityGrid.SelectedItem is not ActivityRow row) return;
        try
        {
            var ok = await SessionMonitor.CancelAsync(_profile, row.Pid);
            MonitorStatus.Text = ok ? $"PID {row.Pid} 쿼리 취소 요청됨" : $"PID {row.Pid} 취소 실패(권한/종료됨)";
        }
        catch (Exception ex) { MonitorStatus.Text = $"취소 실패: {ex.Message}"; }
        await RefreshAsync();
    }

    private async void OnTerminate(object? sender, RoutedEventArgs e)
    {
        if (ActivityGrid.SelectedItem is not ActivityRow row) return;
        try
        {
            var ok = await SessionMonitor.TerminateAsync(_profile, row.Pid);
            MonitorStatus.Text = ok ? $"PID {row.Pid} 세션 종료됨" : $"PID {row.Pid} 종료 실패(권한 필요)";
        }
        catch (Exception ex) { MonitorStatus.Text = $"종료 실패: {ex.Message}"; }
        await RefreshAsync();
    }
}
