# Attach-only stdio MCP launcher. Run with the game project as the working directory.
[CmdletBinding()]
param([string]$EngineDirectory)
$ErrorActionPreference = 'Stop'
try {
    $candidates = if ($EngineDirectory) { @($EngineDirectory) } else {
        @($env:FACEPUNCH_ENGINE, [Environment]::GetEnvironmentVariable('FACEPUNCH_ENGINE', 'User'),
          $(if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} 'Steam/steamapps/common/sbox' }))
    }
    $executable = $null
    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        $path = Join-Path $candidate 'bin/managed/sbox-cli.exe'
        if (Test-Path -LiteralPath $path -PathType Leaf) { $executable = [IO.Path]::GetFullPath($path); break }
    }
    if (-not $executable) { throw 'sbox-cli.exe was not found. Set FACEPUNCH_ENGINE or pass -EngineDirectory for your s&box installation.' }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $executable
    $start.ArgumentList.Add('--connect')
    $start.WorkingDirectory = (Get-Location).ProviderPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    # Explicit byte-stream relay: nested processes do not reliably inherit the
    # redirected client stdin through PowerShell. Never format JSON-RPC as objects.
    $process = [Diagnostics.Process]::Start($start)
    $inputCopy = [Console]::OpenStandardInput().CopyToAsync($process.StandardInput.BaseStream)
    $outputCopy = $process.StandardOutput.BaseStream.CopyToAsync([Console]::OpenStandardOutput())
    $errorCopy = $process.StandardError.BaseStream.CopyToAsync([Console]::OpenStandardError())
    $inputClosed = $false
    while (-not $process.WaitForExit(100)) {
        if (-not $inputClosed -and $inputCopy.IsCompleted) {
            $null = $inputCopy.GetAwaiter().GetResult()
            $process.StandardInput.Close()
            $inputClosed = $true
        }
    }
    $null = $outputCopy.GetAwaiter().GetResult()
    $null = $errorCopy.GetAwaiter().GetResult()
    $code = $process.ExitCode
    $process.Dispose()
    exit $code
} catch {
    [Console]::Error.WriteLine("sbox-mcp-plus: $($_.Exception.Message)")
    exit 1
}
