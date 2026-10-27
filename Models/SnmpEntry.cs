namespace SnmpBrowser.Models;

/// <summary>Una riga di risultato SNMP (OID, nome noto, tipo e valore).</summary>
public sealed record SnmpEntry(string Oid, string Name, string Type, string Value);
