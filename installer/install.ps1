# MCSM Server Manager - installer payload
# Runs from the IExpress temp folder; copies the app to the user profile and
# registers it in Start Menu / Desktop / Add-Remove-Programs.
$ErrorActionPreference = "Stop"

$appName   = "MCSM Server Manager"
$exeName   = "MCSMServerManager.exe"
# 版本号从已发布的主程序读取，避免与 csproj / 界面显示不一致
$version = try {
    $v = (Get-Item -LiteralPath (Join-Path $PSScriptRoot $exeName)).VersionInfo.ProductVersion
    if ($v) { ($v -replace '[^0-9.].*$', '') } else { "1.1.0" }
} catch { "1.1.0" }
$publisher = "MCSMServerManager"

$sourceExe  = Join-Path $PSScriptRoot $exeName
$installDir = if ($env:MCSM_SETUP_INSTALL_DIR) { $env:MCSM_SETUP_INSTALL_DIR }
              else { Join-Path $env:LOCALAPPDATA "Programs\MCSMServerManager" }
$targetExe  = Join-Path $installDir $exeName
$regPath    = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MCSMServerManager"

function Show-NativeBox([string]$text, [string]$title) {
    try {
        Add-Type -AssemblyName PresentationFramework -ErrorAction Stop
        [System.Windows.MessageBox]::Show($text, $title) | Out-Null
    } catch {
        Write-Host $text
    }
}

