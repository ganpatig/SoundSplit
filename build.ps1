# Tests SoundSplit, publishes it and zips it.
# Output: publish\SoundSplit.exe and dist\SoundSplit-<version>-win-x64.zip
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# A running copy locks its own files; replacing the folder underneath it would leave a mix of old and new.
$running = Get-Process SoundSplit -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$root\publish\*" }
if ($running) { throw "SoundSplit is running from $root\publish. Close it and run this again." }

dotnet test "$root\tests\SoundSplit.Tests\SoundSplit.Tests.csproj" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Tests failed" }

Remove-Item "$root\publish" -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$root\src\SoundSplit\SoundSplit.csproj" -c Release -r win-x64 --self-contained true -o "$root\publish" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Zip with a top-level SoundSplit folder, so unzipping doesn't scatter 400 files.
$version = ([xml](Get-Content "$root\src\SoundSplit\SoundSplit.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$stage = Join-Path ([IO.Path]::GetTempPath()) "SoundSplit-stage-$PID"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$stage\SoundSplit" | Out-Null
Copy-Item "$root\publish\*" "$stage\SoundSplit" -Recurse
Copy-Item "$root\README.md", "$root\LICENSE" "$stage\SoundSplit"
New-Item -ItemType Directory -Force "$root\dist" | Out-Null
$zip = "$root\dist\SoundSplit-$version-win-x64.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
# Entries are written one by one with "/" separators: Windows PowerShell 5.1's zip helpers write "\",
# which unzip tools other than Explorer mangle.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem $stage -Recurse -File) {
        $name = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally {
    $archive.Dispose()
}
Remove-Item $stage -Recurse -Force

Get-Item "$root\publish\SoundSplit.exe", $zip | Select-Object FullName, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }, LastWriteTime
