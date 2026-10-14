[CmdletBinding()]
param([string]$EmbeddedRoot, [switch]$SelfTest, [switch]$InventoryOnly)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$canonical = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function SharedFiles([string]$Root) {
    @('README.md','README-model-import.md','docs/MCP-PARITY.md','Tests/.gitignore')
    foreach($directory in @('Editor','Tests')) {
        $path = Join-Path $Root $directory
        if(Test-Path -LiteralPath $path) {
            Get-ChildItem -LiteralPath $path -File | Where-Object {
                $_.Extension -eq '.cs' -or ($directory -eq 'Tests' -and $_.Extension -in '.ps1','.csproj')
            } | ForEach-Object { "$directory/$($_.Name)" }
        }
    }
}
function Differences([string]$Left, [string]$Right) {
    $files = @(SharedFiles $Left) + @(SharedFiles $Right) | Sort-Object -Unique
    foreach($file in $files) {
        $a = Join-Path $Left $file; $b = Join-Path $Right $file
        if(-not (Test-Path -LiteralPath $a -PathType Leaf)) { "Canonical missing: $file"; continue }
        if(-not (Test-Path -LiteralPath $b -PathType Leaf)) { "Embedded missing: $file"; continue }
        # Git may check the same source out with different line endings. All other bytes matter.
        $leftText = [IO.File]::ReadAllText($a).Replace("`r`n","`n")
        $rightText = [IO.File]::ReadAllText($b).Replace("`r`n","`n")
        if($leftText -cne $rightText) { "Content drift: $file" }
    }
}
function CheckInventory([string]$Root) {
    $names = @()
    foreach($file in (Get-ChildItem (Join-Path $Root 'Editor') -Filter '*.cs')) {
        $source = [IO.File]::ReadAllText($file.FullName)
        if($source -match 'using KampaiRoulette|namespace KampaiRoulette|kr_playtest_') { throw "Game dependency in generic source: $($file.Name)" }
        foreach($match in [regex]::Matches($source,'\[McpTool(?:\.ReadOnly)?\(\s*"([^"]+)"')) { $names += $match.Groups[1].Value }
    }
    if($names.Count -eq 0) { throw 'No MCP tools found.' }
    if(@($names | Group-Object | Where-Object Count -GT 1).Count) { throw 'Duplicate MCP tool name.' }
    $docs = [IO.File]::ReadAllText((Join-Path $Root 'README.md')) + [IO.File]::ReadAllText((Join-Path $Root 'README-model-import.md'))
    foreach($name in $names) { if(-not $name.StartsWith('x_') -or -not $docs.Contains($name)) { throw "Undocumented/unprefixed tool: $name" } }
    "PASS inventory: $($names.Count) unique documented generic tools"
}
if($InventoryOnly) {
    CheckInventory $canonical
    exit 0
}
if($SelfTest) {
    $fixture = Join-Path ([IO.Path]::GetTempPath()) ('sbox-mcp-parity-' + [Guid]::NewGuid().ToString('N'))
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if(-not $resolved.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture escapes temp root.' }
    try {
        foreach($side in @('left','right')) {
            $root = Join-Path $fixture $side
            foreach($file in (SharedFiles $canonical)) {
                $destination = Join-Path $root $file
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
                Copy-Item -LiteralPath (Join-Path $canonical $file) -Destination $destination
            }
        }
        $left = Join-Path $fixture 'left'; $right = Join-Path $fixture 'right'
        if(@(Differences $left $right).Count) { throw 'Identical fixture rejected.' }
        $candidate = Join-Path $right 'Editor/InputTraceBuffer.cs'
        $original = [IO.File]::ReadAllText($candidate)
        [IO.File]::WriteAllText($candidate,$original.Replace("`r`n","`n"))
        if(@(Differences $left $right).Count) { throw 'Line-ending-only fixture rejected.' }
        [IO.File]::AppendAllText($candidate,'// intentional drift')
        if(@(Differences $left $right).Count -ne 1) { throw 'Content drift not detected.' }
        Remove-Item -LiteralPath $candidate
        if(@(Differences $left $right).Count -ne 1) { throw 'Missing shared source not detected.' }
        Copy-Item -LiteralPath (Join-Path $left 'Editor/InputTraceBuffer.cs') -Destination $candidate
        [IO.File]::WriteAllText((Join-Path $right 'Editor/NewTool.cs'),'// new source')
        if(@(Differences $left $right).Count -ne 1) { throw 'Additional shared source not detected.' }
        'PASS parity fixtures: identical, CRLF/LF, changed, missing, added'
    } finally {
        # Exact UUID fixture checked above, native PowerShell cleanup only.
        if(Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
    CheckInventory $canonical
    exit 0
}
if([string]::IsNullOrWhiteSpace($EmbeddedRoot)) { throw 'Use -InventoryOnly for a submodule, -SelfTest for legacy comparison fixtures, or -EmbeddedRoot for an older vendored checkout.' }
$embedded = [IO.Path]::GetFullPath($EmbeddedRoot)
if(-not (Test-Path -LiteralPath $embedded -PathType Container)) { throw 'Embedded root does not exist.' }
$differences = @(Differences $canonical $embedded)
if($differences.Count) { $differences | Write-Output; throw "$($differences.Count) shared-file differences; reconcile deliberately before claiming parity." }
CheckInventory $canonical
"PASS shared parity: $(@(SharedFiles $canonical).Count) files (CRLF/LF normalized); no files written"
