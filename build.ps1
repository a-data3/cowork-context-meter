# build.ps1 - builds CoworkContextMeter.exe (GUI) and TestScanner.exe (test harness)
# Requires .NET Framework 4.x (csc.exe ships with Windows). C# 5 source only.

$ErrorActionPreference = "Stop"

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir = Join-Path $root "src"

$scannerSrc = Join-Path $srcDir "Scanner.cs"
$appSrc = Join-Path $srcDir "App.cs"
$testSrc = Join-Path $srcDir "TestScanner.cs"
$guiExe = Join-Path $root "CoworkContextMeter.exe"
$testExe = Join-Path $root "TestScanner.exe"

if (-not (Test-Path $csc)) {
    Write-Host "ERROR: csc.exe not found at $csc"
    exit 1
}

Write-Host "Building GUI -> $guiExe"
& $csc /nologo /target:winexe /out:"$guiExe" `
    /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    "$scannerSrc" "$appSrc"
if ($LASTEXITCODE -ne 0) {
    Write-Host "GUI build FAILED (exit $LASTEXITCODE)"
    exit 1
}

Write-Host "Building test harness -> $testExe"
& $csc /nologo /target:exe /out:"$testExe" `
    /r:System.Web.Extensions.dll `
    "$scannerSrc" "$testSrc"
if ($LASTEXITCODE -ne 0) {
    Write-Host "Test harness build FAILED (exit $LASTEXITCODE)"
    exit 1
}

Write-Host "Build OK"
exit 0
