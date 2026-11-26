param(
    [string]$Configuration = "Release",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

$root      = Split-Path -Parent $PSScriptRoot
$project   = Join-Path $root "MCSMServerManager.csproj"
$stage     = Join-Path $root "stage"
$outputDir = Join-Path $root "output"
$setupExe  = Join-Path $outputDir "MCSMServerManager_Setup.exe"
$sedFile   = Join-Path $root "setup.sed"
$publishDir = Join-Path $root "publish-single"

if (-not $SkipPublish) {
    Write-Host "==> dotnet publish ($Configuration)"
    dotnet publish $project -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None -p:DebugSymbols=false -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
}

$publishedExe = Join-Path $publishDir "MCSMServerManager.exe"
if (-not (Test-Path -LiteralPath $publishedExe)) {
    throw "Published executable not found: $publishedExe"
}

Write-Host "==> Staging payload"
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage     | Out-Null
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

Copy-Item -LiteralPath $publishedExe                     -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "install.cmd") -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "install.ps1") -Destination $stage -Force

# 随安装包一起分发许可证与声明，满足 Apache-2.0 的署名要求
$legalDir = Split-Path -Parent $PSScriptRoot
foreach ($legal in @("LICENSE", "NOTICE")) {
    $legalPath = Join-Path $legalDir $legal
    if (Test-Path -LiteralPath $legalPath) {
        Copy-Item -LiteralPath $legalPath -Destination $stage -Force
    }
}

if (Test-Path -LiteralPath $setupExe) { Remove-Item -LiteralPath $setupExe -Force }

Write-Host "==> Writing IExpress SED"
$sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=1
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=MCSM Server Manager has been installed. You can start it from the Start Menu or your Desktop.
TargetName=$setupExe
FriendlyName=MCSM Server Manager Setup
AppLaunched=install.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
SourceFiles=SourceFiles
[Strings]
FILE0="MCSMServerManager.exe"
FILE1="install.cmd"
FILE2="install.ps1"
FILE3="LICENSE"
FILE4="NOTICE"
[SourceFiles]
SourceFiles0=$stage\
[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
%FILE3%=
%FILE4%=
"@

# IExpress requires the SED file to be UTF-16 (Unicode).
Set-Content -LiteralPath $sedFile -Value $sed -Encoding Unicode

Write-Host "==> Running IExpress (this compresses ~$([math]::Round((Get-Item $publishedExe).Length/1MB,0)) MB, please wait)"
$iexpress = Join-Path $env:WINDIR "System32\iexpress.exe"
if (-not (Test-Path -LiteralPath $iexpress)) { throw "iexpress.exe not found" }

$proc = Start-Process -FilePath $iexpress -ArgumentList @("/N", "/Q", $sedFile) `
        -Wait -PassThru -WindowStyle Hidden
if ($proc.ExitCode -ne 0) { throw "IExpress exited with code $($proc.ExitCode)" }
if (-not (Test-Path -LiteralPath $setupExe)) { throw "Setup executable was not produced: $setupExe" }

$item = Get-Item -LiteralPath $setupExe
$hash = (Get-FileHash -LiteralPath $setupExe -Algorithm SHA256).Hash
Write-Host ""
Write-Host "DONE  $($item.FullName)"
Write-Host "      size   : $([math]::Round($item.Length/1MB,2)) MB"
Write-Host "      sha256 : $hash"