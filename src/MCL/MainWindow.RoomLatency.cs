using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Windows;
using System.Windows.Media;
using MCL.Services;

namespace MCL;

public partial class MainWindow
{
    private enum LatencyTarget { None, Relay, PublicTunnel, PublicTunnelLoop, DirectHost, DirectGuest }

    private readonly RoomLatencyHistory _latencyHistory = new();
    private LatencyTarget _latencyTarget;
    private bool _latencyRunning;
    private bool _awaitingDirectSample;
    private int _chartMax = 50;
    private RoomLatencySnapshot _latency = new([], null, null, null, 0);

    private string LatencyTargetLabel => _latencyTarget switch
    {
        LatencyTarget.Relay => "本机 ↔ 中转入口",
        LatencyTarget.PublicTunnel => "本机 ↔ Cloudflare 公网入口",
        LatencyTarget.PublicTunnelLoop => "本机 ↔ Cloudflare 公网入口 ↔ 本机",
        LatencyTarget.DirectHost => "本机 ↔ 一位直连好友",
        LatencyTarget.DirectGuest => "本机 ↔ IPv6 房主",
        _ => ""
    };

    public string RoomLatencyCaption => _latencyTarget == LatencyTarget.None
        ? "创建或加入房间后自动测速"
        : $"{LatencyTargetLabel} · {(!_latencyRunning ? "已停止" : _awaitingDirectSample ? "等待好友测速" : "实时")}";
    public string RoomLatencyCurrent => _latency.Samples.Length > 0 && _latency.Current is null ? "未响应" : FormatLatency(_latency.Current);
    public string RoomLatencyAverage => FormatLatency(_latency.Average);
    public string RoomLatencyJitter => FormatLatency(_latency.Jitter);
    public string RoomLatencyMissed => _latency.Samples.Length == 0 ? "—" : $"{_latency.Missed} / {_latency.Samples.Length}";
    public string RoomLatencyChartTop => $"{_chartMax} ms";
    public string RoomLatencyChartMiddle => $"{_chartMax / 2} ms";
    public Geometry RoomLatencyCurve { get; private set; } = Geometry.Empty;
    public Geometry RoomLatencyTimeoutMarks { get; private set; } = Geometry.Empty;

    private static string FormatLatency(double? value) => value switch
    {
        null => "—",
        < 1 => "<1 ms",
        _ => $"{value:0} ms"
    };

    private void StartLatency(LatencyTarget target)
    {
        _latencyTarget = target;
        _latencyRunning = true;
        _awaitingDirectSample = target == LatencyTarget.DirectHost;
        Notify(nameof(RoomLatencyCaption));
    }

    private void ClearLatency()
    {
        _latencyRunning = false;
        _latencyTarget = LatencyTarget.None;
        _awaitingDirectSample = false;
        _latencyHistory.Clear();
        UpdateLatencyDisplay();
        Notify(nameof(RoomLatencyCaption));
    }

    private void StopLatency()
    {
        _latencyRunning = false;
        Notify(nameof(RoomLatencyCaption));
    }

    private void ObserveDirectGuestLatency(CancellationTokenSource room, double? sample)
    {
        if (_closing || room.IsCancellationRequested || !ReferenceEquals(room, _multiplayerCancellation)) return;
        if (sample is null)
        {
            _awaitingDirectSample = true;
            Notify(nameof(RoomLatencyCaption));
            return;
        }
        if (_awaitingDirectSample)
        {
            _awaitingDirectSample = false;
            _latencyHistory.Clear();
            Notify(nameof(RoomLatencyCaption));
        }
        AddLatencySample(sample);
    }

    private async Task MonitorLatencyAsync(CancellationTokenSource room, LatencyTarget target,
        Func<CancellationToken, Task<double>> measure, IDisposable? probe = null)
    {
        using (probe)
        {
            var ct = room.Token;
            if (ct.IsCancellationRequested) return;
            StartLatency(target);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(TimeSpan.FromSeconds(3));
                    double? sample;
                    try { sample = await measure(deadline.Token); }
                    catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException or AuthenticationException or OperationCanceledException)
                    { sample = null; }
                    if (_closing || ct.IsCancellationRequested || !ReferenceEquals(room, _multiplayerCancellation)) return;

                    AddLatencySample(sample);
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
    }

    private void AddLatencySample(double? sample)
    {
        _latencyHistory.Add(sample);
        UpdateLatencyDisplay();
    }

    private void UpdateLatencyDisplay()
    {
        _latency = _latencyHistory.Snapshot();
        var peak = _latency.Samples.Max() ?? 0;
        _chartMax = Math.Max(50, (int)Math.Ceiling(peak / 50) * 50);
        var lines = new StreamGeometry();
        var points = new GeometryGroup();
        var misses = new GeometryGroup();
        using (var path = lines.Open())
        {
            var continuing = false;
            for (var i = 0; i < _latency.Samples.Length; i++)
            {
                var x = 7 + 25 * (i + RoomLatencyHistory.Capacity - _latency.Samples.Length);
                if (_latency.Samples[i] is not { } value)
                {
                    continuing = false;
                    misses.Children.Add(new EllipseGeometry(new Point(x, 86), 3, 2.5));
                    continue;
                }
                var point = new Point(x, 84 - 78 * value / _chartMax);
                points.Children.Add(new EllipseGeometry(point, 2.3, 1.9));
                if (continuing) path.LineTo(point, isStroked: true, isSmoothJoin: true);
                else path.BeginFigure(point, isFilled: false, isClosed: false);
                continuing = true;
            }
        }
        points.Children.Add(lines);
        points.Freeze();
        misses.Freeze();
        RoomLatencyCurve = points;
        RoomLatencyTimeoutMarks = misses;
        Notify(nameof(RoomLatencyCurrent), nameof(RoomLatencyAverage), nameof(RoomLatencyJitter), nameof(RoomLatencyMissed),
            nameof(RoomLatencyChartTop), nameof(RoomLatencyChartMiddle), nameof(RoomLatencyCurve), nameof(RoomLatencyTimeoutMarks));
    }

    private string RoomLatencyDiagnostic => _latency.Samples.Length == 0 ? "" : string.Join(Environment.NewLine,
        $"测速范围：{LatencyTargetLabel}",
        $"延迟：{RoomLatencyCurrent}  平均：{RoomLatencyAverage}  波动：{RoomLatencyJitter}  未响应：{RoomLatencyMissed}");
}
