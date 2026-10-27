namespace SnmpBrowser.Plugins;

public enum PluginState
{
    /// <summary>Installato ma mai approvato (o il codice e' cambiato): non viene eseguito.</summary>
    NeedsApproval,

    /// <summary>Approvato ma spento dall'utente.</summary>
    Disabled,

    Active,
    Error,
}

/// <summary>Contenuto di plugin.json.</summary>
public sealed class PluginManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public int ApiVersion { get; set; } = 1;
    public string Main { get; set; } = "main.js";
    public List<string> Permissions { get; set; } = new();

    /// <summary>Broker MQTT a cui il plugin puo' inviare dati (richiesto dal permesso mqtt.publish).</summary>
    public PluginMqtt? Mqtt { get; set; }
}

public sealed class PluginMqtt
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 1883;
    public bool Tls { get; set; }
}

public sealed record PluginAction(string Id, string Title);
public sealed record PluginPanel(string Id, string Title);
public sealed record PluginExporter(string Id, string Title, string Extension);
public sealed record PluginDecoder(string Id, string Title, string OidPrefix);
public sealed record PluginSchedule(string Id, string Title, int EverySeconds);

/// <summary>Esito di una chiamata a un plugin: Json e' il valore restituito dallo script, gia' serializzato.</summary>
public sealed record PluginCallResult(string? Json, string? Error)
{
    public bool Ok => Error is null;
}
