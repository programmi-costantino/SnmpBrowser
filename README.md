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
