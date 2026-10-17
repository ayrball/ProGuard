<#
.SYNOPSIS
    Turns LICENSE (Markdown) and NOTICE (plain text) into the RTF the installer's license agreement dialog shows.
.DESCRIPTION
    Handles only what those two files use: # headings, paragraphs, > quotes, [text](link), <url> and `code`.
#>
param(
    [Parameter(Mandatory)][string[]]$Path,
    [Parameter(Mandatory)][string]$Destination
)

$ErrorActionPreference = "Stop"

function ConvertTo-RtfText([string]$text) {
    $text = $text -replace '\[([^\]]+)\]\([^)]*\)', '$1' -replace '<(https?://[^>]+)>', '$1' -replace '`', ''
    $sb = [System.Text.StringBuilder]::new()
    foreach ($c in $text.ToCharArray()) {
        if ($c -eq '\' -or $c -eq '{' -or $c -eq '}') { [void]$sb.Append('\').Append($c) }
        elseif ([int]$c -gt 127) { [void]$sb.Append("\u$([int]$c)?") }
        else { [void]$sb.Append($c) }
    }
    $sb.ToString()
}

$body = [System.Text.StringBuilder]::new()
foreach ($file in $Path) {
    # Paragraphs are separated by blank lines; lines inside one are joined.
    $paragraphs = ((Get-Content -Raw -Encoding UTF8 $file) -replace "`r", "") -split "\n\s*\n"
    foreach ($paragraph in $paragraphs) {
        $line = ($paragraph.Trim() -split "\n" | ForEach-Object { $_.Trim() }) -join " "
        if (-not $line) { continue }
        if ($line -match '^(#+)\s+(.*)$') {
            $size = if ($Matches[1].Length -eq 1) { 24 } else { 20 }
            [void]$body.Append("{\pard\sb120\sa60\b\fs$size $(ConvertTo-RtfText $Matches[2])\par}`r`n")
        } elseif ($line -match '^>\s*(.*)$') {
            [void]$body.Append("{\pard\li360\sa120\fs18 $(ConvertTo-RtfText $Matches[1])\par}`r`n")
        } else {
            [void]$body.Append("{\pard\sa120\fs18 $(ConvertTo-RtfText $line)\par}`r`n")
        }
    }
}

$rtf = "{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fswiss Segoe UI;}}\viewkind4\uc1`r`n$body}"
[System.IO.File]::WriteAllText($Destination, $rtf, [System.Text.Encoding]::ASCII)
