# Measures, for each way the stub can end, (a) the exit code an outside watcher would see and
# (b) whether the clean-exit proof from plan section 3.5 was left behind.
#
# The point is the SECOND column: section 3.5 reports any exit that leaves no proof, so we need to
# know that proof really is absent for crashes and kills, and present for orderly exits.

$ErrorActionPreference = 'Continue'
$dir   = Join-Path $PSScriptRoot 'FreezeStub\bin\Debug\net8.0-windows'
$exe   = Join-Path $dir 'FreezeStub.exe'
$cmdF  = Join-Path $dir 'freezestub-command.txt'
$proof = Join-Path $dir 'freezestub-exit-proof.txt'

function Test-ExitMode {
    param(
        [string]$Label,
        [string]$Command,      # word written to the command file, or '' to skip
        [switch]$HardKill,     # terminate from outside instead of commanding it
        [int]$SettleSeconds = 3
    )

    if (Test-Path $proof) { Remove-Item $proof -Force }
    $p = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Seconds 2

    if ($HardKill) {
        Stop-Process -Id $p.Id -Force
    } elseif ($Command) {
        Set-Content -Path $cmdF -Value $Command -Encoding utf8
    }

    $exited = $p.WaitForExit($SettleSeconds * 1000)
    if (-not $exited) {
        # Still running: that is itself the result for the zombie case.
        $stillHasWindow = $p.MainWindowHandle -ne 0
        [pscustomobject]@{
            Mode      = $Label
            ExitCode  = 'still running'
            ExitCodeHex = ''
            ProofLeft = (Test-Path $proof)
            Note      = "window handle present: $stillHasWindow"
        }
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        return
    }

    $code = $p.ExitCode
    $unsigned = [uint32]([int64]$code -band [int64]0xFFFFFFFF)
    [pscustomobject]@{
        Mode        = $Label
        ExitCode    = $code
        # Exit codes for crashes are negative as signed ints; show the unsigned form people quote.
        ExitCodeHex = ('0x{0:X8}' -f $unsigned)
        ProofLeft   = (Test-Path $proof)
        Note        = if (Test-Path $proof) { (Get-Content $proof -Raw).Trim() } else { '' }
    }
}

$results = @(
    Test-ExitMode -Label 'clean quit (Application.Exit)' -Command 'quit'
    Test-ExitMode -Label 'FailFast'                      -Command 'failfast' -SettleSeconds 8
    Test-ExitMode -Label 'unhandled exception'           -Command 'throw'    -SettleSeconds 8
    Test-ExitMode -Label 'hard kill (as Task Manager / debugger stop)' -HardKill
    Test-ExitMode -Label 'zombie: window closed, thread alive' -Command 'zombie' -SettleSeconds 4
)

$results | Format-Table -AutoSize -Wrap
