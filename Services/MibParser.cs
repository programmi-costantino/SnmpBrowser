using System.Text;
using System.Text.RegularExpressions;

namespace SnmpBrowser.Services;

internal sealed class RawDef
{
    public string Name = "";
    public string Kind = "";
    public List<string> Tokens = new();
    public string? Syntax, Access, Status, Units, Description;
    public Dictionary<int, string> Enums = new();
}

internal sealed class ParsedModule
{
    public string Name = "";
    public string FilePath = "";
    public string? Source;
    public List<RawDef> Defs = new();
    public List<string> Imports = new();
}

/// <summary>
/// Parser leggero per MIB SMIv1/SMIv2: estrae i nomi con il loro OID, tipo, accesso, descrizione ed enumerazioni.
/// Non e' un compilatore ASN.1 completo: legge solo cio' che serve a navigare e descrivere gli OID.
/// </summary>
internal static class MibParser
{
    private static readonly Regex HeaderRx = new(
        @"(?<name>[A-Za-z][A-Za-z0-9-]*)\s+DEFINITIONS\s*(?:(?:IMPLICIT|EXPLICIT|AUTOMATIC)\s+TAGS\s*)?::=\s*BEGIN",
        RegexOptions.Compiled);

    private static readonly Regex ImportsRx = new(@"\bIMPORTS\b(?<b>.*?);", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex FromRx = new(@"\bFROM\s+(?<m>[A-Za-z][A-Za-z0-9-]*)", RegexOptions.Compiled);

    private static readonly Regex OidIdRx = new(
        @"(?<![A-Za-z0-9_-])(?<name>[a-z][A-Za-z0-9_-]*)\s+OBJECT\s+IDENTIFIER\s*::=\s*\{(?<val>[^{}]*)\}",
        RegexOptions.Compiled);

    private static readonly Regex MacroRx = new(
        @"(?<![A-Za-z0-9_-])(?<name>[a-z][A-Za-z0-9_-]*)\s+(?<kind>OBJECT-TYPE|MODULE-IDENTITY|OBJECT-IDENTITY|NOTIFICATION-TYPE|OBJECT-GROUP|NOTIFICATION-GROUP|MODULE-COMPLIANCE|AGENT-CAPABILITIES)\b(?<body>.*?)::=\s*\{(?<val>[^{}]*)\}",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex TrapRx = new(
        @"(?<![A-Za-z0-9_-])(?<name>[a-z][A-Za-z0-9_-]*)\s+TRAP-TYPE\s+ENTERPRISE\s+(?<ent>[A-Za-z0-9_.()-]+)(?<body>.*?)::=\s*(?<num>\d+)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex SyntaxRx = new(
        @"\bSYNTAX\s+(?<s>.*?)(?=\s+(?:UNITS|MAX-ACCESS|ACCESS|STATUS|DESCRIPTION|REFERENCE|INDEX|AUGMENTS|DEFVAL)\b|\s*$)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex AccessRx = new(@"\b(?:MAX-ACCESS|ACCESS)\s+(?<a>[a-z-]+)", RegexOptions.Compiled);
    private static readonly Regex StatusRx = new(@"\bSTATUS\s+(?<a>[a-z-]+)", RegexOptions.Compiled);
    private static readonly Regex UnitsRx = new(@"\bUNITS\s+""#(?<i>\d+)""", RegexOptions.Compiled);
    private static readonly Regex DescRx = new(@"\bDESCRIPTION\s+""#(?<i>\d+)""", RegexOptions.Compiled);
    private static readonly Regex EnumRx = new(@"(?<n>[A-Za-z][A-Za-z0-9-]*)\s*\(\s*(?<v>-?\d+)\s*\)", RegexOptions.Compiled);

    public static List<ParsedModule> Parse(string text, string path)
    {
        var strings = new List<string>();
        var clean = Clean(text, strings);

        var headers = HeaderRx.Matches(clean);
        var modules = new List<ParsedModule>();

        for (var i = 0; i < headers.Count; i++)
        {
            var start = headers[i].Index + headers[i].Length;
            var end = i + 1 < headers.Count ? headers[i + 1].Index : clean.Length;
            var body = clean[start..end];

            var module = new ParsedModule { Name = headers[i].Groups["name"].Value, FilePath = path };

            var imports = ImportsRx.Match(body);
            if (imports.Success)
            {
                foreach (Match m in FromRx.Matches(imports.Value))
                    module.Imports.Add(m.Groups["m"].Value);
                body = body.Remove(imports.Index, imports.Length);
            }

            foreach (Match m in OidIdRx.Matches(body))
            {
                module.Defs.Add(new RawDef
                {
                    Name = m.Groups["name"].Value,
                    Kind = "OBJECT IDENTIFIER",
                    Tokens = Tokenize(m.Groups["val"].Value),
                });
            }

            foreach (Match m in MacroRx.Matches(body))
                module.Defs.Add(BuildMacroDef(m, strings));

            foreach (Match m in TrapRx.Matches(body))
            {
                module.Defs.Add(new RawDef
                {
                    Name = m.Groups["name"].Value,
                    Kind = "TRAP-TYPE",
                    Tokens = new List<string> { m.Groups["ent"].Value, "0", m.Groups["num"].Value },
                    Description = Str(DescRx.Match(m.Groups["body"].Value), strings),
                });
            }

            modules.Add(module);
        }
        return modules;
    }

    private static RawDef BuildMacroDef(Match m, List<string> strings)
    {
        var body = m.Groups["body"].Value;
        var def = new RawDef
        {
            Name = m.Groups["name"].Value,
            Kind = m.Groups["kind"].Value,
            Tokens = Tokenize(m.Groups["val"].Value),
            Description = Str(DescRx.Match(body), strings),
            Units = Str(UnitsRx.Match(body), strings),
        };

        var syntax = SyntaxRx.Match(body);
        if (syntax.Success)
        {
            def.Syntax = Regex.Replace(syntax.Groups["s"].Value.Trim(), @"\s+", " ");

            var open = def.Syntax.IndexOf('{');
            var close = def.Syntax.LastIndexOf('}');
            if (open >= 0 && close > open)
            {
                foreach (Match e in EnumRx.Matches(def.Syntax[open..close]))
                    def.Enums[int.Parse(e.Groups["v"].Value)] = e.Groups["n"].Value;
            }
        }

        var access = AccessRx.Match(body);
        if (access.Success) def.Access = access.Groups["a"].Value;

        var status = StatusRx.Match(body);
        if (status.Success) def.Status = status.Groups["a"].Value;

        return def;
    }

    private static string? Str(Match m, List<string> strings)
    {
        if (!m.Success) return null;
        var index = int.Parse(m.Groups["i"].Value);
        return Regex.Replace(strings[index].Trim(), @"\s+", " ");
    }

    /// <summary>Spezza "{ iso org(3) dod(6) 1 }" in ["iso", "org(3)", "dod(6)", "1"].</summary>
    private static List<string> Tokenize(string value)
    {
        value = Regex.Replace(value, @"\s*\(\s*", "(");
        value = Regex.Replace(value, @"\s*\)", ")");
        return value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>Toglie i commenti (--) e sostituisce le stringhe "..." con segnaposto "#n".</summary>
    private static string Clean(string text, List<string> strings)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '"')
            {
                var s = new StringBuilder();
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            s.Append('"');
                            i += 2;
                            continue;
                        }
                        break;
                    }
                    s.Append(text[i]);
                    i++;
                }
                i++; // virgolette di chiusura
                strings.Add(s.ToString());
                sb.Append("\"#").Append(strings.Count - 1).Append('"');
                continue;
            }

            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                i += 2;
                while (i < text.Length && text[i] != '\n' && text[i] != '\r')
                {
                    if (text[i] == '-' && i + 1 < text.Length && text[i + 1] == '-')
                    {
                        i += 2;
                        break;
                    }
                    i++;
                }
                sb.Append(' ');
                continue;
            }

            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
