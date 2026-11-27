using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;

namespace MCL.Services;

internal enum RoomRoute { Unknown, Ipv6, Cloudflare, SharedRelay }

internal sealed class RoomDiagnostic(bool hosting, RoomRoute route)
{
    private static readonly HashSet<string> SafeStages = new(StringComparer.Ordinal)
    {
        "准备开房", "启动游戏", "识别局域网房间", "核对房主模组", "准备 IPv6 直连", "IPv6 房间已开放",
        "连接中转", "已转发好友的连接", "房间已开放", "中转已断开", "查找好友房间", "检查 IPv6 直连",
        "检查版本和模组", "连接房间失败", "已连通房主游戏", "等待从游戏内加入", "邀请码有误",
        "局域网已关闭", "联机已结束"
    };
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly List<(string Stage, int Seconds)> _stages = [];
    private string? _error;
    internal string RouteName => route switch
    {
        RoomRoute.Ipv6 => "IPv6 直连",
        RoomRoute.Cloudflare => "Cloudflare 自动隧道",
        RoomRoute.SharedRelay => "手动中转",
        _ => "待识别"
    };
    internal string Details => string.Join(Environment.NewLine, _stages
        .Select(entry => $"+{entry.Seconds}s  {entry.Stage}")
        .Concat(_error is { } error ? [$"结果：{error}"] : []));

    internal bool Record(string stage)
    {
        if (!SafeStages.Contains(stage) && !(stage.EndsWith("失败", StringComparison.Ordinal) && SafeStages.Contains(stage[..^2])))
            return false;
        if (_stages.Count > 0 && _stages[^1].Stage == stage) return false;
        if (_stages.Count == 16) _stages.RemoveAt(0);
        _stages.Add((stage, (int)_elapsed.Elapsed.TotalSeconds));
        if (stage is "已连通房主游戏" or "已转发好友的连接") _error = null;
        return true;
    }

    internal void SetFailure(Exception ex)
    {
        _error = ex switch
        {
            InvalidDataException => "邀请码、版本或房间数据未通过检查",
            AuthenticationException => "身份验证失败",
            SocketException or HttpRequestException or WebSocketException => "网络连接失败",
            OperationCanceledException or TimeoutException => "连接超时或中断",
            _ => "当前步骤未能完成"
        };
        for (Exception? cause = ex; cause is not null; cause = cause.InnerException)
        {
            if (cause is SocketException socket)
            {
                _error = $"网络连接失败（{socket.SocketErrorCode}）";
                break;
            }
            if (cause is AuthenticationException) _error = "身份验证失败";
        }
    }

    internal string Format(string version) => string.Join(Environment.NewLine,
        $"MCL 联机诊断 · {version}", $"角色：{(hosting ? "房主" : "加入方")}", $"方式：{RouteName}", Details);
}
