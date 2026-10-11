param([switch]$Smoke)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
# Vendored decoder is pinned as well as the compiler. No network dependency for it.
foreach ($line in Get-Content (Join-Path $root 'vendor/SHA256SUMS.txt')) {
    if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith('#')) { continue }
    $parts = $line -split '  ',2
    if ($parts.Count -ne 2) { throw 'Vendored decoder manifest is malformed' }
    $vendoredPath = Join-Path $root ('vendor/' + $parts[1])
    if (!(Test-Path -LiteralPath $vendoredPath)) { throw "Vendored decoder file is missing: $($parts[1])" }
    if ((Get-FileHash $vendoredPath).Hash -ne $parts[0]) {
        throw 'Vendored decoder SHA256 mismatch'
    }
}
$cache = Join-Path $root '.toolchain'
$archive = Join-Path $cache 'compiler.7z'
$compiler = Join-Path $cache 'mingw32/bin/g++.exe'
$sha = '4A8A3F58C88EC6FF6F0270913F01BF2C0BC1366C39AFDA32BE05BC0D3BAF5126'
$url = 'https://github.com/niXman/mingw-builds-binaries/releases/download/14.2.0-rt_v12-rev0/i686-14.2.0-release-win32-dwarf-msvcrt-rt_v12-rev0.7z'
New-Item -ItemType Directory -Force $cache | Out-Null
if (!(Test-Path $archive)) {
    & curl.exe -L --fail --retry 2 -o "$archive.partial" $url
    if ($LASTEXITCODE -ne 0) { throw 'Compiler download failed' }
    if ((Get-FileHash "$archive.partial").Hash -ne $sha) { throw 'Compiler archive SHA256 mismatch' }
    Move-Item -LiteralPath "$archive.partial" -Destination $archive
}
if ((Get-FileHash $archive).Hash -ne $sha) { throw 'Compiler archive SHA256 mismatch' }
if (!(Test-Path $compiler)) {
    $windowsTar = Join-Path $env:SystemRoot 'System32/tar.exe'
    if (!(Test-Path -LiteralPath $windowsTar)) { throw 'Windows system tar.exe not found' }
    & $windowsTar -xf $archive -C $cache
    if ($LASTEXITCODE -ne 0) { throw 'Windows tar extraction failed' }
}
$out = Join-Path $root 'out'
New-Item -ItemType Directory -Force $out | Out-Null
$common = @('-std=c++17','-O2','-Wall','-Wextra','-Werror','-Wno-cast-function-type','-fno-exceptions','-fno-rtti','-static','-static-libgcc','-static-libstdc++','-D_WIN32_WINNT=0x0601','-municode','-Wl,--no-insert-timestamp')
& $compiler --version | Out-File (Join-Path $out 'compiler.txt')
Push-Location $root
$oldPath = $env:PATH
$env:PATH = (Split-Path $compiler) + ';' + $env:PATH
try {
    & $compiler @common -shared overlay.cpp vendor/hde/hde32.c -o out/Ra3Overlay.dll -lgdi32 -luser32
    if ($LASTEXITCODE -ne 0) { throw 'Overlay compile failed' }
    New-Item -ItemType Directory -Force out/product | Out-Null
    & $compiler @common -DOVERLAY_PRODUCT -DOVERLAY_PANEL -shared overlay.cpp product-proxy.cpp vendor/hde/hde32.c -o out/product/Ra3Overlay.dll '-Wl,--kill-at' -lgdi32 -luser32 -ldxguid
    if ($LASTEXITCODE -ne 0) { throw 'Product proxy compile failed' }
    & $compiler @common product-tests.cpp -o out/ProductTests.exe -ldxguid
    if ($LASTEXITCODE -ne 0) { throw 'Product test compile failed' }
    & $compiler @common harness.cpp vendor/hde/hde32.c -o out/Dx9OverlayHarness.exe -ld3d9 -lgdi32 -luser32
    if ($LASTEXITCODE -ne 0) { throw 'Harness compile failed' }
    & $compiler @common contract-tests.cpp -o out/ContractTests.exe -lgdi32 -luser32
    if ($LASTEXITCODE -ne 0) { throw 'Contract test compile failed' }
    & $compiler @common harness-table-tests.cpp vendor/hde/hde32.c -o out/HarnessTableTests.exe -ld3d9 -lgdi32 -luser32
    if ($LASTEXITCODE -ne 0) { throw 'Harness table test compile failed' }
    & $compiler @common abi-probe.cpp -o out/AbiProbe.exe
    if ($LASTEXITCODE -ne 0) { throw 'D3D9 ABI verification failed' }
    & $compiler @common code-hook-tests.cpp vendor/hde/hde32.c -o out/CodeHookTests.exe -ld3d9 -lgdi32 -luser32
    if ($LASTEXITCODE -ne 0) { throw 'Code hook test compile failed' }
    & $compiler @common engine-hook-tests.cpp vendor/hde/hde32.c -o out/EngineHookTests.exe -lgdi32 -luser32
    if ($LASTEXITCODE -ne 0) { throw 'Engine probe compile failed' }
    & (Join-Path $out 'EngineHookTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Engine probe test failed' }
    & $compiler @common engine-lua-tests.cpp vendor/hde/hde32.c -o out/EngineLuaTests.exe -lgdi32 -luser32
    if ($LASTEXITCODE -ne 0) { throw 'Engine Lua call compile failed' }
    & (Join-Path $out 'EngineLuaTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Engine Lua call test failed' }
    & $compiler @common aob-scan-tests.cpp vendor/hde/hde32.c -o out/AobScanTests.exe -lgdi32 -luser32
    if ($LASTEXITCODE -ne 0) { throw 'AOB scan fixture compile failed' }
    & (Join-Path $out 'AobScanTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'AOB scan fixture failed' }
    & $compiler @common entry-diagnostics-tests.cpp -o out/EntryDiagnosticsTests.exe
    if ($LASTEXITCODE -ne 0) { throw 'Entry diagnostics test compile failed' }
    & $compiler @common entry-format-tests.cpp -o out/EntryFormatTests.exe -lgdi32 -luser32 -ldxguid
    if ($LASTEXITCODE -ne 0) { throw 'Entry format test compile failed' }
    & $compiler @common entry-relocation-tests.cpp -o out/EntryRelocationTests.exe -lgdi32 -luser32 -ldxguid
    if ($LASTEXITCODE -ne 0) { throw 'Entry relocation test compile failed' }
    & $compiler @common texture-draw-tests.cpp -o out/TextureDrawTests.exe -lgdi32 -luser32
    if ($LASTEXITCODE -ne 0) { throw 'Texture draw test compile failed' }
    & (Join-Path $out 'TextureDrawTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Texture draw tests failed' }
    & $compiler @common product-texture-tests.cpp vendor/hde/hde32.c -o out/ProductTextureTests.exe -lgdi32 -luser32
    if ($LASTEXITCODE -ne 0) { throw 'Product texture test compile failed' }
    & (Join-Path $out 'ProductTextureTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Product texture tests failed' }
    & $compiler @common thread-policy-tests.cpp vendor/hde/hde32.c -o out/ThreadPolicyTests.exe -lgdi32 -luser32
    if ($LASTEXITCODE -ne 0) { throw 'Thread policy fixture compile failed' }
    & (Join-Path $out 'ThreadPolicyTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Thread policy fixture failed' }
    & $compiler @common real-msaa-probe.cpp -o out/RealMsaaProbe.exe -ld3d9 -lgdi32 -luser32
    if ($LASTEXITCODE -ne 0) { throw 'Real MSAA probe compile failed' }
    & $compiler @common panel-tests.cpp vendor/hde/hde32.c -o out/PanelTests.exe -lgdi32 -luser32
& $compiler @common panel-input-tests.cpp -o out/PanelInputTests.exe -lgdi32 -luser32
    if ($LASTEXITCODE -ne 0) { throw 'Panel fixture compile failed' }
    & (Join-Path $out 'PanelTests.exe')
& (Join-Path $out 'PanelInputTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Panel fixture failed' }
    foreach ($test in @('EntryRelocationTests.exe','AbiProbe.exe','HarnessTableTests.exe','ContractTests.exe','CodeHookTests.exe','ProductTests.exe','EntryDiagnosticsTests.exe','EntryFormatTests.exe')) {
        & (Join-Path $out $test)
        if ($LASTEXITCODE -ne 0) { throw "$test failed" }
    }
    if (Test-Path loader.cpp) {
        & $compiler @common loader.cpp -o out/OfflineLoader.exe
        if ($LASTEXITCODE -ne 0) { throw 'Loader compile failed' }
    }
    New-Item -ItemType Directory -Force out/proxy-probe | Out-Null
    & $compiler @common -shared proxy-marker.cpp -o out/proxy-probe/d3d9.dll
    if ($LASTEXITCODE -ne 0) { throw 'Proxy sentinel compile failed' }
    & $compiler @common proxy-probe.cpp -o out/proxy-probe/ProxyProbe.exe
    if ($LASTEXITCODE -ne 0) { throw 'Proxy probe compile failed' }
    $objdump = Join-Path $cache 'mingw32/bin/objdump.exe'
    $inspectFiles = @(Get-ChildItem $out -File | Where-Object Extension -In '.dll','.exe') + @(Get-Item (Join-Path $out 'product/Ra3Overlay.dll'))
    foreach ($file in $inspectFiles) {
        $inspection = & $objdump -p $file.FullName
        if ($LASTEXITCODE -ne 0) { throw 'PE inspection failed' }
        $inspection | Out-File ($file.FullName + '.pe.txt')
        if ($inspection -match 'DLL Name:.*(d3dx|libgcc|libstdc|libwinpthread|vcruntime|msvcp)') { throw 'Unexpected third-party runtime dependency' }
        if (!($inspection -match 'pei-i386')) { throw 'Not an x86 PE' }
    }
    $products = @('PanelInputTests.exe','PanelTests.exe','ThreadPolicyTests.exe','ProductTextureTests.exe','TextureDrawTests.exe','RealMsaaProbe.exe','EntryRelocationTests.exe','EntryFormatTests.exe','EntryDiagnosticsTests.exe','ProductTests.exe','CodeHookTests.exe','AbiProbe.exe','ContractTests.exe','Dx9OverlayHarness.exe','HarnessTableTests.exe',
        'OfflineLoader.exe','Ra3Overlay.dll','product/Ra3Overlay.dll','proxy-probe/d3d9.dll','proxy-probe/ProxyProbe.exe')
    $hashLines = foreach ($product in $products) {
        $productPath = Join-Path $out $product
        # loader.cpp is optional: name the product that is missing instead of failing
        # inside Get-FileHash with an opaque path-not-found error.
        if (!(Test-Path -LiteralPath $productPath)) { throw "Product was not built: $product" }
        '{0}  {1}' -f (Get-FileHash $productPath -Algorithm SHA256).Hash, $product
    }
    $hashLines | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding ASCII
    $hashLines | Write-Output
    if ($Smoke) {
        Push-Location $out
        try {
            & ./Dx9OverlayHarness.exe --smoke
            if ($LASTEXITCODE -ne 0) { throw 'Offline smoke failed' }
        } finally { Pop-Location }
    }
} finally { $env:PATH = $oldPath; Pop-Location }
