$ErrorActionPreference = 'Stop'
# Pinned upstream release; downloaded only on ephemeral native Windows CI runners.
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'CI bootstrap only. Install Inno Setup manually on development machines.' }
$compilerInstaller = Join-Path $env:RUNNER_TEMP 'innosetup-6.7.3.exe'
$compilerDirectory = Join-Path $env:RUNNER_TEMP 'SamsungController Inno Setup'
Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $compilerInstaller
$expectedHash = '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732'
if ((Get-FileHash $compilerInstaller -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Inno Setup checksum mismatch' }
$result = Start-Process -FilePath $compilerInstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', ('/DIR="' + $compilerDirectory + '"')) -Wait -PassThru
if ($result.ExitCode -ne 0) { throw "Inno Setup installation failed: $($result.ExitCode)" }
$compiler = Join-Path $compilerDirectory 'ISCC.exe'
if (-not (Test-Path $compiler)) { throw 'Inno Setup compiler was not installed' }
"SAMSUNG_INNO_COMPILER=$compiler" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