try {
    if (-not (Test-Path -LiteralPath $sourceExe)) {
        throw "Source executable not found: $sourceExe"
    }

    # 若程序正在运行，先请用户自行关闭，避免未保存的编辑内容被强杀丢失。
    $running = Get-Process -Name "MCSMServerManager" -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host ""
        Write-Host "检测到 MCSM 服务器管理器正在运行。"
        Write-Host "为避免丢失未保存的文件编辑内容，请先手动关闭程序。"
        Write-Host "关闭后本安装程序会自动继续。"
        Write-Host ""

        # 给 60 秒等待用户自行关闭，而不是直接强杀
        $waited = 0
        while ((Get-Process -Name "MCSMServerManager" -ErrorAction SilentlyContinue) -and $waited -lt 60) {
            Start-Sleep -Seconds 2
            $waited += 2
        }

        $still = Get-Process -Name "MCSMServerManager" -ErrorAction SilentlyContinue
        if ($still) {
            Write-Host "程序仍未关闭。文件可能被锁定，安装将失败或需要重启后再试。"
            Write-Host ""
        }
        else {
            Write-Host "程序已关闭，继续安装。"
        }
        Start-Sleep -Milliseconds 500
    }

    if (Get-Process -Name "MCSMServerManager" -ErrorAction SilentlyContinue) {
        Write-Error "程序仍在运行，已中止安装。请关闭 MCSM 服务器管理器后重新运行本安装程序。"
        exit 1
    }

    Write-Host "Installing to $installDir"
    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
    Copy-Item -LiteralPath $sourceExe -Destination $targetExe -Force

    # ---- 许可证与声明（Apache-2.0 署名要求）--------------------------------
    foreach ($legal in @("LICENSE", "NOTICE")) {
        $srcLegal = Join-Path $PSScriptRoot $legal
        if (Test-Path -LiteralPath $srcLegal) {
            Copy-Item -LiteralPath $srcLegal -Destination (Join-Path $installDir $legal) -Force
        }
    }

    # ---- 日志目录 -----------------------------------------------------------
    $logDir = Join-Path $installDir "logs"
    if (-not (Test-Path -LiteralPath $logDir)) {
        New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    }

    # ---- 配置文件（已存在则保留用户设置）------------------------------------
    $configPath = Join-Path $installDir "config.json"
    if (-not (Test-Path -LiteralPath $configPath)) {
        $cfgLines = @(
            '{',
            '  "SavePanelUrl": true,',
            '  "SaveApiKey": false,',
            '  "LastPanelUrl": "http://localhost:23333",',
            '  "LastApiKey": "",',
            '  "IgnoreCertificateErrors": false,',
            '  "LogEnabled": true,',
            '  "LogLevel": "信息",',
            '  "LogHttpBody": false,',
            '  "LogMaxSizeMb": 8,',
            '  "LogKeepFiles": 14,',
            '  "PerformanceMonitorEnabled": true,',
            '  "PerformanceIntervalUnit": "秒",',
            '  "PerformanceIntervalValue": 10,',
            '  "ShowErrorDetailDialog": true,',
            '  "AutoConnectOnStart": false,',
            '  "InstancePageSize": 20,',
            '  "FilePageSize": 50,',
            '  "LogViewerLines": 5000',
            '}'
        )
        [IO.File]::WriteAllText($configPath, ($cfgLines -join [Environment]::NewLine), (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "Created default config: $configPath"
    }

    # ---- Shortcuts ---------------------------------------------------------
    $shell = New-Object -ComObject WScript.Shell

    $programs    = [Environment]::GetFolderPath("Programs")
    $startMenuLnkDir = Join-Path $programs $appName
    New-Item -ItemType Directory -Force -Path $startMenuLnkDir | Out-Null

    $startLnkPath = Join-Path $startMenuLnkDir "$appName.lnk"
    $startLnk = $shell.CreateShortcut($startLnkPath)
    $startLnk.TargetPath       = $targetExe
    $startLnk.WorkingDirectory = $installDir
    $startLnk.Description      = $appName
    $startLnk.Save()

    $desktopLnkPath = Join-Path ([Environment]::GetFolderPath("Desktop")) "$appName.lnk"
    $desktopLnk = $shell.CreateShortcut($desktopLnkPath)
    $desktopLnk.TargetPath       = $targetExe
    $desktopLnk.WorkingDirectory = $installDir
    $desktopLnk.Description      = $appName
    $desktopLnk.Save()

    # ---- Uninstaller -------------------------------------------------------
    $uninstallCmd = Join-Path $installDir "Uninstall.cmd"
    $uninstallLines = @(
        "@echo off",
        "echo Uninstalling $appName ...",
        "powershell -NoProfile -ExecutionPolicy Bypass -File `"$installDir\Uninstall.ps1`"",
        "exit /b 0"
    )
    # 必须写 UTF-8 with BOM：早前版本用 ASCII 编码，当安装路径含非 ASCII 字符
    # （例如中文目录）时，路径在写入 .cmd 时即被破坏，cmd 再把损坏的路径传给
    # PowerShell，报 "Illegal characters in path"，卸载直接失败。
    [IO.File]::WriteAllText($uninstallCmd, ($uninstallLines -join "`r`n"), (New-Object System.Text.UTF8Encoding($true)))

    # 延迟删除自身目录的辅助脚本。
    # 用 PowerShell 而非 cmd：早前版本用 ASCII 编码的 remove.cmd，
    # 当安装路径含非 ASCII 字符（例如中文目录）时路径会被写坏，导致 rmdir 静默失败、目录残留。
    $removePs = Join-Path $installDir "remove.ps1"
    $removeScript = @"
Start-Sleep -Seconds 2
Set-Location -LiteralPath `$env:TEMP
Remove-Item -LiteralPath '$installDir' -Recurse -Force -ErrorAction SilentlyContinue
"@
    [IO.File]::WriteAllText($removePs, $removeScript, (New-Object System.Text.UTF8Encoding($true)))

    $uninstallPs = Join-Path $installDir "Uninstall.ps1"
    $uninstallScript = @"
`$ErrorActionPreference = "SilentlyContinue"

# 与安装程序保持一致：程序运行中时绝不静默强杀，否则会丢掉用户未保存的文件编辑内容。
`$running = Get-Process -Name "MCSMServerManager" -ErrorAction SilentlyContinue
if (`$running) {
    Write-Host ""
    Write-Host "检测到 MCSM 服务器管理器正在运行。"
    Write-Host "为避免丢失未保存的文件编辑内容，请先手动关闭程序。"
    Write-Host "关闭后本卸载程序会自动继续（最多等待 60 秒）。"
    Write-Host ""

    `$waited = 0
    while ((Get-Process -Name "MCSMServerManager" -ErrorAction SilentlyContinue) -and `$waited -lt 60) {
        Start-Sleep -Seconds 2
        `$waited += 2
    }

    if (Get-Process -Name "MCSMServerManager" -ErrorAction SilentlyContinue) {
        Write-Host "程序仍未关闭，已取消卸载，未删除任何文件。"
        Write-Host "请关闭 MCSM 服务器管理器后重新运行卸载程序。"
        exit 1
    }
    Write-Host "程序已关闭，继续卸载。"
}

Remove-Item -LiteralPath "$desktopLnkPath" -Force
Remove-Item -LiteralPath "$startMenuLnkDir" -Recurse -Force
Remove-Item -LiteralPath "$regPath" -Recurse -Force
    Start-Process -FilePath "powershell.exe" -ArgumentList @("-NoProfile","-ExecutionPolicy","Bypass","-File","$removePs") -WindowStyle Hidden
"@
    [IO.File]::WriteAllText($uninstallPs, $uninstallScript, (New-Object System.Text.UTF8Encoding($true)))

    # ---- Add / Remove Programs --------------------------------------------
    New-Item -Path $regPath -Force | Out-Null
    Set-ItemProperty -Path $regPath -Name "DisplayName"     -Value $appName
    Set-ItemProperty -Path $regPath -Name "DisplayVersion"  -Value $version
    Set-ItemProperty -Path $regPath -Name "Publisher"       -Value $publisher
    Set-ItemProperty -Path $regPath -Name "InstallLocation" -Value $installDir
    Set-ItemProperty -Path $regPath -Name "DisplayIcon"     -Value $targetExe
    Set-ItemProperty -Path $regPath -Name "UninstallString" -Value "`"$uninstallCmd`""
    Set-ItemProperty -Path $regPath -Name "NoModify"        -Value 1 -Type DWord
    Set-ItemProperty -Path $regPath -Name "NoRepair"        -Value 1 -Type DWord

    Write-Host "Installed: $targetExe"

    if ($env:MCSM_SETUP_NO_LAUNCH -ne "1") {
        Start-Process -FilePath $targetExe -WorkingDirectory $installDir | Out-Null
    }
    exit 0
}
catch {
    $msg = $_.Exception.Message
    Write-Host "INSTALL FAILED: $msg"
    Write-Host $_.ScriptStackTrace
    if (-not $env:MCSM_SETUP_NO_DIALOG) {
        Show-NativeBox ("Installation failed.`n`n$msg`n`nPlease run this setup again, or contact support if the problem persists.") "MCSM Server Manager Setup"
    }
    exit 1
}