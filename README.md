# SNMP Browser

Browser SNMP per Windows (WPF, .NET 8) basato su [SharpSnmpLib](https://github.com/lextudio/sharpsnmplib).

## Funzioni
- Get e Walk (sottoalbero) su un OID, SNMP v1 / v2c
- Albero OID + tabella risultati (nome MIB-II, OID, tipo, valore)
- Set con conferma (Integer32, OctetString, Gauge32, TimeTicks, IpAddress, ObjectIdentifier)

## Avvio
```
cd SnmpBrowser
dotnet run
```
Oppure apri `SnmpBrowser.csproj` in Visual Studio 2022.

## Idee future
SNMPv3, caricamento MIB (file .mib), GetBulk, tasto Stop per i walk lunghi, export CSV.


## Plugin

Script JavaScript (motore Jint, isolato: niente file, rete o programmi) distribuiti come i MIB:
repository centrale di submodule (`snmpbrowser-plugins`), versioni = tag, sorgenti personali anche per i plugin.

Un plugin e' una cartella con `plugin.json` e uno script:

    {
      "id": "mio-plugin", "name": "Il mio plugin", "author": "Io", "description": "...",
      "apiVersion": 1, "main": "main.js", "permissions": ["snmp.read"]
    }

API disponibili nello script:

| Funzione | Cosa fa |
|---|---|
| `app.registerAction({id,title,run(ctx)})` | voce nel menu Plugin; il risultato (testo o tabella) si apre in una finestra |
| `app.registerPanel({id,title,render(ctx)})` | come sopra, con pulsante Aggiorna |
| `app.registerExporter({id,title,extension,export(ctx)})` | formato nel menu Esporta; restituisce il testo del file |
| `app.registerDecoder({id,title,oidPrefix,decode(entry)})` | cambia il valore mostrato; `null` = nessuna modifica |
| `snmp.get(oid)`, `snmp.walk(oid)` | `{ok,error,entries:[{oid,name,type,value,raw}]}` (permesso `snmp.read`) |
| `snmp.set(oid,type,value)` | scrittura, ogni volta con conferma dell'utente (permesso `snmp.write`) |
| `mib.resolve(nome)`, `mib.name(oid)` | conversione nome <-> OID |

`ctx` contiene `host`, `port`, `version`, `oid`, `selected` e `entries` (risultati visibili).
Risultato tabellare: `{type:"table", columns:[...], rows:[[...]]}`.

Sicurezza: un plugin installato resta spento finche' non lo approvi (autore, permessi, SHA-256, codice).
Se il codice cambia (nuova versione) serve una nuova approvazione. Limiti: 20 s per esecuzione, 64 MB, 300 chiamate SNMP per esecuzione.
Le approvazioni sono in `%AppData%\SnmpBrowser\plugins-trust.json`.
