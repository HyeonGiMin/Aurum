using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PrismOne.Db.Core.Mcp;

namespace PrismOne.Studio;

/// <summary>
/// Claude(MCP)가 쓰기 SQL 이나 COMMIT 을 요청했을 때 사용자에게 묻는 창.
/// SQL 을 고정폭 글꼴로 그대로 보여준다 — 사람이 읽고 승인해야 하므로 요약하지 않는다.
/// 기본 버튼(Enter)은 두지 않는다: 엉겁결에 Enter 로 승인되는 일이 없게.
/// </summary>
public static class McpConfirmDialog
{
    /// <summary>승인 창이 <b>전부</b> 보여줄 수 있는 길이. 이보다 긴 쓰기 SQL 은 run_query 가 아예 받지 않는다.</summary>
    public const int MaxPreviewChars = 20_000;

    /// <summary>자리를 비운 사이 승인 대기가 끝없이 남지 않게 — 시간이 지나면 거부로 본다.</summary>
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 승인하면 true. 거부·창 닫기·<see cref="AnswerTimeout"/> 초과·<paramref name="ct"/> 취소(서버 종료)는 false.
    /// </summary>
    /// <param name="autoCommit">오토커밋 탭 — 실행 즉시 확정된다고 경고한다 (COMMIT 은 따로 묻지 않는다).</param>
    /// <param name="willCommit">허용하면 실행 뒤 COMMIT 까지 한 번에 한다 (수동 커밋 탭, 사용자가 그렇게 시킨 경우).</param>
    public static async Task<bool> ShowAsync(
        Window owner, string message, string? sql, bool autoCommit, bool willCommit, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return false;
        var allowed = false;
        var allow = new Button
        {
            Content = willCommit ? "실행 + COMMIT 허용" : "실행 허용",
            MinWidth = 96,
            MinHeight = 30,
        };
        var deny = new Button { Content = "거부", MinWidth = 80, MinHeight = 30, IsCancel = true };

        var body = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        body.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
        });
        if (!string.IsNullOrEmpty(sql))
        {
            body.Children.Add(new TextBox
            {
                Text = sql,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas,Menlo,Monaco,monospace"),
                MaxHeight = 320,
            });
        }
        if (!string.IsNullOrEmpty(sql))
            body.Children.Add(CommitNote(autoCommit, willCommit));
        body.Children.Add(new TextBlock
        {
            Text = $"{AnswerTimeout.TotalMinutes:0}분 안에 답하지 않으면 거부로 처리합니다.",
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { deny, allow },
        });

        var dialog = new Window
        {
            Title = "Aurum — Claude 요청 확인",
            Icon = AppIcon.Shared,
            Width = 600,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = body,
        };
        allow.Click += (_, _) => { allowed = true; dialog.Close(); };
        deny.Click += (_, _) => dialog.Close();
        dialog.Opened += (_, _) => deny.Focus();

        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(ct);
        expiry.CancelAfter(AnswerTimeout);
        using var registration = expiry.Token.Register(() => Dispatcher.UIThread.Post(() =>
        {
            allowed = false;
            dialog.Close();
        }));
        await dialog.ShowDialog(owner);
        return allowed && !expiry.IsCancellationRequested;
    }

    /// <summary>허용하면 언제 확정되는지 — 사용자가 승인 한 번의 무게를 알고 누르게.</summary>
    private static TextBlock CommitNote(bool autoCommit, bool willCommit)
    {
        if (autoCommit || willCommit)
        {
            return new TextBlock
            {
                Text = autoCommit
                    ? "⚠ 이 탭은 Auto Commit 입니다 — 허용하면 실행 즉시 확정되어 되돌릴 수 없습니다."
                    : "⚠ 허용하면 실행 후 바로 COMMIT 합니다 — 확정되어 되돌릴 수 없습니다. (실행 중 오류가 나면 자동 ROLLBACK)",
                Foreground = Brushes.Firebrick,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            };
        }
        return new TextBlock
        {
            Text = "수동 커밋 탭입니다 — 실행해도 COMMIT 전까지 확정되지 않습니다 (COMMIT 은 따로 묻습니다).",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        };
    }
}

/// <summary>
/// Tools &gt; MCP Server — 켜기/끄기, 포트, Claude Code 등록 명령 복사, 토큰 재발급.
/// 결과는 새 <see cref="McpSettings"/> (취소하면 null). 저장·재시작은 MainWindow 가 한다.
/// </summary>
public sealed class McpSettingsDialog : Window
{
    private const int MinPort = 1024;
    private const int MaxPort = 65535;

