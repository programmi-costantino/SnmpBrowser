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
}

public sealed record PluginAction(string Id, string Title);
public sealed record PluginPanel(string Id, string Title);
public sealed record PluginExporter(string Id, string Title, string Extension);
public sealed record PluginDecoder(string Id, string Title, string OidPrefix);

/// <summary>Esito di una chiamata a un plugin: Json e' il valore restituito dallo script, gia' serializzato.</summary>
public sealed record PluginCallResult(string? Json, string? Error)
{
    public bool Ok => Error is null;
}
