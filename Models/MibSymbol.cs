namespace SnmpBrowser.Models;

/// <summary>Un simbolo definito in una MIB (OBJECT-TYPE, OBJECT IDENTIFIER, NOTIFICATION-TYPE...).</summary>
public sealed class MibSymbol
{
    public string Name { get; init; } = "";
    public string Module { get; init; } = "";
    public string Oid { get; init; } = "";
    public string Kind { get; init; } = "";
    public string? Syntax { get; init; }
    public string? Access { get; init; }
    public string? Status { get; init; }
    public string? Units { get; init; }
    public string? Description { get; init; }
    public IReadOnlyDictionary<int, string> Enums { get; init; } = new Dictionary<int, string>();
}

/// <summary>Riga mostrata nella finestra di gestione MIB.</summary>
public sealed class MibModuleInfo
{
    public string Name { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string FileName => Path.GetFileName(FilePath);
    public int SymbolCount { get; init; }
    public string StatusText { get; init; } = "OK";
    public bool IsOk { get; init; } = true;
}