    private McpSettings _working;
    private string _fullCommand = "";
    private readonly CheckBox _enabled;
    private readonly TextBox _port;
    private readonly TextBox _command;
    private readonly TextBlock _hint;

    public McpSettings? Result { get; private set; }

    public McpSettingsDialog() : this(new McpSettings().WithNewToken(), null, null, "") { }

    public McpSettingsDialog(McpSettings current, string? runningUrl, string? error, string auditPath)
    {
        _working = current;
        Title = "MCP Server (Claude Code)";
        Icon = AppIcon.Shared;
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _enabled = new CheckBox
        {
            Content = "MCP 서버 켜기 — Claude Code 가 이 Aurum 창을 조작할 수 있게 한다",
            IsChecked = current.Enabled,
        };
        _port = new TextBox { Text = current.Port.ToString(CultureInfo.InvariantCulture), Width = 100 };
        _port.TextChanged += (_, _) => UpdateCommand();
        _command = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas,Menlo,Monaco,monospace"),
        };
        _hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };

        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = error is not null ? $"상태: 오류 — {error}"
                 : runningUrl is not null ? $"상태: 실행 중 — {runningUrl}"
                 : "상태: 꺼짐",
        };

        var copy = new Button { Content = "명령 복사", MinHeight = 28 };
        copy.Click += async (_, _) =>
        {
            // 화면엔 토큰을 가려 두고, 복사할 때만 전체를 넘긴다
            if (Clipboard is { } clipboard && _fullCommand.Length > 0)
                await clipboard.SetTextAsync(_fullCommand);
            _hint.Text = "복사했습니다. Claude Code 를 띄울 터미널에 붙여 넣으세요 (한 번만 등록하면 됩니다).";
        };
        var regenerate = new Button { Content = "토큰 재발급", MinHeight = 28 };
        regenerate.Click += (_, _) =>
        {
            _working = _working.WithNewToken();
            UpdateCommand();
            _hint.Text = "새 토큰입니다. 기존 등록은 동작하지 않으니 `claude mcp remove aurum` 후 다시 등록하세요.";
        };

        var ok = new Button { Content = "OK", MinWidth = 80, MinHeight = 30, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, MinHeight = 30, IsCancel = true };
        ok.Click += (_, _) => OnOk();
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                _enabled,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "포트", VerticalAlignment = VerticalAlignment.Center },
                        _port,
                        new TextBlock
                        {
                            Text = "(localhost 에만 열림 · Bearer 토큰 필요)",
                            VerticalAlignment = VerticalAlignment.Center,
                            Opacity = 0.7,
                        },
                    },
                },
                status,
                new TextBlock { Text = "Claude Code 등록 명령 (터미널에서 한 번 실행):" },
                _command,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { copy, regenerate } },
                _hint,
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.7,
                    Text = "단순 SELECT 만 바로 실행되고, 그 밖의 문장과 COMMIT·ROLLBACK 은 매번 승인을 묻습니다. "
                         + "결과는 호출당 최대 " + current.MaxRows + "행까지만 Claude 에게 전달됩니다. "
                         + "실행·조회 내역(SQL·행 수)은 감사 로그에 남습니다: " + auditPath,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, ok },
                },
            },
        };
        UpdateCommand();
    }

    private int? ParsedPort() =>
        int.TryParse(_port.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
        && port is >= MinPort and <= MaxPort
            ? port
            : null;

    private void UpdateCommand()
    {
        if (ParsedPort() is not { } port)
        {
            _fullCommand = "";
            _command.Text = $"(포트는 {MinPort}–{MaxPort} 사이여야 합니다)";
            return;
        }
        var preview = _working with { Port = port };
        var token = preview.Token ?? "";
        _fullCommand = preview.ClaudeAddCommand(token);
        _command.Text = preview.ClaudeAddCommand(MaskToken(token));
    }

    /// <summary>화면·스크린샷에 토큰이 그대로 남지 않게 앞 네 글자만 보인다.</summary>
    private static string MaskToken(string token) =>
        token.Length <= 4 ? "••••" : token[..4] + new string('•', 12);

    private void OnOk()
    {
        if (ParsedPort() is not { } port)
        {
            _hint.Text = $"포트는 {MinPort}–{MaxPort} 사이 숫자여야 합니다.";
            return;
        }
        Result = _working with { Enabled = _enabled.IsChecked == true, Port = port };
        Close();
    }
}
