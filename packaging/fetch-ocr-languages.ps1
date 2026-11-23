# Downloads the OCR language files (Tesseract tessdata_fast, Apache-2.0) into packaging\ocr-languages,
# checks each one against the size and SHA-256 pinned below (the same values as
# src\KcsPdfSigner.Core\Documents\OcrLanguages.cs), and skips files that are already there and correct.
#
#   powershell -ExecutionPolicy Bypass -File packaging\fetch-ocr-languages.ps1
#
# eng (English) and osd (page orientation) are needed to build and test the program and the installer.
# The ten Indian languages are optional installer components; setup.iss leaves out any that are missing.
# The folder is in .gitignore: never commit these files.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$base = 'https://github.com/tesseract-ocr/tessdata_fast/raw/4.1.0/'
$target = Join-Path $PSScriptRoot 'ocr-languages'
New-Item -ItemType Directory -Force -Path $target | Out-Null

# code, size in bytes, SHA-256
$packs = @(
    @('eng', 4113088,  '7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2'),
    @('osd', 10562727, '9cf5d576fcc47564f11265841e5ca839001e7e6f38ff7f7aacf46d15a96b00ff'),
    @('hin', 1122751,  '4c73ffc59d497c186b19d1e90f5d721d678ea6b2e277b719bee4e2af12271825'),
    @('kan', 3608331,  'bd31e6b6ae93271e3bcf5383d306d8eefbb91542937cd6d735a5930c970e61d8'),
    @('mar', 2118233,  '0ba3f2d116972e72fe9e176bc84c38e81dfb6670f4ed1f7f6c8e16a27da7cb61'),
    @('tam', 3237963,  'd02fbec24be4b07e32e80d0ccfc3b6b67a3c5d61c9d0a7c8532677990912c6ec'),
    @('tel', 2769654,  'd10691fddd5b67802e1c12800ebb321d3b8bcd8d24a2ac3ff206f93188c04ab5'),
    @('mal', 5275996,  'bd05cbf1b197e7810d2903419aedb06f9ef77bfedf50b358673c1d18d707cdb4'),
    @('guj', 1418394,  'fa69658614b4946a9afae8853d67e0689838803dfa3d12c2e35ec53ee6f8df34'),
    @('ben', 855841,   '31163084c279aaebd376216f0c3d5c17ad4b5fee8db49dae79c20000b5de5964'),
    @('pan', 497721,   '1ec0907fc3534065ea9ae190c6bb7ec9e5c74fd9d2fa996aaec7407f11ad8131'),
    @('urd', 1398718,  '62e8250ce2a994106e313a82e26a516a39e2cf159d0ce3c5b5008387fd0d555f')
)

function Test-Pack([string]$path, [long]$size, [string]$sha) {
    if (-not (Test-Path $path)) { return $false }
    if ((Get-Item $path).Length -ne $size) { return $false }
    return ((Get-FileHash -Algorithm SHA256 -Path $path).Hash.ToLowerInvariant() -eq $sha)
}

$failed = 0
foreach ($pack in $packs) {
    $code, $size, $sha = $pack
    $file = Join-Path $target "$code.traineddata"
    if (Test-Pack $file $size $sha) { Write-Host "ok        $code (already there)"; continue }

    $part = "$file.part"
    try {
        Invoke-WebRequest -UseBasicParsing -Uri ($base + "$code.traineddata") -OutFile $part
        if (Test-Pack $part $size $sha) {
            Move-Item -Force $part $file
            Write-Host ("fetched   {0} ({1:N1} MB)" -f $code, ($size / 1MB))
        } else {
            Remove-Item -Force $part
            Write-Host "REFUSED   $code (size or checksum does not match the pinned value)"
            $failed++
        }
    } catch {
        if (Test-Path $part) { Remove-Item -Force $part }
        Write-Host "FAILED    $code ($($_.Exception.Message))"
        $failed++
    }
}

if ($failed -gt 0) { Write-Host "$failed file(s) missing. eng and osd are required; the others are optional."; exit 1 }
Write-Host "All 12 language files are in $target"
