<#
.SYNOPSIS
    Compila WinDrop como ejecutable portable en la carpeta build/.

.DESCRIPTION
    Publica la aplicación como un único .exe autocontenido para Windows x64.
    Autocontenido significa que no hace falta tener .NET instalado en el equipo
    de destino: el runtime va dentro del ejecutable.

.PARAMETER SkipTests
    Omite la ejecución de la suite de tests antes de publicar.
#>
[CmdletBinding()]
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$output = Join-Path $root 'build'

# El SDK puede estar instalado por usuario en lugar de en Archivos de programa.
$userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if (Test-Path (Join-Path $userDotnet 'dotnet.exe')) {
    $env:PATH = "$userDotnet;$env:PATH"
}

Write-Host 'WinDrop - compilación portable' -ForegroundColor Cyan
Write-Host ''

if (-not $SkipTests) {
    Write-Host 'Ejecutando tests...' -ForegroundColor Yellow
    dotnet test (Join-Path $root 'AirDrop.slnx') --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        throw 'Los tests han fallado. Se cancela la compilación.'
    }
    Write-Host ''
}

# Se vacía el contenido en lugar de borrar la carpeta: si el explorador la tiene abierta, o
# quedan handles de una ejecución anterior del ejecutable, borrar la carpeta en sí falla.
if (Test-Path $output) {
    Get-ChildItem $output -Force -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}
else {
    New-Item -ItemType Directory -Path $output | Out-Null
}

Write-Host 'Publicando ejecutable portable...' -ForegroundColor Yellow

dotnet publish (Join-Path $root 'src\AirDrop.App\AirDrop.App.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $output `
    --nologo `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none

if ($LASTEXITCODE -ne 0) {
    throw 'La publicación ha fallado.'
}

# Los .pdb no aportan nada en una distribución portable y ocupan bastante.
Get-ChildItem $output -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

$exe = Join-Path $output 'WinDrop.exe'
if (-not (Test-Path $exe)) {
    throw "No se generó el ejecutable en $exe"
}

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)

Write-Host ''
Write-Host 'Listo.' -ForegroundColor Green
Write-Host "  Ejecutable : $exe"
Write-Host "  Tamaño     : $size MB"
Write-Host ''
Write-Host 'Es portable: no requiere instalación ni tener .NET en el equipo.'
