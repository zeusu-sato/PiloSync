param(
    [string]$Source,
    [string]$Destination = (Join-Path (Split-Path $PSScriptRoot -Parent) 'logs')
)

$ErrorActionPreference = 'Stop'
$count = 0
try {
    if (-not $Source) {
        $Source = Join-Path $env:LOCALAPPDATA 'PiloSync'
        $previous = Join-Path $env:LOCALAPPDATA 'PalmSync'
        if (-not (Test-Path -LiteralPath $Source -PathType Container) -and
            (Test-Path -LiteralPath $previous -PathType Container)) {
            $Source = $previous
        }
    }

    function Copy-Log($file, $target) {
        try {
            if (Test-Path -LiteralPath $target -PathType Leaf) {
                $existing = Get-Item -LiteralPath $target
                if ($existing.Length -eq $file.Length -and $existing.LastWriteTimeUtc -eq $file.LastWriteTimeUtc) { return }
            }
            $null = New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent)
            try {
                Copy-Item -LiteralPath $file.FullName -Destination $target -Force
            } catch {
                $inputStream = $null
                $outputStream = $null
                try {
                    $inputStream = [System.IO.File]::Open($file.FullName, 'Open', 'Read', 'ReadWrite')
                    $outputStream = [System.IO.File]::Open($target, 'Create', 'Write', 'Read')
                    $inputStream.CopyTo($outputStream)
                } finally {
                    if ($outputStream) { $outputStream.Dispose() }
                    if ($inputStream) { $inputStream.Dispose() }
                }
            }
            [System.IO.File]::SetLastWriteTimeUtc($target, $file.LastWriteTimeUtc)
            $script:count++
        } catch { }
    }

    $logs = Join-Path $Source 'Logs'
    if (Test-Path -LiteralPath $logs -PathType Container) {
        Get-ChildItem -LiteralPath $logs -Filter '*.log' -File | ForEach-Object {
            Copy-Log $_ (Join-Path $Destination $_.Name)
        }
    }
    $accounts = Join-Path $Source 'Palm OS Desktop'
    if (Test-Path -LiteralPath $accounts -PathType Container) {
        Get-ChildItem -LiteralPath $accounts -Directory | ForEach-Object {
            $log = Join-Path $_.FullName 'HotSync.log'
            if (Test-Path -LiteralPath $log -PathType Leaf) {
                Copy-Log (Get-Item -LiteralPath $log) (Join-Path (Join-Path (Join-Path $Destination 'accounts') $_.Name) 'HotSync.log')
            }
        }
    }
} catch { }
Write-Output "Copied: $count"
exit 0
