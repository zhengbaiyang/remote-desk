$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $project = 'src/RemoteDesk/RemoteDesk.csproj'
    $version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version
    $work = "artifacts/build/$([guid]::NewGuid().ToString('N'))"
    $stage = "$work/app"
    $output = "artifacts/releases/$version"

    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Could not restore the pinned packaging tool.' }
    dotnet publish $project -c Release -r win-x64 --self-contained true -p:DebugType=None -o $stage
    if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }

    Copy-Item -LiteralPath LICENSE,packaging/Velopack-LICENSE.txt -Destination $stage
    $assets = Get-Content src/RemoteDesk/obj/project.assets.json -Raw | ConvertFrom-Json
    $runtime = Get-Content "$stage/RemoteDesk.runtimeconfig.json" -Raw | ConvertFrom-Json
    foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
        $package = $framework.name.ToLowerInvariant() + '.runtime.win-x64/' + $framework.version
        $directory = $assets.packageFolders.PSObject.Properties.Name | ForEach-Object { Join-Path $_ $package } |
            Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (!$directory) { throw "Missing runtime license directory for $package" }
        foreach ($notice in Get-ChildItem $directory -File | Where-Object Name -Match '^(LICENSE|THIRD-PARTY-NOTICES)(\.txt)?$') {
            Copy-Item -LiteralPath $notice.FullName -Destination "$stage/$($framework.name)-$($notice.Name)"
        }
    }

    # Keep the installation separate from the legacy per-user settings directory.
    dotnet vpk pack --packId RemoteDesk.Windows --packTitle RemoteDesk --packAuthors zhengbaiyang --packVersion $version --packDir $stage --mainExe RemoteDesk.exe --icon src/RemoteDesk/Assets/remotedesk.ico --runtime win-x64 --channel win --delta None --noPortable --shortcuts StartMenuRoot --outputDir "$work/release"
    if ($LASTEXITCODE -ne 0) { throw 'Installer packaging failed.' }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Copy-Item -LiteralPath "$work/release/RemoteDesk.Windows-win-Setup.exe","$work/release/RemoteDesk.Windows-$version-full.nupkg","$work/release/releases.win.json" -Destination $output
    Write-Host "Installer and update feed: $output"
} finally {
    Pop-Location
}
