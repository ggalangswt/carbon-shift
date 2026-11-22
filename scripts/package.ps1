param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.0',
    [string]$PythonExecutable = 'python'
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$buildRoot = Join-Path $artifactsRoot ('build-' + [guid]::NewGuid().ToString('N'))
$packageName = "CarbonShift-Submission-win-x64-$Version"
$packageRoot = Join-Path $buildRoot $packageName
$appRoot = Join-Path $packageRoot 'Aplikasi'
New-Item -ItemType Directory -Force -Path $appRoot | Out-Null

function Invoke-Checked {
    param([string]$Program, [string[]]$Arguments)
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $Program" }
}

Push-Location $repoRoot
try {
    Invoke-Checked -Program 'dotnet' -Arguments @('test','Form/CarbonShift.slnx','-c','Release','--nologo')
    Invoke-Checked -Program 'dotnet' -Arguments @('publish','Form/CarbonShift/CarbonShift.csproj','-p:PublishProfile=Submission',"-p:Version=$Version",'-o',$appRoot,'--nologo')
    foreach ($runtimeFile in @('CarbonShift.exe','hostfxr.dll','coreclr.dll','System.Windows.Forms.dll','Npgsql.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $appRoot $runtimeFile))) { throw "Incomplete runtime: $runtimeFile" }
    }
    $forbidden = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse | Where-Object {
        $_.Name -eq 'database.config' -or $_.Extension -eq '.pdb' -or $_.Name -like 'CarbonShift.Tests*'
    })
    if ($forbidden.Count -gt 0) { throw 'Publish includes private configuration, debug symbols, or test assemblies.' }
    $runtime = Get-Content -Raw -LiteralPath (Join-Path $appRoot 'CarbonShift.runtimeconfig.json') | ConvertFrom-Json
    if ($runtime.runtimeOptions.frameworks -or -not $runtime.runtimeOptions.includedFrameworks) { throw 'Publish is not self-contained.' }

    $guideSource = Join-Path $packageRoot 'Dokumentasi'
    $dbOutput = Join-Path $packageRoot 'Database'
    New-Item -ItemType Directory -Path $guideSource,$dbOutput | Out-Null
    $markdown = [System.IO.File]::ReadAllText((Join-Path $repoRoot 'docs/PANDUAN_PENGGUNA.md')).Replace('Versi 1.0.0', "Versi $Version")
    [System.IO.File]::WriteAllText((Join-Path $guideSource 'Panduan-Pengguna.md'),$markdown,[System.Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/images') -Destination (Join-Path $guideSource 'images') -Recurse
    foreach ($sql in @('schema.psql','schema.sql','seed.sql')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot "database/$sql") -Destination $dbOutput
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'Form/CarbonShift/database.config.example') -Destination $dbOutput
    Invoke-Checked -Program $PythonExecutable -Arguments @((Join-Path $repoRoot 'scripts/build_guide.py'),'--source',(Join-Path $guideSource 'Panduan-Pengguna.md'),'--output',(Join-Path $packageRoot 'Panduan-Pengguna.pdf'),'--version',$Version)
    $instructions = @"
CARBONSHIFT $Version - MULAI DI SINI
1. Ekstrak SELURUH ZIP ke folder yang dapat ditulis, misalnya Documents.
2. Pasang PostgreSQL lokal jika belum tersedia dan pastikan layanannya aktif.
   Unduh dari https://www.postgresql.org/download/windows/
3. Buka folder Aplikasi, lalu jalankan CarbonShift.exe.
   .NET/Visual Studio tidak perlu dipasang.
4. Pada wizard, isi host localhost, port 5432, database carbonshift, dan akun PostgreSQL.
   Uji koneksi, lalu Siapkan dan simpan. Data DB lama yang sesuai tidak dihapus.
5. Daftar akun atau masuk, lalu pilih kota di Dashboard untuk mengambil forecast.
Baca Panduan-Pengguna.pdf untuk panduan bergambar.
Sumber panduan berada di Dokumentasi/Panduan-Pengguna.md.
Folder Database berisi SQL untuk setup manual jika diperlukan.
Pertahankan seluruh isi folder Aplikasi. Semua DLL/runtime di dalamnya diperlukan.
PostgreSQL tidak disertakan. Forecast live membutuhkan internet.
Untuk DB baru, akun contoh adalah admin@carbonshift.com dengan password 12345.
Laporan testing Modul 10 kelompok ditambahkan terpisah oleh pengumpul.
"@
    [System.IO.File]::WriteAllText((Join-Path $packageRoot 'MULAI-DI-SINI.txt'),$instructions,[System.Text.UTF8Encoding]::new($false))
    $commit = (& git rev-parse HEAD).Trim()
    $workspace = if ((& git status --porcelain).Count -gt 0) { 'modified (uncommitted changes included)' } else { 'clean' }
    $information = @"
Version: $Version
Target: Windows x64, self-contained
Source commit: $commit
Workspace: $workspace
Built UTC: $([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'))
Repository: https://github.com/ggalangswt/carbon-shift
"@
    [System.IO.File]::WriteAllText((Join-Path $packageRoot 'INFO-VERSI.txt'),$information,[System.Text.UTF8Encoding]::new($false))
    $zip = Join-Path $artifactsRoot ($packageName + '.zip')
    Compress-Archive -LiteralPath $packageRoot -DestinationPath $zip -CompressionLevel Optimal -Force
    $hash = Get-FileHash -LiteralPath $zip -Algorithm SHA256
    [System.IO.File]::WriteAllText(($zip + '.sha256'),($hash.Hash.ToLowerInvariant() + '  ' + [System.IO.Path]::GetFileName($zip) + [Environment]::NewLine),[System.Text.UTF8Encoding]::new($false))
    Write-Output "Package: $zip"
    Write-Output "Staging: $packageRoot"
    Write-Output "SHA256: $($hash.Hash)"
}
finally { Pop-Location }
