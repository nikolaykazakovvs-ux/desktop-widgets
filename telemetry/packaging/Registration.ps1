# Библиотека саморегистрации MCP-сервера widgets-bridge в клиентах. Dot-source: . "$PSScriptRoot\Registration.ps1"
# Правит только запись widgets-bridge в конфигурации клиента. JSON и TOML разбираются настоящими сканерами
# (System.Text.Json.Utf8JsonReader и структурный сканер TOML), правка - точечная вставка/удаление фрагмента по
# найденным позициям: остальные байты файла (чужие записи, форматирование, порядок ключей) не меняются.
Set-StrictMode -Version Latest

if (-not ('WbJson' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

public sealed class WbMember { public string Name; public int Start; public int ValueStart; public int End; }
public sealed class WbObject {
    public int Open; public int Close;
    public List<WbMember> Members = new List<WbMember>();
    public WbObject Child;
}
public static class WbJson {
    // Проверяет весь документ (один корневой объект, без комментариев и хвостовых запятых, без дублей ключей
    // в корне и в объекте key) и возвращает позиции (индексы байтов) корня и его дочернего объекта key.
    public static WbObject Scan(byte[] json, string key) {
        var opts = new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = 256 };
        var r = new Utf8JsonReader(json, opts);
        if (!r.Read() || r.TokenType != JsonTokenType.StartObject) throw new JsonException("root is not an object");
        var root = ReadObject(ref r, key, true);
        if (r.Read()) throw new JsonException("trailing content after the root object");
        return root;
    }
    static WbObject ReadObject(ref Utf8JsonReader r, string key, bool descend) {
        var o = new WbObject { Open = (int)r.TokenStartIndex };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (r.Read()) {
            if (r.TokenType == JsonTokenType.EndObject) { o.Close = (int)r.TokenStartIndex; return o; }
            string name = r.GetString();
            if (!seen.Add(name)) throw new JsonException("duplicate key in the scanned object");
            int start = (int)r.TokenStartIndex;
            r.Read();
            int vstart = (int)r.TokenStartIndex;
            bool target = descend && string.Equals(name, key, StringComparison.Ordinal);
            if (r.TokenType == JsonTokenType.StartObject || r.TokenType == JsonTokenType.StartArray) {
                if (target) {
                    if (r.TokenType != JsonTokenType.StartObject) throw new JsonException(key + " is not an object");
                    o.Child = ReadObject(ref r, key, false);
                } else r.Skip();
            } else if (target) throw new JsonException(key + " is not an object");
            o.Members.Add(new WbMember { Name = name, Start = start, ValueStart = vstart, End = (int)r.BytesConsumed });
        }
        throw new JsonException("unexpected end of document");
    }
}

public sealed class WbTable { public string[] Path; public bool IsArray; public int Start; public int End; public int ContentEnd; }
public sealed class WbKey { public string[] FullPath; public int Table; public string Value; }
public sealed class WbToml { public List<WbTable> Tables = new List<WbTable>(); public List<WbKey> Keys = new List<WbKey>(); }

public static class WbTomlScan {
    static FormatException Bad(string m) { return new FormatException("toml: " + m); }
    static int Eol(string t, int i) {
        if (t[i] == '\r') { i++; if (i < t.Length && t[i] == '\n') i++; return i; }
        return i + 1;
    }
    static int ToEol(string t, int i) { while (i < t.Length && t[i] != '\r' && t[i] != '\n') i++; return i; }
    static void Ws(string t, ref int i) { while (i < t.Length && (t[i] == ' ' || t[i] == '\t')) i++; }
    static void WsNlComments(string t, ref int i) {
        while (i < t.Length) {
            char c = t[i];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n') i++;
            else if (c == '#') i = ToEol(t, i);
            else break;
        }
    }
    static bool At(string t, int i, string s) { return string.CompareOrdinal(t, i, s, 0, s.Length) == 0; }

    public static WbToml Scan(string t) {
        var d = new WbToml(); int n = t.Length, i = 0;
        string[] cur = new string[0]; int curTable = -1;
        while (i < n) {
            int ls = i; Ws(t, ref i);
            if (i >= n) break;
            char c = t[i];
            if (c == '\r' || c == '\n') { i = Eol(t, i); continue; }
            if (c == '#') { i = ToEol(t, i); if (i < n) i = Eol(t, i); continue; }
            if (c == '[') {
                bool arr = i + 1 < n && t[i + 1] == '[';
                i += arr ? 2 : 1;
                var parts = KeyPath(t, ref i);
                Ws(t, ref i);
                if (i >= n || t[i] != ']') throw Bad("bad table header");
                i++;
                if (arr) { if (i >= n || t[i] != ']') throw Bad("bad array-of-tables header"); i++; }
                i = ToEol(t, i); if (i < n) i = Eol(t, i);
                d.Tables.Add(new WbTable { Path = parts, IsArray = arr, Start = ls, ContentEnd = i });
                cur = parts; curTable = d.Tables.Count - 1;
                continue;
            }
            var kp = KeyPath(t, ref i);
            Ws(t, ref i);
            if (i >= n || t[i] != '=') throw Bad("expected '=' after key");
            i++; Ws(t, ref i);
            int vs = i; int ve = Value(t, ref i);
            var full = new string[cur.Length + kp.Length];
            Array.Copy(cur, 0, full, 0, cur.Length); Array.Copy(kp, 0, full, cur.Length, kp.Length);
            d.Keys.Add(new WbKey { FullPath = full, Table = curTable, Value = t.Substring(vs, ve - vs) });
            i = ToEol(t, i); if (i < n) i = Eol(t, i);
            if (curTable >= 0) d.Tables[curTable].ContentEnd = i;
        }
        for (int k = 0; k < d.Tables.Count; k++) {
            int end = k + 1 < d.Tables.Count ? d.Tables[k + 1].Start : n;
            // Хвост между последней строкой с ключом и следующим заголовком: комментарии в нём принадлежат следующей таблице
            // (и пустые строки перед ними), поэтому диапазон таблицы заканчивается перед первой строкой-комментарием хвоста.
            int p = d.Tables[k].ContentEnd;
            while (p < end) {
                int q = p; Ws(t, ref q);
                if (q < end && t[q] == '#') { end = p; break; }
                p = ToEol(t, p); if (p < n) p = Eol(t, p);
            }
            d.Tables[k].End = end;
        }
        return d;
    }

    static bool BareChar(char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-'; }
    static string[] KeyPath(string t, ref int i) {
        var parts = new List<string>(); int n = t.Length;
        while (true) {
            Ws(t, ref i);
            if (i >= n) throw Bad("unexpected end in key");
            char c = t[i];
            if (c == '"') parts.Add(Basic(t, ref i));
            else if (c == '\'') parts.Add(Literal(t, ref i));
            else {
                int s = i; while (i < n && BareChar(t[i])) i++;
                if (i == s) throw Bad("empty or invalid key");
                parts.Add(t.Substring(s, i - s));
            }
            Ws(t, ref i);
            if (i < n && t[i] == '.') { i++; continue; }
            return parts.ToArray();
        }
    }
    static string Basic(string t, ref int i) {
        int n = t.Length; i++; var sb = new StringBuilder();
        while (true) {
            if (i >= n) throw Bad("unterminated string");
            char c = t[i];
            if (c == '\r' || c == '\n') throw Bad("newline in single-line string");
            if (c == '"') { i++; return sb.ToString(); }
            if (c == '\\') { i++; if (i >= n) throw Bad("bad escape"); sb.Append(Escape(t, ref i)); continue; }
            sb.Append(c); i++;
        }
    }
    static string Escape(string t, ref int i) {
        char e = t[i]; i++;
        switch (e) {
            case 'b': return "\b"; case 't': return "\t"; case 'n': return "\n"; case 'f': return "\f";
            case 'r': return "\r"; case '"': return "\""; case '\\': return "\\";
            case 'u': return Hex(t, ref i, 4);
            case 'U': return Hex(t, ref i, 8);
            default: throw Bad("bad escape");
        }
    }
    static string Hex(string t, ref int i, int len) {
        if (i + len > t.Length) throw Bad("bad unicode escape");
        int v;
        if (!int.TryParse(t.Substring(i, len), System.Globalization.NumberStyles.AllowHexSpecifier, null, out v)) throw Bad("bad unicode escape");
        i += len;
        try { return char.ConvertFromUtf32(v); } catch (ArgumentOutOfRangeException) { throw Bad("bad unicode escape"); }
    }
    static string Literal(string t, ref int i) {
        int n = t.Length; i++; int s = i;
        while (i < n && t[i] != '\'' && t[i] != '\r' && t[i] != '\n') i++;
        if (i >= n || t[i] != '\'') throw Bad("unterminated literal string");
        string r = t.Substring(s, i - s); i++; return r;
    }
    static void MultiBasic(string t, ref int i) {
        int n = t.Length; i += 3;
        while (true) {
            if (i >= n) throw Bad("unterminated multi-line string");
            if (t[i] == '\\') { i += 2; continue; }
            if (At(t, i, "\"\"\"")) { i += 3; int extra = 0; while (extra < 2 && i < n && t[i] == '"') { i++; extra++; } return; }
            i++;
        }
    }
    static void MultiLiteral(string t, ref int i) {
        int n = t.Length; i += 3;
        while (true) {
            if (i >= n) throw Bad("unterminated multi-line literal string");
            if (At(t, i, "'''")) { i += 3; int extra = 0; while (extra < 2 && i < n && t[i] == '\'') { i++; extra++; } return; }
            i++;
        }
    }
    static int Value(string t, ref int i) {
        int n = t.Length;
        if (i >= n) throw Bad("missing value");
        char c = t[i];
        if (c == '"') { if (At(t, i, "\"\"\"")) MultiBasic(t, ref i); else Basic(t, ref i); }
        else if (c == '\'') { if (At(t, i, "'''")) MultiLiteral(t, ref i); else Literal(t, ref i); }
        else if (c == '[') {
            i++;
            while (true) {
                WsNlComments(t, ref i);
                if (i >= n) throw Bad("unterminated array");
                if (t[i] == ']') { i++; break; }
                Value(t, ref i);
                WsNlComments(t, ref i);
                if (i < n && t[i] == ',') { i++; continue; }
                if (i < n && t[i] == ']') { i++; break; }
                throw Bad("bad array");
            }
        }
        else if (c == '{') {
            i++;
            while (true) {
                WsNlComments(t, ref i);
                if (i < n && t[i] == '}') { i++; break; }
                KeyPath(t, ref i); Ws(t, ref i);
                if (i >= n || t[i] != '=') throw Bad("bad inline table");
                i++; Ws(t, ref i);
                Value(t, ref i);
                WsNlComments(t, ref i);
                if (i < n && t[i] == ',') { i++; continue; }
                if (i < n && t[i] == '}') { i++; break; }
                throw Bad("bad inline table");
            }
        }
        else {
            int s = i;
            while (i < n && " \t\r\n,]}#".IndexOf(t[i]) < 0) i++;
            if (i == s) throw Bad("missing value");
            // Дата-время с пробелом (1979-05-27 07:32:00Z) - одно значение: пробел между датой и временем допустим.
            if (i - s == 10 && i + 3 < n && t[i] == ' ' && char.IsDigit(t[i + 1]) && char.IsDigit(t[i + 2]) && t[i + 3] == ':'
                && char.IsDigit(t[s]) && char.IsDigit(t[s + 1]) && char.IsDigit(t[s + 2]) && char.IsDigit(t[s + 3]) && t[s + 4] == '-' && t[s + 7] == '-') {
                i++;
                while (i < n && " \t\r\n,]}#".IndexOf(t[i]) < 0) i++;
            }
        }
        return i;
    }
    // Простое строковое значение (basic или literal, не многострочное); иначе null.
    public static string DecodeString(string value) {
        string v = value.Trim();
        if (v.Length >= 2 && v[0] == '"' && !At(v, 0, "\"\"\"")) { int i = 0; try { string r = Basic(v, ref i); return i == v.Length ? r : null; } catch (FormatException) { return null; } }
        if (v.Length >= 2 && v[0] == '\'' && !At(v, 0, "'''")) { int i = 0; try { string r = Literal(v, ref i); return i == v.Length ? r : null; } catch (FormatException) { return null; } }
        return null;
    }
}
'@
}

# ---------- Общие утилиты ----------

$script:Utf8Strict = New-Object Text.UTF8Encoding($false, $true)
$script:Utf8 = New-Object Text.UTF8Encoding($false)

function Get-NewLine([string]$Text) { if ($Text.Contains("`r`n")) { return "`r`n" } else { return "`n" } }

# Guard (scriptblock) вызывается после записи временного файла непосредственно перед заменой; $false - замена не выполняется.
# Возвращает $true, если файл заменён, и $false, если Guard отказал.
function Write-AtomicBytes([string]$Path, [byte[]]$Bytes, [scriptblock]$Guard = $null) {
    $tmp = "$Path.tmp-wb-$([guid]::NewGuid().ToString('N'))"
    [IO.File]::WriteAllBytes($tmp, $Bytes)
    try {
        if (Test-Path -LiteralPath $Path) {
            # Guard перед КАЖДОЙ попыткой Replace: пауза после IOException - окно, в котором клиент мог сохранить файл
            # (находка ревью N1); отказ guard возвращает $false, вызывающий код перечитывает файл и повторяет правку.
            for ($a = 1; ; $a++) {
                if ($Guard -and -not [bool](& $Guard)) { return $false }
                try { [IO.File]::Replace($tmp, $Path, [NullString]::Value); break }
                catch [IO.IOException] { if ($a -ge 5) { throw }; Start-Sleep -Milliseconds 200 }
            }
        } else {
            if ($Guard -and -not [bool](& $Guard)) { return $false }
            [IO.File]::Move($tmp, $Path)
        }
        return $true
    } finally { if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue } }
}

