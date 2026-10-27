using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace SnmpBrowser.Services;

/// <summary>Validazione degli input. Ogni metodo restituisce null se il valore e' valido, altrimenti il messaggio d'errore.</summary>
public static class InputValidator
{
    private static readonly Regex HostLabel = new(@"^(?!-)[A-Za-z0-9-]{1,63}(?<!-)$", RegexOptions.Compiled);

    public static bool IsIPv4(string text)
    {
        var parts = text.Split('.');
        if (parts.Length != 4) return false;

        foreach (var part in parts)
        {
            if (part.Length is 0 or > 3) return false;
            if (!part.All(char.IsAsciiDigit)) return false;
            if (int.Parse(part, CultureInfo.InvariantCulture) > 255) return false;
        }
        return true;
    }

    public static string? Host(string text)
    {
        var host = text.Trim();
        if (host.Length == 0)
            return "Inserisci un indirizzo IP o un hostname.";

        // Solo cifre e punti: deve essere un IPv4 completo e corretto
        if (host.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            return IsIPv4(host)
                ? null
                : "Indirizzo IPv4 non valido: servono 4 numeri da 0 a 255 (es. 192.168.1.10).";
        }

        if (host.Contains(':'))
        {
            return IPAddress.TryParse(host, out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6
                ? null
                : "Indirizzo IPv6 non valido.";
        }

        if (host.Length > 253 || !host.Split('.').All(l => HostLabel.IsMatch(l)))
            return "Hostname non valido (ammessi lettere, numeri e trattini).";

        return null;
    }

    public static string? Port(string text) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
            ? null
            : "Porta non valida: inserisci un numero da 1 a 65535 (default 161).";

    public static string? Timeout(string text) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var ms) && ms is >= 100 and <= 60000
            ? null
            : "Timeout non valido: inserisci i millisecondi, da 100 a 60000.";

    // ---- SNMPv3 ----

    public static string? V3User(string text) =>
        text.Trim().Length is >= 1 and <= 32
            ? null
            : "SNMPv3: inserisci il nome utente (da 1 a 32 caratteri).";

    /// <summary>RFC 3414: le password SNMPv3 devono avere almeno 8 caratteri.</summary>
    public static string? V3Password(string text, string label) =>
        text.Length is >= 8 and <= 128
            ? null
            : $"SNMPv3: la password di {label} deve avere da 8 a 128 caratteri.";

    public static string? Oid(string text)
    {
        var oid = text.Trim().TrimStart('.');
        if (oid.Length == 0)
            return "Inserisci un OID (es. 1.3.6.1.2.1.1 oppure sysDescr.0).";

        // Nome simbolico (es. sysDescr.0, IF-MIB::ifDescr): deve essere noto a una MIB caricata
        if (oid.Any(char.IsLetter))
        {
            return MibStore.Instance.TryResolveText(oid, out _)
                ? null
                : "Nome OID sconosciuto: carica la MIB che lo definisce (pulsante MIB in alto).";
        }

        var parts = oid.Split('.');
        if (parts.Length < 2)
            return "L'OID deve avere almeno due numeri separati da punti.";

        var arcs = new uint[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0)
                return "OID non valido: c'e' un numero mancante (punti doppi o finale).";
            if (!uint.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out arcs[i]))
                return $"OID non valido: '{parts[i]}' non e' un numero.";
        }

        if (arcs[0] > 2)
            return "OID non valido: il primo numero deve essere 0, 1 o 2.";
        if (arcs[0] < 2 && arcs[1] > 39)
            return "OID non valido: il secondo numero deve essere al massimo 39.";

        return null;
    }

    public static string? SetValue(string type, string value)
    {
        var v = value.Trim();
        switch (type)
        {
            case "Integer32":
                return int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
                    ? null : "Valore non valido: serve un intero a 32 bit (-2147483648 ... 2147483647).";
            case "Gauge32":
            case "TimeTicks":
                return uint.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out _)
                    ? null : "Valore non valido: serve un intero positivo (0 ... 4294967295).";
            case "IpAddress":
                return IsIPv4(v) ? null : "Indirizzo IPv4 non valido (es. 192.168.1.10).";
            case "ObjectIdentifier":
                return Oid(v);
            default:
                return null; // OctetString: qualsiasi testo
        }
    }
}
