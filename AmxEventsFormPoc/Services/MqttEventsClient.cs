using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AmxEventsFormPoc.Models;
using MQTTnet;
using MQTTnet.Protocol;

namespace AmxEventsFormPoc.Services;

// Same shape as DraxView/DraxView/Services/MqttService.cs: one client, the same
// connect/retry-every-5-seconds loop, the same "log the outage once" behaviour,
// the same topics (drax/<panel>/event, drax/<panel>/log, drax/<panel>/cmd) and
// the same MQTTnet version, so this speaks the identical wire dialect as
// DraxView and the Drax Technology service behind it.
//
// The one real difference: DraxView is a BackgroundService running inside
// ASP.NET Core's host, which gives it a cancellation token and a lifetime for
// free. A WinForms app has no such host, so this class manages its own
// Task/CancellationTokenSource instead, started from MainForm and stopped
// when the form closes.
public sealed class MqttEventsClient : IAsyncDisposable
{
    private readonly MqttSettings _settings;
    private readonly IMqttClient _client;
    private readonly HashSet<string> _panels = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private long _seq;
    private CancellationTokenSource? _cts;
    private Task? _runLoop;

    public MqttEventsClient(MqttSettings settings)
    {
        _settings = settings;
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageAsync;
    }

    public bool IsConnected => _client.IsConnected;
    public string Broker => $"{_settings.Broker}:{_settings.Port}";
    public string TopicFilter => $"{_settings.TopicPrefix}/#";
    public string? LastError { get; private set; }
    public DateTime? LastMessageLocal { get; private set; }

    // Raised on a background thread — subscribers (MainForm) must marshal
    // back onto the UI thread before touching any control.
    public event Action? StateChanged;
    public event Action<DraxEventRow>? EventReceived;

    public IReadOnlyList<string> Panels
    {
        get { lock (_lock) return _panels.OrderBy(p => p).ToList(); }
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _runLoop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_settings.Broker, _settings.Port)
            .WithClientId($"AmxEventsFormPoc-{Environment.ProcessId}")
            .WithCleanSession()
            .Build();

        bool outageAnnounced = false;
        while (!ct.IsCancellationRequested)
        {
            if (!_client.IsConnected)
            {
                try
                {
                    await _client.ConnectAsync(options, ct);
                    await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(f => f.WithTopic(TopicFilter).WithAtMostOnceQoS())
                        .Build(), ct);
                    LastError = null;
                    outageAnnounced = false;
                    StateChanged?.Invoke();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    if (!outageAnnounced)
                    {
                        outageAnnounced = true;
                        StateChanged?.Invoke();
                    }
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            string topic = e.ApplicationMessage.Topic;
            string payload = e.ApplicationMessage.ConvertPayloadToString();
            LastMessageLocal = DateTime.Now;

            if (topic.EndsWith("/event", StringComparison.OrdinalIgnoreCase))
            {
                RememberPanel(topic);
                var row = DraxEventRow.Parse(Interlocked.Increment(ref _seq), payload);
                if (row != null) EventReceived?.Invoke(row);
            }
            else if (topic.EndsWith("/log", StringComparison.OrdinalIgnoreCase))
            {
                RememberPanel(topic);
            }
        }
        catch
        {
            // Best-effort, same as DraxView's MqttService — a malformed message
            // shouldn't take the listener down.
        }
        return Task.CompletedTask;
    }

    private void RememberPanel(string topic)
    {
        // drax/<panel>/event -> <panel>, same split DraxView uses.
        var parts = topic.Split('/');
        if (parts.Length < 3) return;
        bool added;
        lock (_lock) added = _panels.Add(parts[^2]);
        if (added) StateChanged?.Invoke();
    }

    // Same pipe-command strings the WinForms client sends (e.g. "SILENCE|0,0,0,0"),
    // published to drax/<panel>/cmd — the service subscribes there and forwards
    // them on to the real panel connection.
    public async Task<bool> SendCommandAsync(string panel, string command)
    {
        if (!_client.IsConnected || string.IsNullOrWhiteSpace(panel)) return false;
        try
        {
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic($"{_settings.TopicPrefix}/{panel.Trim().ToLowerInvariant()}/cmd")
                .WithPayload(command)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                .Build();
            await _client.PublishAsync(msg);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        try { if (_runLoop != null) await _runLoop; } catch { /* cancellation is expected */ }
        try { if (_client.IsConnected) await _client.DisconnectAsync(); } catch { /* best effort */ }
        _client.Dispose();
        _cts?.Dispose();
    }
}
