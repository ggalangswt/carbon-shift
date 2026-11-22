param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.0',
    [string]$PythonExecutable = 'python',
    [string]$SupabaseUrl,
    [string]$SupabasePublishableKey
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$settingsPath = Join-Path $repoRoot 'Form/CarbonShift/supabase.public.json'
if ((-not $SupabaseUrl -or -not $SupabasePublishableKey) -and (Test-Path -LiteralPath $settingsPath)) {
    $publicSettings = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
    if (-not $SupabaseUrl) { $SupabaseUrl = $publicSettings.url }
    if (-not $SupabasePublishableKey) { $SupabasePublishableKey = $publicSettings.publishableKey }
}
$endpoint = $null
if (-not [Uri]::TryCreate($SupabaseUrl,[UriKind]::Absolute,[ref]$endpoint) -or
    $endpoint.Scheme -ne 'https' -or $endpoint.AbsolutePath -ne '/' -or
    $endpoint.UserInfo -or $endpoint.Query -or $endpoint.Fragment -or
    $SupabaseUrl -match 'YOUR_|REPLACE|unconfigured|example\.') {
    throw 'Configure the real public Supabase URL before packaging. See docs/PENGELOLA.md.'
}
$publicKeyValid = $SupabasePublishableKey -match '^sb_publishable_[A-Za-z0-9_-]{12,}$'
if (-not $publicKeyValid -and $SupabasePublishableKey -match '^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$') {
    try {
        $payload = $SupabasePublishableKey.Split('.')[1].Replace('-','+').Replace('_','/')
        $payload = $payload.PadRight([int]([Math]::Ceiling($payload.Length / 4.0) * 4),'=')
        $claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json
        $publicKeyValid = $claims.role -eq 'anon'
    } catch { $publicKeyValid = $false }
}
if (-not $publicKeyValid -or $SupabasePublishableKey -match 'YOUR_|REPLACE|test_value') { throw 'Use a publishable or legacy anon key. Secret keys are not allowed.' }

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
    if ($env:CARBONSHIFT_TEST_LOCAL_DB -eq '1' -or $env:CARBONSHIFT_TEST_LIVE -eq '1') {
        throw 'Disable integration test flags before packaging. Packaging runs only default tests.'
    }
    Invoke-Checked -Program 'dotnet' -Arguments @('test','Form/CarbonShift.slnx','-c','Release','--nologo')
    Invoke-Checked -Program 'dotnet' -Arguments @('publish','Form/CarbonShift/CarbonShift.csproj','-p:PublishProfile=Submission','-p:DebugType=None','-p:DebugSymbols=false',"-p:Version=$Version","-p:SupabaseUrl=$SupabaseUrl","-p:SupabasePublishableKey=$SupabasePublishableKey",'-o',$appRoot,'--nologo')
    foreach ($runtimeFile in @('CarbonShift.exe','hostfxr.dll','coreclr.dll','System.Windows.Forms.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $appRoot $runtimeFile))) { throw "Incomplete runtime: $runtimeFile" }
    }
    $forbidden = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse | Where-Object {
        $_.Name -in @('database.config','supabase.config','local-source.config','backend.config','supabase.public.json','Npgsql.dll','prod-ca-2021.crt') -or
        $_.Extension -in @('.pdb','.dump','.pfx','.key') -or $_.Name -like 'CarbonShift.Tests*'
    })
    if ($forbidden.Count -gt 0) { throw 'Publish includes private configuration, debug symbols, or test assemblies.' }
    $runtime = Get-Content -Raw -LiteralPath (Join-Path $appRoot 'CarbonShift.runtimeconfig.json') | ConvertFrom-Json
    if ($runtime.runtimeOptions.frameworks -or -not $runtime.runtimeOptions.includedFrameworks) { throw 'Publish is not self-contained.' }

    $guideSource = Join-Path $packageRoot 'Dokumentasi'
    New-Item -ItemType Directory -Path $guideSource | Out-Null
    $markdown = [System.IO.File]::ReadAllText((Join-Path $repoRoot 'docs/PANDUAN_PENGGUNA.md')).Replace('Versi 1.0.0', "Versi $Version")
    [System.IO.File]::WriteAllText((Join-Path $guideSource 'Panduan-Pengguna.md'),$markdown,[System.Text.UTF8Encoding]::new($false))
    $imageOutput = Join-Path $guideSource 'images'
    New-Item -ItemType Directory -Path $imageOutput | Out-Null
    foreach ($match in [regex]::Matches($markdown,'!\[[^\]]*\]\((images/[^)]+)\)')) {
        Copy-Item -LiteralPath (Join-Path (Join-Path $repoRoot 'docs') $match.Groups[1].Value) -Destination $imageOutput
    }
    Invoke-Checked -Program $PythonExecutable -Arguments @((Join-Path $repoRoot 'scripts/build_guide.py'),'--source',(Join-Path $guideSource 'Panduan-Pengguna.md'),'--output',(Join-Path $packageRoot 'Panduan-Pengguna.pdf'),'--version',$Version)
    $instructions = @"
CARBONSHIFT $Version - MULAI DI SINI
1. Ekstrak seluruh ZIP ke folder pilihanmu.
2. Buka folder Aplikasi dan jalankan CarbonShift.exe.
3. Pastikan komputer terhubung ke internet.
4. Pilih Daftar jika belum punya akun, atau masuk dengan email dan passwordmu.
5. Pilih lokasi di Dashboard, lalu buat dan simpan jadwal melalui Scheduler.
Baca Panduan-Pengguna.pdf untuk panduan bergambar.
Pertahankan seluruh isi folder Aplikasi saat memindahkannya.

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
Supabase project URL: $SupabaseUrl
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
