# Increments version.txt's patch (third) component - run automatically after every successful
# local build, see PanguBridge.csproj's BumpVersion target.
$path = Join-Path $PSScriptRoot "version.txt"
$parts = (Get-Content $path -Raw).Trim().Split('.')
$parts[2] = [string]([int]$parts[2] + 1)
Set-Content -Path $path -Value ($parts -join '.') -NoNewline
