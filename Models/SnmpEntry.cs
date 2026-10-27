namespace SnmpBrowser.Models;

/// <summary>Una riga di risultato SNMP. Raw e' il valore grezzo, prima della decodifica con la MIB.</summary>
public sealed record SnmpEntry(string Oid, string Name, string Type, string Value, string? Raw = null);
