# Static safety checks over tests/. Prints PASS or FAIL lines and exits 1 on any failure.
#   lint.ps1 [-Root <folder>]   (default: the folder this script is in)
# 1. network:  no test points the widget, a request or a started process at a real address. Fake servers are on 127.0.0.1.
# 2. personal: no personal data. Text is normalised first (\uXXXX escapes decoded, Cyrillic, Greek and fullwidth look-alikes
#              folded to Latin, zero-width characters and accents removed), so look-alikes and escapes are caught too.
# 3. live:     no widget is ever started with "live", which shows real notices and registers a real global shortcut.
param([string]$Root = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$failures = New-Object System.Collections.Generic.List[string]
$files = Get-ChildItem $Root -Recurse -File -Include *.cs, *.ps1, *.py, *.rsp, *.md, *.txt, *.json, *.jsonl

# SHA-256 (first 32 hex characters) of the owner's names and account words, so the words themselves aren't in the repo.
$ownerHashes = @(
    '8426b28ac04abd0589c6605db5b60cd2', '46bc101ad19b92f195ad04e4b4d9d7d0', '87925ba4fc53cb65f7d9a40641c422b2',
    '7182911a54c0af14e7e7d0908f350703', '32d9501cfd9eee5f86f16f7379676ac0', '61cb9d956480c694289885c0029eee1a'
)
# Email domains fixtures may use: reserved example domains, and the generic ones the email-parsing tests rely on.
$allowedDomains = @('example.com', 'example.org', 'example.net', 'evil.com', 'good.com', 'b.com', 'c.com', 'github.com', 'users.noreply.github.com')

# Look-alike letters, as code points so this file stays ASCII (Windows PowerShell 5.1 reads a BOM-less file as ANSI).
$fold = @{}
$pairs = @(
    # Cyrillic lower: a e o p c y x i j s h d q w l
    0x0430, 'a', 0x0435, 'e', 0x043E, 'o', 0x0440, 'p', 0x0441, 'c', 0x0443, 'y', 0x0445, 'x', 0x0456, 'i', 0x0458, 'j',
    0x0455, 's', 0x04BB, 'h', 0x0501, 'd', 0x051B, 'q', 0x051D, 'w', 0x04CF, 'l', 0x0261, 'g',
    # Cyrillic upper: A B E K M H O P C T X Y I J S
    0x0410, 'a', 0x0412, 'b', 0x0415, 'e', 0x041A, 'k', 0x041C, 'm', 0x041D, 'h', 0x041E, 'o', 0x0420, 'p', 0x0421, 'c',
    0x0422, 't', 0x0425, 'x', 0x0423, 'y', 0x0406, 'i', 0x0408, 'j', 0x0405, 's',
    # Greek lower: a o p i k v t u x
    0x03B1, 'a', 0x03BF, 'o', 0x03C1, 'p', 0x03B9, 'i', 0x03BA, 'k', 0x03BD, 'v', 0x03C4, 't', 0x03C5, 'u', 0x03C7, 'x',
    # Greek upper: A B E Z H I K M N O P T Y X
    0x0391, 'a', 0x0392, 'b', 0x0395, 'e', 0x0396, 'z', 0x0397, 'h', 0x0399, 'i', 0x039A, 'k', 0x039C, 'm', 0x039D, 'n',
    0x039F, 'o', 0x03A1, 'p', 0x03A4, 't', 0x03A5, 'y', 0x03A7, 'x'
)
for ($k = 0; $k -lt $pairs.Count; $k += 2) { $fold[[string][char]$pairs[$k]] = $pairs[$k + 1] }

function Normalize([string]$text) {
    # Plain ASCII without escapes only needs lower-casing; the slow path is for everything else.
    if ($text -notmatch '[^\x00-\x7F]|\\+[ux][0-9a-fA-F]|&#x') { return $text.ToLowerInvariant() }
    $t = [regex]::Replace($text, '\\+u([0-9a-fA-F]{4})', { param($m) [string][char][Convert]::ToInt32($m.Groups[1].Value, 16) })
    $t = [regex]::Replace($t, '\\+x([0-9a-fA-F]{2})', { param($m) [string][char][Convert]::ToInt32($m.Groups[1].Value, 16) })
    $t = [regex]::Replace($t, '&#x([0-9a-fA-F]{1,4});', { param($m) [string][char][Convert]::ToInt32($m.Groups[1].Value, 16) })
    $sb = New-Object System.Text.StringBuilder
    foreach ($ch in $t.Normalize([Text.NormalizationForm]::FormD).ToCharArray()) {
        $cat = [Globalization.CharUnicodeInfo]::GetUnicodeCategory($ch)
        if ($cat -eq 'NonSpacingMark' -or $cat -eq 'Format') { continue }
        $code = [int]$ch
        if ($code -ge 0xFF01 -and $code -le 0xFF5E) { [void]$sb.Append([char]($code - 0xFEE0)); continue }
        $s = [string]$ch
        if ($fold.ContainsKey($s)) { [void]$sb.Append($fold[$s]) } else { [void]$sb.Append($s) }
    }
    return $sb.ToString().ToLowerInvariant()
}
$sha = [Security.Cryptography.SHA256]::Create()
$hashCache = @{}
function Hash([string]$word) {
    if (-not $hashCache.ContainsKey($word)) {
        $hashCache[$word] = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($word))).Replace('-', '').Substring(0, 32).ToLowerInvariant()
    }
    return $hashCache[$word]
}

