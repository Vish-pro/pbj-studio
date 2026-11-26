# Builds dist\PBJStudio-<version>.msix for the Microsoft Store.
# Needs: the protected release build in ..\..\release, the OCR language files in ..\ocr-languages
# (fetch-ocr-languages.ps1), and Microsoft's build tools unpacked in ..\sdk-buildtools\pkg
# (NuGet package Microsoft.Windows.SDK.BuildTools) or an installed Windows SDK.
# The package is left unsigned: the Store signs it. Identity values come from identity.json.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$packaging = Split-Path -Parent $here
$root = Split-Path -Parent $packaging
$release = Join-Path $root 'release'
$languages = Join-Path $packaging 'ocr-languages'
$dist = Join-Path $root 'dist'
$layout = Join-Path $dist 'msix-layout'

function Find-Tool([string]$name) {
    $roots = @((Join-Path $packaging 'sdk-buildtools\pkg\bin'), "${env:ProgramFiles(x86)}\Windows Kits\10\bin")
    foreach ($r in $roots) {
        if (Test-Path $r) {
            $hit = Get-ChildItem $r -Recurse -Filter $name -File | Where-Object { $_.FullName -like '*\x64\*' } |
                Sort-Object FullName -Descending | Select-Object -First 1
            if ($hit) { return $hit.FullName }
        }
    }
    throw "$name not found. Unpack the NuGet package Microsoft.Windows.SDK.BuildTools into packaging\sdk-buildtools\pkg."
}

$makeappx = Find-Tool 'makeappx.exe'
$makepri = Find-Tool 'makepri.exe'

foreach ($f in 'PBJStudio.exe', 'x64\tesseract50.dll', 'x64\leptonica-1.82.0.dll') {
    if (-not (Test-Path (Join-Path $release $f))) { throw "release\$f is missing. Run the protected release build first." }
}
foreach ($f in 'eng.traineddata', 'osd.traineddata') {
    if (-not (Test-Path (Join-Path $languages $f))) { throw "OCR language files are missing. Run packaging\fetch-ocr-languages.ps1." }
}

$setup = Get-Content (Join-Path $packaging 'setup.iss') -Raw
if ($setup -notmatch '#define AppVersion "(\d+\.\d+\.\d+)"') { throw 'AppVersion not found in setup.iss.' }
$version = $Matches[1]
$built = (Get-Item (Join-Path $release 'PBJStudio.exe')).VersionInfo.ProductVersion
if (-not $built.StartsWith($version)) { throw "release\PBJStudio.exe is $built but setup.iss says $version. Rebuild the release." }

# ---- Layout -------------------------------------------------------------------
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
foreach ($d in '', 'x64', 'tessdata', 'licenses', 'Images') { New-Item -ItemType Directory -Force (Join-Path $layout $d) | Out-Null }
Copy-Item (Join-Path $release 'PBJStudio.exe') $layout
Copy-Item (Join-Path $release 'x64\*.dll') (Join-Path $layout 'x64')
Copy-Item (Join-Path $languages '*.traineddata') (Join-Path $layout 'tessdata')
Copy-Item (Join-Path $packaging 'LICENSE.txt') $layout
Copy-Item (Join-Path $root 'docs\licenses\*') (Join-Path $layout 'licenses')

# ---- Tiles and icons, drawn from the seal ---------------------------------------
Add-Type -AssemblyName System.Drawing
$seal = [System.Drawing.Image]::FromFile((Join-Path $root 'src\KcsPdfSigner.App\Assets\seal-1024.png'))
function Save-Tile([string]$name, [int]$w, [int]$h, [double]$fill) {
    $bitmap = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.SmoothingMode = 'HighQuality'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $side = [Math]::Round([Math]::Min($w, $h) * $fill)
    $g.DrawImage($seal, [int](($w - $side) / 2), [int](($h - $side) / 2), [int]$side, [int]$side)
    $g.Dispose()
    $bitmap.Save((Join-Path $layout "Images\$name"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}
foreach ($scale in 100, 200, 400) {
    $k = $scale / 100
    Save-Tile "StoreLogo.scale-$scale.png" (50 * $k) (50 * $k) 1.0
    Save-Tile "Square44x44Logo.scale-$scale.png" (44 * $k) (44 * $k) 0.92
    Save-Tile "Square150x150Logo.scale-$scale.png" (150 * $k) (150 * $k) 0.66
    Save-Tile "Wide310x150Logo.scale-$scale.png" (310 * $k) (150 * $k) 0.66
}
foreach ($size in 16, 24, 32, 48, 256) {
    Save-Tile "Square44x44Logo.targetsize-$size.png" $size $size 1.0
    Save-Tile "Square44x44Logo.targetsize-${size}_altform-unplated.png" $size $size 1.0
}
$seal.Dispose()

# ---- Manifest, resource index, package -------------------------------------------
$identity = Get-Content (Join-Path $here 'identity.json') -Raw | ConvertFrom-Json
$manifest = Get-Content (Join-Path $here 'AppxManifest.template.xml') -Raw
$values = @{
    IdentityName = $identity.IdentityName; Publisher = $identity.Publisher
    PublisherDisplayName = $identity.PublisherDisplayName; DisplayName = $identity.DisplayName; Version = "$version.0"
}
foreach ($key in $values.Keys) { $manifest = $manifest.Replace("{$key}", [System.Security.SecurityElement]::Escape($values[$key])) }
[System.IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, (New-Object System.Text.UTF8Encoding $false))

$priConfig = Join-Path $dist 'msix-priconfig.xml'
& $makepri createconfig /cf $priConfig /dq en-US /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makepri createconfig failed.' }
& $makepri new /pr $layout /cf $priConfig /mn (Join-Path $layout 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makepri new failed.' }

$package = Join-Path $dist "PBJStudio-$version.msix"
$log = & $makeappx pack /d $layout /p $package /o
if ($LASTEXITCODE -ne 0) { $log | Select-Object -Last 15; throw 'makeappx pack failed.' }

$size = [Math]::Round((Get-Item $package).Length / 1MB, 1)
"Built $package ($size MB), identity $($identity.IdentityName) $version.0"
if ($identity.IdentityName -like 'PLACEHOLDER*') { 'NOTE: identity.json still holds placeholders; the Store will reject this package until they are replaced.' }
