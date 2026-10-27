using System.Text.Json;
using SnmpBrowser.Services;

namespace SnmpBrowser.Plugins;

/// <summary>Un'esecuzione periodica avviata dall'utente.</summary>
public sealed class ScheduleRun
{
    public required PluginInstance Plugin { get; init; }
    public required PluginSchedule Schedule { get; init; }
    public required SnmpSettings? Settings { get; init; }
    public required string Host { get; init; }
    public required string Port { get; init; }
    public required string Version { get; init; }
    public required CancellationTokenSource Cts { get; init; }

    public int Runs { get; set; }
    public string Status { get; set; } = "in partenza...";
}

/// <summary>
/// Esegue a intervalli regolari le funzioni registrate con app.registerSchedule.
/// Parte solo quando l'utente lo chiede, vive finche' l'app e' aperta e si ferma da sola
/// se il plugin viene spento/rimosso o dopo 5 errori consecutivi.
/// </summary>
public sealed class PluginScheduler
{
    public static PluginScheduler Instance { get; } = new();

    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly object _gate = new();
    private readonly Dictionary<string, ScheduleRun> _runs = new();

    private PluginScheduler()
    {
    }

    /// <summary>Cambia stato o esito di un'esecuzione. Puo' arrivare da qualsiasi thread.</summary>
    public event Action? Changed;

    private static string KeyOf(PluginInstance plugin, string scheduleId) => plugin.Key + "|" + scheduleId;

    public IReadOnlyList<ScheduleRun> Running
    {
        get
        {
            lock (_gate) return _runs.Values.ToList();
        }
    }

    public ScheduleRun? Find(PluginInstance plugin, string scheduleId)
    {
        lock (_gate) return _runs.GetValueOrDefault(KeyOf(plugin, scheduleId));
    }

    public void Start(PluginInstance plugin, PluginSchedule schedule, SnmpSettings? settings, string host, string port, string version)
    {
        ScheduleRun run;
        lock (_gate)
        {
            var key = KeyOf(plugin, schedule.Id);
            if (_runs.ContainsKey(key)) return;

            run = new ScheduleRun
            {
                Plugin = plugin,
                Schedule = schedule,
                Settings = settings,
                Host = host,
                Port = port,
                Version = version,
                Cts = new CancellationTokenSource(),
            };
            _runs[key] = run;
        }

        Changed?.Invoke();
        _ = Task.Run(() => LoopAsync(run));
    }

    public void Stop(PluginInstance plugin, string scheduleId)
    {
        ScheduleRun? run;
        lock (_gate) _runs.TryGetValue(KeyOf(plugin, scheduleId), out run);
        run?.Cts.Cancel();
    }

    /// <summary>Ferma tutte le esecuzioni di un plugin (o di tutti, se null).</summary>
    public void StopAll(PluginInstance? plugin = null)
    {
        List<ScheduleRun> targets;
        lock (_gate) targets = _runs.Values.Where(r => plugin is null || r.Plugin == plugin).ToList();
        foreach (var run in targets) run.Cts.Cancel();
    }

    private async Task LoopAsync(ScheduleRun run)
    {
        var token = run.Cts.Token;
        var failures = 0;

        try
        {
            while (!token.IsCancellationRequested && run.Plugin.State == PluginState.Active)
            {
                run.Runs++;
                var context = JsonSerializer.Serialize(new
                {
                    host = run.Host,
                    port = run.Port,
                    version = run.Version,
                    run = run.Runs,
                    time = DateTime.Now.ToString("o"),
                }, Camel);

                var result = await run.Plugin.CallAsync("schedule", run.Schedule.Id, context, run.Settings, unattended: true);
                if (token.IsCancellationRequested) break;

                if (result.Ok)
                {
                    failures = 0;
                    run.Status = $"ultima esecuzione {DateTime.Now:HH:mm:ss}{Summary(result.Json)}";
                }
                else
                {
                    failures++;
                    run.Status = $"errore: {result.Error}";
                    if (failures >= 5)
                    {
                        run.Status += " - fermato dopo 5 errori consecutivi";
                        break;
                    }
                }
                Changed?.Invoke();

                await Task.Delay(TimeSpan.FromSeconds(run.Schedule.EverySeconds), token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_gate) _runs.Remove(KeyOf(run.Plugin, run.Schedule.Id));
            run.Cts.Dispose();
            Changed?.Invoke();
        }
    }

    /// <summary>Se lo script restituisce un testo breve lo mostro accanto all'orario.</summary>
    private static string Summary(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "null");
            if (doc.RootElement.ValueKind != JsonValueKind.String) return "";
            var text = doc.RootElement.GetString() ?? "";
            if (text.Length > 80) text = text[..80] + "...";
            return text.Length == 0 ? "" : " - " + text.ReplaceLineEndings(" ");
        }
        catch
        {
            return "";
        }
    }
}
