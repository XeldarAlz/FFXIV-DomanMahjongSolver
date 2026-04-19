# Patch the DalamudPackager-built latest.zip with images/icon.png
# Invoked from the csproj BundleIcon target.
param(
  [Parameter(Mandatory=$true)][string]$ZipPath,
  [Parameter(Mandatory=$true)][string]$IconPath
)

if (-not (Test-Path $ZipPath)) {
  Write-Error "zip not found: $ZipPath"
  exit 1
}
if (-not (Test-Path $IconPath)) {
  Write-Error "icon not found: $IconPath"
  exit 1
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

$zip = [IO.Compression.ZipFile]::Open($ZipPath, 'Update')
try {
  # Remove any existing entry at the target path so re-runs stay idempotent.
  $existing = @($zip.Entries | Where-Object { $_.FullName -eq 'images/icon.png' })
  foreach ($e in $existing) { $e.Delete() }

  [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $IconPath, 'images/icon.png')
  Write-Host "bundled icon into $ZipPath"
} finally {
  $zip.Dispose()
}