$networkCall = 'Download\(|\.Fetch\(|WebRequest|WebClient|HttpClient|TcpClient|new Socket\(|ReleasesUrl\s*=|ProfileUrl\s*=|new WidgetWindow\(|StartWidget\(|ProcessStartInfo\(|Start-Process|ArgumentList|Invoke-WebRequest|Invoke-RestMethod|urlopen|requests\.(get|post)|\bcurl\b|\bwget\b'
$realUrl = 'https?://(?!127\.0\.0\.1[:/]|localhost[:/])[A-Za-z0-9.-]+'

foreach ($f in $files) {
    if ($f.FullName -eq $PSCommandPath) { continue }   # this file holds the patterns themselves
    $rel = $f.FullName.Substring($Root.Length).TrimStart('\')
    $lines = [IO.File]::ReadAllLines($f.FullName)
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]; $at = "${rel}:$($i + 1)"
        # 1. network
        if ($line -match $networkCall -and ($line -match $realUrl -or $line -match 'ReleasesAddress|ProfileAddress|api\.anthropic\.com|api\.github\.com')) {
            $failures.Add("network: $at points a request, widget or process at a real address")
        }
        # 3. live
        if ($line -match 'StartWidget\(|ProcessStartInfo\(|Start-Process|ArgumentList|Program\.Main' -and $line -match '["'']live["'']') {
            $failures.Add("live: $at starts a widget with ""live""")
        }
        # 2. personal
        $n = Normalize $line
        if ($n -match '[a-z]:\\{1,2}users\\{1,2}[a-z0-9._-]{3,}') { $failures.Add("personal: $at has a user folder path") }
        if ($n -match 'sk-ant-[a-z0-9_-]{8,}') { $failures.Add("personal: $at has what looks like an Anthropic key") }
        if ($n -match 'bearer [a-z0-9._-]{20,}') { $failures.Add("personal: $at has what looks like a real bearer token") }
        if ($n -match '"?refreshtoken"?\s*[:=]\s*"[^"]{8,}') { $failures.Add("personal: $at has a refresh token value") }
        foreach ($m in [regex]::Matches($n, '[a-z0-9._%+-]+@([a-z0-9-]+(\.[a-z0-9-]+)+)')) {
            $domain = $m.Groups[1].Value.TrimEnd('.')
            $ok = $false
            foreach ($d in $allowedDomains) { if ($domain -eq $d -or $domain.EndsWith('.' + $d)) { $ok = $true } }
            if ($domain.EndsWith('.example')) { $ok = $true }
            if (-not $ok) { $failures.Add("personal: $at has an email on a domain fixtures don't use ($domain)") }
        }
        foreach ($w in [regex]::Matches($n, '\b[a-z0-9]{5,12}\b')) {
            if ($ownerHashes -contains (Hash $w.Value)) { $failures.Add("personal: $at has one of the owner's names or account words") }
        }
    }
}

foreach ($check in 'network', 'personal', 'live') {
    $mine = @($failures | Where-Object { $_.StartsWith($check + ':') })
    if ($mine.Count -eq 0) { Write-Output "PASS lint: $check ($($files.Count) files)" }
    else { foreach ($x in $mine) { Write-Output "FAIL lint: $x" } }
}
if ($failures.Count -gt 0) { exit 1 }
exit 0
