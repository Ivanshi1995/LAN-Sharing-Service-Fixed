param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDir,
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$ReleaseNotes = "",
    [string]$ReleaseNotesFile = ""
)

$ErrorActionPreference = 'Stop'

$OutputDir = (Resolve-Path -LiteralPath $OutputDir).Path.TrimEnd('\', '/')

if ($ReleaseNotesFile -and (Test-Path $ReleaseNotesFile)) {
    $ReleaseNotes = [System.IO.File]::ReadAllText($ReleaseNotesFile, [System.Text.UTF8Encoding]::new($false)).Trim()
}

$excludeNames = @('version.json', 'settings.json', 'users.json')
$excludeExts = @('.razor.json', '.log')
$files = [ordered]@{}

Get-ChildItem -Path $OutputDir -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($OutputDir.Length).TrimStart('\', '/').Replace('\', '/')
    $name = Split-Path $rel -Leaf
    if ($excludeNames -contains $name) { return }
    if ($rel -like 'SharedFiles/*' -or $rel -like '.update/*' -or $rel -like 'webview_cache/*' -or $rel -like '.upload-sessions/*' -or $rel -like 'log/*' -or $rel -like '*/obj/*' -or $rel -like '*/bin/*' -or $rel -like '*/EBWebView/*' -or $rel -like '*/.db' -or $rel -like '*/.db-journal') { return }
    foreach ($ext in $excludeExts) { if ($name.EndsWith($ext)) { return } }

    try {
        $hash = (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    catch {
        Write-Warning "Skip locked file: $rel"
        return
    }
    $files[$rel] = $hash
}

$manifest = [ordered]@{
    version       = $Version
    release_notes = $ReleaseNotes
    files         = $files
}

$outFile = Join-Path $OutputDir 'version.json'
$json = ($manifest | ConvertTo-Json -Depth 10)
[System.IO.File]::WriteAllText($outFile, $json, [System.Text.UTF8Encoding]::new($false))

Write-Host "Generated version.json ($Version) with $($files.Count) files"