# Резервная копия рядом: <файл>.bak-widgets-bridge-YYYYMMDD (при повторе в тот же день - суффикс -2, -3...).
# Копия создаётся без перезаписи (File.Copy overwrite=false): занятое параллельным запуском имя пропускается.
# Копии содержат тот же приватный конфиг и сами не удаляются.
function New-ConfigBackup([string]$Path) {
    $stamp = Get-Date -Format yyyyMMdd
    for ($n = 1; $n -le 1000; $n++) {
        $dest = if ($n -eq 1) { "$Path.bak-widgets-bridge-$stamp" } else { "$Path.bak-widgets-bridge-$stamp-$n" }
        try { [IO.File]::Copy($Path, $dest, $false); return $dest }
        catch [IO.IOException] { if (-not (Test-Path -LiteralPath $dest)) { throw } }
    }
    throw 'Cannot choose a free backup file name.'
}

function Split-Bom([byte[]]$Bytes) {
    if ($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xEF -and $Bytes[1] -eq 0xBB -and $Bytes[2] -eq 0xBF) {
        $rest = [byte[]]::new($Bytes.Length - 3); [Array]::Copy($Bytes, 3, $rest, 0, $rest.Length)
        return @{ bom = $true; bytes = $rest }
    }
    return @{ bom = $false; bytes = $Bytes }
}
function Join-Bom([bool]$Bom, [byte[]]$Bytes) {
    if (-not $Bom) { return ,$Bytes }
    $res = [byte[]]::new($Bytes.Length + 3); $res[0] = 0xEF; $res[1] = 0xBB; $res[2] = 0xBF; [Array]::Copy($Bytes, 0, $res, 3, $Bytes.Length)
    return ,$res
}

