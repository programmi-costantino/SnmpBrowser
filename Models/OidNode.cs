using System.Collections.ObjectModel;

namespace SnmpBrowser.Models;

/// <summary>Nodo dell'albero OID mostrato a sinistra.</summary>
public sealed class OidNode
{
    public OidNode(string oid, string header)
    {
        Oid = oid;
        Header = header;
    }

    public string Oid { get; }
    public string Header { get; set; }
    public bool IsExpanded { get; set; }
    public ObservableCollection<OidNode> Children { get; } = new();
}
