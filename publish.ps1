<#
.SYNOPSIS
    发布 IcsWatch 单文件 exe，并（可选）就地部署到安装目录。

.DESCRIPTION
    **强制校验产物大小**：单文件自包含的正常体积约 63 MB，
    低于 50 MB 基本可以断定是被打断/截断的半成品，直接报错，绝不部署出去。
    （踩过：一次发布被中途打断留下 17.7 MB 的 exe，MSBuild 增量判断仍认为"最新"，
      结果启动即退出、日志里连"启动"都没有。）

.PARAMETER OutDir
    发布输出目录，默认 <仓库根>\publish（已在 .gitignore 里）

.PARAMETER Deploy
    发布成功后：停掉正在运行的实例 → 覆盖 <安装目录>\IcsWatch.exe → 重新启动

.PARAMETER InstallDir
    安装目录（exe 与 config.json、logs\、data\ 所在处），默认当前用户名下的
    %LOCALAPPDATA%\IcsWatch。首次部署会自动建目录。

.EXAMPLE
    .\publish.ps1                                  # 只发布，不动现役程序
    .\publish.ps1 -Deploy -InstallDir 'C:\Tools\IcsWatch' # 发布并部署到指定目录
#>
param(
    [string]$OutDir = (Join-Path $PSScriptRoot 'publish'),
    [switch]$Deploy,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'IcsWatch')
)

$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj   = Join-Path $root 'src\IcsWatch\IcsWatch.csproj'

Write-Host "== 1/4 编译并发布 ==" -ForegroundColor Cyan
# -p:EnableCompressionInSingleFile=true 必须保留：去掉会产出 139 MB。
dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -o $OutDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }

$exe = Join-Path $OutDir 'IcsWatch.exe'
if (-not (Test-Path $exe)) { throw "发布目录里没有 IcsWatch.exe: $OutDir" }

Write-Host "== 2/4 校验产物 ==" -ForegroundColor Cyan
$len = (Get-Item $exe).Length
$sizeMb = [math]::Round($len / 1MB, 1)
if ($len -lt 50MB) {
    throw "产物只有 $sizeMb MB（预期 ≥50 MB），疑似截断，已中止。文件: $exe"
}
$ver = (Get-Item $exe).VersionInfo.FileVersion
$sha = (Get-FileHash $exe -Algorithm SHA256).Hash
Write-Host ("   exe: {0}   {1} MB   v{2}" -f $exe, $sizeMb, $ver)
Write-Host ("   sha256: {0}" -f $sha)

if (-not $Deploy) {
    Write-Host "== 完成（未部署，加 -Deploy 才会替换现役 exe）==" -ForegroundColor Green
    return
}

Write-Host "== 3/4 停止现有实例 ==" -ForegroundColor Cyan
$procs = Get-Process IcsWatch -ErrorAction SilentlyContinue
if ($procs) {
    $procs | ForEach-Object { Write-Host ("   停止 PID {0}" -f $_.Id) }
    $procs | Stop-Process -Force
    Start-Sleep -Milliseconds 900
}

Write-Host "== 4/4 部署并重启 ==" -ForegroundColor Cyan
if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir | Out-Null }
$target = Join-Path $InstallDir 'IcsWatch.exe'
Copy-Item $exe $target -Force
Write-Host ("   已覆盖 {0}  ({1} MB)" -f $target, [math]::Round((Get-Item $target).Length / 1MB, 1))
Start-Process $target
Start-Sleep -Seconds 2
$new = Get-Process IcsWatch -ErrorAction SilentlyContinue
if ($new) { Write-Host ("   已启动 PID {0}" -f ($new | Select-Object -First 1).Id) -ForegroundColor Green }
else { Write-Warning "进程没起来，检查 $InstallDir\logs 里的最新日志" }