# ---------- JSON ----------

function ConvertTo-JsonLiteral([string]$S) {
    $sb = New-Object Text.StringBuilder
    [void]$sb.Append('"')
    foreach ($ch in $S.ToCharArray()) {
        if ($ch -eq [char]'"') { [void]$sb.Append('\"') }
        elseif ($ch -eq [char]'\') { [void]$sb.Append('\\') }
        elseif ([int]$ch -lt 0x20) { [void]$sb.AppendFormat('\u{0:x4}', [int]$ch) }
        else { [void]$sb.Append($ch) }
    }
    [void]$sb.Append('"')
    return $sb.ToString()
}

function ConvertTo-JsonText($Value, [string]$Indent, [string]$Step, [string]$Nl, [bool]$Compact) {
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [bool]) { if ($Value) { return 'true' } else { return 'false' } }
    if ($Value -is [string]) { return (ConvertTo-JsonLiteral $Value) }
    if ($Value -is [int] -or $Value -is [long] -or $Value -is [double]) { return [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0}', $Value) }
    if ($Value -is [System.Collections.IDictionary]) {
        if ($Value.Count -eq 0) { return '{}' }
        $inner = $Indent + $Step
        $items = @(foreach ($k in $Value.Keys) { (ConvertTo-JsonLiteral ([string]$k)) + ': ' + (ConvertTo-JsonText $Value[$k] $inner $Step $Nl $Compact) })
        if ($Compact) { return '{' + ($items -join ', ') + '}' }
        return '{' + $Nl + $inner + ($items -join (',' + $Nl + $inner)) + $Nl + $Indent + '}'
    }
    if ($Value -is [System.Collections.IEnumerable]) {
        $items = @(foreach ($x in $Value) { ConvertTo-JsonText $x $Indent $Step $Nl $true })
        if ($items.Count -eq 0) { return '[]' }
        return '[' + ($items -join ', ') + ']'
    }
    throw "Unsupported JSON value type: $($Value.GetType().FullName)"
}

function Edit-Bytes([byte[]]$Bytes, [int]$Start, [int]$End, [string]$Insert) {
    $ins = $script:Utf8.GetBytes($Insert)
    $res = [byte[]]::new($Bytes.Length - ($End - $Start) + $ins.Length)
    [Array]::Copy($Bytes, 0, $res, 0, $Start)
    [Array]::Copy($ins, 0, $res, $Start, $ins.Length)
    [Array]::Copy($Bytes, $End, $res, $Start + $ins.Length, $Bytes.Length - $End)
    return ,$res
}

# Ведущие пробелы строки, содержащей позицию Pos.
function Get-LineIndent([byte[]]$B, [int]$Pos) {
    $s = $Pos; while ($s -gt 0 -and $B[$s - 1] -ne 10) { $s-- }
    $e = $s; while ($e -lt $B.Length -and ($B[$e] -eq 32 -or $B[$e] -eq 9)) { $e++ }
    return [Text.Encoding]::ASCII.GetString($B, $s, $e - $s)
}

function Test-ByteRangeHasNewline([byte[]]$B, [int]$From, [int]$To) {
    for ($i = $From; $i -lt $To; $i++) { if ($B[$i] -eq 10) { return $true } }
    return $false
}

# Вставляет член "name": value в объект, найденный сканером (Open/Close/Members).
function Add-JsonMember([byte[]]$B, $Obj, [string]$Name, $Value, [string]$Nl) {
    $lit = ConvertTo-JsonLiteral $Name
    if ($Obj.Members.Count -eq 0) {
        $closeIndent = Get-LineIndent $B $Obj.Open
        $step = if ($closeIndent.Contains("`t")) { "`t" } else { '  ' }
        $memberIndent = $closeIndent + $step
        $text = $lit + ': ' + (ConvertTo-JsonText $Value $memberIndent $step $Nl $false)
        return (Edit-Bytes $B ($Obj.Open + 1) $Obj.Close ($Nl + $memberIndent + $text + $Nl + $closeIndent))
    }
    $first = $Obj.Members[0]; $last = $Obj.Members[$Obj.Members.Count - 1]
    if (Test-ByteRangeHasNewline $B ($Obj.Open + 1) $first.Start) {
        $memberIndent = Get-LineIndent $B $first.Start
        $step = if ($memberIndent.Contains("`t")) { "`t" } else { '  ' }
        $text = $lit + ': ' + (ConvertTo-JsonText $Value $memberIndent $step $Nl $false)
        return (Edit-Bytes $B $last.End $last.End (',' + $Nl + $memberIndent + $text))
    }
    $text = $lit + ': ' + (ConvertTo-JsonText $Value '' '' $Nl $true)
    return (Edit-Bytes $B $last.End $last.End (', ' + $text))
}

function Get-JsonMemberText([byte[]]$B, $M) { return $script:Utf8.GetString($B, $M.ValueStart, $M.End - $M.ValueStart) }

# Соответствие существующего значения желаемому: все желаемые ключи совпадают (лишние ключи пользователя допустимы).
function Test-JsonEntryMatches([string]$ValueText, $Desired) {
    try { $doc = [Text.Json.JsonDocument]::Parse($ValueText) } catch { return $false }
    try {
        $root = $doc.RootElement
        if ($root.ValueKind -ne 'Object') { return $false }
        foreach ($k in $Desired.Keys) {
            $el = [Text.Json.JsonElement]::new()
            if (-not $root.TryGetProperty([string]$k, [ref]$el)) { return $false }
            $want = $Desired[$k]
            if ($want -is [string]) {
                if ($el.ValueKind -ne 'String') { return $false }
                # command - путь Windows: регистр не важен (как при снятии записи); прочие строки сравниваются точно
                if ($k -ceq 'command') { if ($el.GetString() -ine $want) { return $false } } elseif ($el.GetString() -cne $want) { return $false }
            }
            elseif ($want -is [int] -or $want -is [long]) { if ($el.ValueKind -ne 'Number' -or $el.GetDouble() -ne [double]$want) { return $false } }
            elseif ($want -is [System.Collections.IEnumerable]) {
                $wantList = @($want)
                if ($el.ValueKind -ne 'Array' -or $el.GetArrayLength() -ne $wantList.Count) { return $false }
                $idx = 0
                foreach ($item in $el.EnumerateArray()) { if ($item.ValueKind -ne 'String' -or $item.GetString() -cne [string]$wantList[$idx]) { return $false }; $idx++ }
            } else { return $false }
        }
        return $true
    } finally { $doc.Dispose() }
}

function Get-JsonEntryCommand([string]$ValueText) {
    try { $doc = [Text.Json.JsonDocument]::Parse($ValueText) } catch { return $null }
    try {
        $el = [Text.Json.JsonElement]::new()
        if ($doc.RootElement.ValueKind -eq 'Object' -and $doc.RootElement.TryGetProperty('command', [ref]$el) -and $el.ValueKind -eq 'String') { return $el.GetString() }
        return $null
    } finally { $doc.Dispose() }
}

# Возвращает @{ status; bytes; changed }. Status: registered | updated | already | removed | not_registered | kept_other_install.
function Edit-JsonMcpServers {
    param([byte[]]$Bytes, [string]$Name, $Desired, [string]$Launcher, [bool]$Remove, [bool]$AnyInstall, [string]$Key = 'mcpServers')
    $split = Split-Bom $Bytes
    $b = $split.bytes
    $text = $script:Utf8Strict.GetString($b)
    $fresh = ($text.Trim().Length -eq 0)
    if ($fresh) { $b = $script:Utf8.GetBytes('{}'); $text = '{}' }
    $nl = Get-NewLine $text
    $scan = [WbJson]::Scan($b, $Key)
    $servers = $scan.Child
    $member = $null; $memberIdx = -1
    if ($servers) {
        for ($i = 0; $i -lt $servers.Members.Count; $i++) { if ($servers.Members[$i].Name -ceq $Name) { $member = $servers.Members[$i]; $memberIdx = $i } }
    }
    if ($Remove) {
        if (-not $member) { return @{ status = 'not_registered'; bytes = $Bytes; changed = $false } }
        if (-not $AnyInstall) {
            $cmd = Get-JsonEntryCommand (Get-JsonMemberText $b $member)
            if ($null -eq $cmd -or $cmd -ine $Launcher) { return @{ status = 'kept_other_install'; bytes = $Bytes; changed = $false } }
        }
        $count = $servers.Members.Count
        if ($count -eq 1) { $nb = Edit-Bytes $b ($servers.Open + 1) $servers.Close '' }
        elseif ($memberIdx -lt $count - 1) { $nb = Edit-Bytes $b $member.Start $servers.Members[$memberIdx + 1].Start '' }
        else { $nb = Edit-Bytes $b $servers.Members[$memberIdx - 1].End $member.End '' }
        return @{ status = 'removed'; bytes = (Join-Bom $split.bom $nb); changed = $true }
    }
    if ($member) {
        if (Test-JsonEntryMatches (Get-JsonMemberText $b $member) $Desired) { return @{ status = 'already'; bytes = $Bytes; changed = $false } }
        $indent = Get-LineIndent $b $member.Start
        $step = if ($indent.Contains("`t")) { "`t" } else { '  ' }
        $nb = Edit-Bytes $b $member.ValueStart $member.End (ConvertTo-JsonText $Desired $indent $step $nl $false)
        return @{ status = 'updated'; bytes = (Join-Bom $split.bom $nb); changed = $true }
    }
    if ($servers) { $nb = Add-JsonMember $b $servers $Name $Desired $nl }
    else { $nb = Add-JsonMember $b $scan $Key ([ordered]@{ $Name = $Desired }) $nl }
    if ($fresh) { $nb = Edit-Bytes $nb $nb.Length $nb.Length $nl }
    return @{ status = 'registered'; bytes = (Join-Bom $split.bom $nb); changed = $true }
}

# ---------- TOML ----------

function Test-PathPrefix([string[]]$Path, [string[]]$Prefix) {
    if ($Path.Count -lt $Prefix.Count) { return $false }
    for ($i = 0; $i -lt $Prefix.Count; $i++) { if ($Path[$i] -cne $Prefix[$i]) { return $false } }
    return $true
}

function ConvertTo-TomlString([string]$S) {
    if (-not $S.Contains("'") -and -not $S.Contains("`r") -and -not $S.Contains("`n")) { return "'" + $S + "'" }
    return '"' + ($S.Replace('\', '\\').Replace('"', '\"').Replace("`r", '\r').Replace("`n", '\n')) + '"'
}

function New-TomlBlock([string]$Name, $Desired, [string]$Nl) {
    $lines = @("[mcp_servers.$Name]")
    foreach ($k in $Desired.Keys) {
        $v = $Desired[$k]
        if ($v -is [string]) { $lines += "$k = $(ConvertTo-TomlString $v)" }
        elseif ($v -is [int] -or $v -is [long]) { $lines += "$k = $v" }
        elseif ($v -is [System.Collections.IEnumerable]) { $lines += "$k = [" + ((@($v) | ForEach-Object { ConvertTo-TomlString ([string]$_) }) -join ', ') + ']' }
        else { throw "Unsupported TOML value for $k" }
    }
    return ($lines -join $Nl) + $Nl
}

# Структурная проверка неоднозначности и состояние записи.
function Get-TomlState([string]$Text, [string]$Name) {
    $doc = [WbTomlScan]::Scan($Text)
    $prefix = @('mcp_servers', $Name)
    $main = @(); $subs = @()
    for ($i = 0; $i -lt $doc.Tables.Count; $i++) {
        $t = $doc.Tables[$i]
        if (-not (Test-PathPrefix $t.Path $prefix)) { continue }
        if ($t.IsArray) { throw "ambiguous: array of tables [[mcp_servers.$Name]]" }
        if ($t.Path.Count -eq 2) { $main += $i } else { $subs += $i }
    }
    if ($main.Count -gt 1) { throw "ambiguous: table [mcp_servers.$Name] occurs $($main.Count) times" }
    if ($subs.Count -gt 0 -and $main.Count -eq 0) { throw "ambiguous: sub-table of mcp_servers.$Name without its own table" }
    foreach ($k in $doc.Keys) {
        if (($main -contains $k.Table) -or ($subs -contains $k.Table)) { continue }
        if (Test-PathPrefix $k.FullPath $prefix) { throw "ambiguous: mcp_servers.$Name is also defined by a key outside its table" }
        if ($k.FullPath.Count -eq 1 -and $k.FullPath[0] -ceq 'mcp_servers') { throw 'ambiguous: mcp_servers is defined as an inline value' }
    }
    $state = @{ doc = $doc; main = $null; subs = @($subs | ForEach-Object { $doc.Tables[$_] }); command = $null; argsEmpty = $false }
    if ($main.Count -eq 1) {
        $state.main = $doc.Tables[$main[0]]
        $cmdKeys = @($doc.Keys | Where-Object { $_.Table -eq $main[0] -and $_.FullPath.Count -eq 3 -and $_.FullPath[2] -ceq 'command' })
        $argKeys = @($doc.Keys | Where-Object { $_.Table -eq $main[0] -and $_.FullPath.Count -eq 3 -and $_.FullPath[2] -ceq 'args' })
        if ($cmdKeys.Count -gt 1 -or $argKeys.Count -gt 1) { throw "ambiguous: repeated command/args keys in [mcp_servers.$Name]" }
        if ($cmdKeys.Count -eq 1) { $state.command = [WbTomlScan]::DecodeString($cmdKeys[0].Value) }
        if ($argKeys.Count -eq 1) { $state.argsEmpty = (($argKeys[0].Value -replace '\s', '') -ceq '[]') }
    }
    return $state
}

function Edit-TomlMcpServer {
    param([byte[]]$Bytes, [string]$Name, $Desired, [string]$Launcher, [bool]$Remove, [bool]$AnyInstall)
    $split = Split-Bom $Bytes
    $text = $script:Utf8Strict.GetString($split.bytes)
    $state = Get-TomlState $text $Name
    $nl = Get-NewLine $text
    $main = $state.main
    if ($Remove) {
        if (-not $main) { return @{ status = 'not_registered'; bytes = $Bytes; changed = $false } }
        if (-not $AnyInstall -and ($null -eq $state.command -or $state.command -ine $Launcher)) { return @{ status = 'kept_other_install'; bytes = $Bytes; changed = $false } }
        $spans = @($main) + @($state.subs) | Sort-Object { $_.Start } -Descending
        $out = $text
        foreach ($s in $spans) { $out = $out.Remove($s.Start, $s.End - $s.Start) }
        # блок в конце файла: убрать пустую строку, добавленную при вставке
        $first = ($spans | Select-Object -Last 1).Start
        if ($first -eq $out.Length -and $out.EndsWith($nl + $nl)) {
            $out = $out.Substring(0, $out.Length - $nl.Length)
            # блок был дописан в файл без завершающего перевода строки (без него и записан): вернуть файл как был
            if (-not $text.EndsWith("`n") -and -not $text.EndsWith("`r") -and $out.EndsWith($nl)) { $out = $out.Substring(0, $out.Length - $nl.Length) }
        }
        return @{ status = 'removed'; bytes = (Join-Bom $split.bom ($script:Utf8.GetBytes($out))); changed = $true }
    }
    $block = New-TomlBlock $Name $Desired $nl
    if ($main) {
        $cmdWant = [string]$Desired['command']
        if ($null -ne $state.command -and $state.command -ieq $cmdWant -and $state.argsEmpty) { return @{ status = 'already'; bytes = $Bytes; changed = $false } }
        $repl = if ($main.End -lt $text.Length) { $block + $nl } else { $block }
        $out = $text.Substring(0, $main.Start) + $repl + $text.Substring($main.End)
        return @{ status = 'updated'; bytes = (Join-Bom $split.bom ($script:Utf8.GetBytes($out))); changed = $true }
    }
    $out = $text
    if ($out.Length -gt 0) {
        if (-not ($out.EndsWith("`n") -or $out.EndsWith("`r"))) {
            # файл без завершающего перевода строки: блок тоже пишется без него, чтобы снятие вернуло байты как были
            $out += $nl + $nl
            $block = $block.Substring(0, $block.Length - $nl.Length)
        } else { $out += $nl }
    }
    $out += $block
    return @{ status = 'registered'; bytes = (Join-Bom $split.bom ($script:Utf8.GetBytes($out))); changed = $true }
}

# ---------- Реестр адаптеров клиентов ----------

# Env: hashtable переменных окружения (CLAUDE_CONFIG_DIR, CODEX_HOME, KIMI_CODE_HOME) либо $null - игнорировать.
function Get-ClientAdapters([string]$HomeDir, $Env, [string]$Name, [string]$Launcher) {
    $envv = { param($n) if ($Env -and $Env.ContainsKey($n) -and $Env[$n]) { return [string]$Env[$n] } else { return $null } }
    $claudeDir = & $envv 'CLAUDE_CONFIG_DIR'
    $codexHome = & $envv 'CODEX_HOME'
    $kimiHome = & $envv 'KIMI_CODE_HOME'
    $stdioJson = [ordered]@{ type = 'stdio'; command = $Launcher; args = @() }
    return @(
        @{ id = 'claude-code'; format = 'json'; path = (Join-Path ($(if ($claudeDir) { $claudeDir } else { $HomeDir })) '.claude.json')
           presenceDir = $null; requireFile = $true; entry = $stdioJson },
        @{ id = 'codex'; format = 'toml'; path = (Join-Path ($(if ($codexHome) { $codexHome } else { Join-Path $HomeDir '.codex' })) 'config.toml')
           presenceDir = ($(if ($codexHome) { $codexHome } else { Join-Path $HomeDir '.codex' })); requireFile = $false
           entry = [ordered]@{ command = $Launcher; args = @(); startup_timeout_sec = 20; tool_timeout_sec = 30 } },
        @{ id = 'kimi-code'; format = 'json'; path = (Join-Path ($(if ($kimiHome) { $kimiHome } else { Join-Path $HomeDir '.kimi-code' })) 'mcp.json')
           presenceDir = ($(if ($kimiHome) { $kimiHome } else { Join-Path $HomeDir '.kimi-code' })); requireFile = $false
           entry = [ordered]@{ command = $Launcher; args = @(); startupTimeoutMs = 20000; toolTimeoutMs = 30000 } },
        @{ id = 'antigravity'; format = 'json'; path = (Join-Path $HomeDir '.gemini\config\mcp_config.json')
           presenceDir = (Join-Path $HomeDir '.gemini\config'); requireFile = $false
           entry = [ordered]@{ command = $Launcher; args = @() } },
        @{ id = 'gemini-cli'; unsupported = $true; format = $null; path = (Join-Path $HomeDir '.gemini\settings.json')
           reason = 'Gemini CLI is not installed here and no client-specific file was confirmed: ~/.gemini/settings.json is shared with other tools. Antigravity is covered by the antigravity adapter.' }
    )
}

function Get-EntryState($A, [byte[]]$Bytes, [string]$Name) {
    # Для проверки результата: present / matches по сохранённым байтам.
    if ($A.format -eq 'json') {
        $split = Split-Bom $Bytes
        $scan = [WbJson]::Scan($split.bytes, 'mcpServers')
        $m = $null
        if ($scan.Child) { foreach ($x in $scan.Child.Members) { if ($x.Name -ceq $Name) { $m = $x } } }
        if (-not $m) { return @{ present = $false; matches = $false } }
        return @{ present = $true; matches = (Test-JsonEntryMatches (Get-JsonMemberText $split.bytes $m) $A.entry) }
    }
    $split = Split-Bom $Bytes
    $st = Get-TomlState ($script:Utf8Strict.GetString($split.bytes)) $Name
    $want = [string]$A.entry['command']
    return @{ present = [bool]$st.main; matches = ($st.main -and $null -ne $st.command -and $st.command -ieq $want -and $st.argsEmpty) }
}

# Выполняет (или планирует) регистрацию/снятие для одного клиента. ShouldWrite: scriptblock(path) -> bool.
function Invoke-ClientAdapter {
    param($A, [string]$Name, [string]$Launcher, [bool]$Remove, [bool]$AnyInstall, [scriptblock]$ShouldWrite, [hashtable]$BackedUp)
    $rep = [ordered]@{ client = $A.id; status = $null; found = $false; applied = $false; path = $A.path; backup = $null; detail = $null }
    if ($A.ContainsKey('unsupported') -and $A.unsupported) { $rep.status = 'unsupported'; $rep.detail = $A.reason; return $rep }
    try {
        $dirOk = if ($A.presenceDir) { Test-Path -LiteralPath $A.presenceDir -PathType Container } else { $true }
        $fileOk = Test-Path -LiteralPath $A.path -PathType Leaf
        $found = if ($A.requireFile) { $fileOk } else { $dirOk -or $fileOk }
        $rep.found = [bool]$found
        if (-not $found) { $rep.status = 'not_found'; $rep.detail = 'client configuration was not found'; return $rep }
        for ($attempt = 1; $attempt -le 2; $attempt++) {
            $existed = Test-Path -LiteralPath $A.path -PathType Leaf
            $orig = [byte[]]::new(0); if ($existed) { $orig = [IO.File]::ReadAllBytes($A.path) }
            if ($A.format -eq 'json') { $res = Edit-JsonMcpServers -Bytes $orig -Name $Name -Desired $A.entry -Launcher $Launcher -Remove $Remove -AnyInstall $AnyInstall }
            else { $res = Edit-TomlMcpServer -Bytes $orig -Name $Name -Desired $A.entry -Launcher $Launcher -Remove $Remove -AnyInstall $AnyInstall }
            $rep.status = $res.status
            if ($res.status -eq 'kept_other_install') { $rep.detail = 'entry points to another installation; use -IncludeOtherInstalls to remove it' }
            if (-not $res.changed) { return $rep }
            if (-not (& $ShouldWrite $A.path)) { return $rep }
            # Резервная копия до замены (копия текущего файла); если файл изменится до замены, копия удаляется и чтение повторяется.
            $newBackup = $null
            if ($existed -and -not $BackedUp.ContainsKey($A.path)) { $newBackup = New-ConfigBackup $A.path }
            # Защита от гонки с самим клиентом: Guard выполняется после записи временного файла, непосредственно перед
            # атомарной заменой, и сверяет текущее содержимое с прочитанным (изменился - замены нет).
            $guard = {
                $nowExisted = Test-Path -LiteralPath $A.path -PathType Leaf
                $now = [byte[]]::new(0); if ($nowExisted) { $now = [IO.File]::ReadAllBytes($A.path) }
                return ($nowExisted -eq $existed -and [System.Linq.Enumerable]::SequenceEqual([byte[]]$now, [byte[]]$orig))
            }.GetNewClosure()
            $wrote = Write-AtomicBytes $A.path $res.bytes $guard
            if (-not $wrote) {
                if ($newBackup) { Remove-Item -LiteralPath $newBackup -Force -ErrorAction SilentlyContinue }
                if ($attempt -lt 2) { Start-Sleep -Milliseconds 150; continue }
                $rep.status = 'error'; $rep.detail = 'configuration changed while it was being edited (concurrent writer); nothing was written'; return $rep
            }
            if ($newBackup) { $BackedUp[$A.path] = $newBackup }
            if ($BackedUp.ContainsKey($A.path)) { $rep.backup = $BackedUp[$A.path] }
            # Проверка записанного. Откатывается только СВОЯ ошибочная правка (файл побайтово равен тому, что мы записали,
            # но запись не проходит проверку). Если файл уже изменила другая программа, он не трогается.
            $after = $null
            try { $after = [IO.File]::ReadAllBytes($A.path) } catch { $after = $null }
            $stateOk = {
                param([byte[]]$Content)
                try {
                    $st = Get-EntryState $A $Content $Name
                    return [bool]$(if ($Remove) { -not $st.present } else { $st.present -and $st.matches })
                } catch { return $false }
            }
            $backupNote = if ($rep.backup) { "; backup: $($rep.backup)" } else { '' }
            if ($null -ne $after -and [System.Linq.Enumerable]::SequenceEqual([byte[]]$after, [byte[]]$res.bytes)) {
                if (& $stateOk $after) { $rep.applied = $true; return $rep }
                # собственная ошибка правки: вернуть исходное содержимое, но только если файл всё ещё тот, что записали мы
                $stillOurs = {
                    try { return ((Test-Path -LiteralPath $A.path -PathType Leaf) -and [System.Linq.Enumerable]::SequenceEqual([byte[]][IO.File]::ReadAllBytes($A.path), [byte[]]$res.bytes)) } catch { return $false }
                }.GetNewClosure()
                $restored = $false
                if (& $stillOurs) {
                    if ($existed) { $restored = [bool](Write-AtomicBytes $A.path $orig $stillOurs) }
                    else { Remove-Item -LiteralPath $A.path -Force -ErrorAction SilentlyContinue; $restored = -not (Test-Path -LiteralPath $A.path) }
                }
                $rep.status = 'error'
                $rep.detail = if ($restored) { 'written file failed verification; the original content was restored' } else { "written file failed verification and was changed by another program meanwhile; not restored$backupNote" }
                return $rep
            }
            # Файл после нашей записи отличается от записанного: его изменила другая программа (клиент). Не восстанавливаем.
            if ($null -ne $after -and (& $stateOk $after)) {
                $rep.applied = $true
                $rep.detail = 'the file was changed by another program right after the write; the entry is in place'
                return $rep
            }
            $rep.status = 'error'
            $rep.detail = "configuration was changed by another program right after the write (concurrent writer); the file was not restored and was left as that program wrote it$backupNote"
            return $rep
        }
    } catch {
        $rep.status = 'error'; $rep.detail = $_.Exception.Message
    }
    return $rep
}
