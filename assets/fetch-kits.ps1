#Requires -Version 5.1
<#
.SYNOPSIS
    Puts the four Kenney art kits where the 3D views expect them.

.DESCRIPTION
    assets/kit/ is deliberately not in the repository — about 10 MB of GLB, decided 11 September
    2026 — so a fresh clone draws placeholders until the kits are fetched. This is the fetch.

    It reads each asset's page on kenney.nl and takes the .zip link from it rather than holding a
    download URL of its own. Kenney's URLs carry a cache-busting hash that changes whenever an
    asset is updated, so a hardcoded link would work until the day it quietly didn't.

    Everything is CC0: no attribution required, though LICENSE-kenney.txt travels with each kit.

.PARAMETER Force
    Fetch kits that are already present, replacing them.

.PARAMETER From
    A folder of Kenney .zip files already downloaded, used instead of fetching anything. For an
    offline machine, and for testing the unpacking without going to the network.

.EXAMPLE
    ./assets/fetch-kits.ps1
    ./assets/fetch-kits.ps1 -Force
    ./assets/fetch-kits.ps1 -From ~/Downloads -Force
#>
[CmdletBinding()]
param(
    [switch] $Force,
    [string] $From
)

$ErrorActionPreference = 'Stop'

# Folder name under assets/kit, and the asset's slug on kenney.nl. The folder names are what
# art.ts and rooms.ts prefix their model paths with, so they are not free to change.
$kits = @(
    @{ Folder = 'city';   Slug = 'city-kit-commercial'; Title = 'City Kit (Commercial)' }
    @{ Folder = 'forest'; Slug = 'mini-forest';         Title = 'Mini Forest' }
    @{ Folder = 'folk';   Slug = 'mini-characters';     Title = 'Mini Characters' }
    @{ Folder = 'room';   Slug = 'furniture-kit';       Title = 'Furniture Kit' }
)

$root = Join-Path $PSScriptRoot 'kit'
New-Item -ItemType Directory -Force -Path $root | Out-Null

$failed = @()

function Get-ZipUrl {
    param([string] $Slug)

    $page = "https://kenney.nl/assets/$Slug"
    Write-Host "  reading $page"

    $html = (Invoke-WebRequest -Uri $page -UseBasicParsing).Content
    $match = [regex]::Match($html, 'https://kenney\.nl/media/pages/assets/[^"'' ]+\.zip')

    if (-not $match.Success) {
        throw "No download link on $page. Kenney may have changed the page; fetch it by hand (see assets/README.md)."
    }

    return $match.Value
}

foreach ($kit in $kits) {
    $target = Join-Path $root $kit.Folder
    $already = (Test-Path $target) -and (Get-ChildItem -Path $target -Filter *.glb -ErrorAction SilentlyContinue)

    if ($already -and -not $Force) {
        Write-Host "$($kit.Title): already here, skipping. Use -Force to replace it." -ForegroundColor DarkGray
        continue
    }

    Write-Host "$($kit.Title) -> assets/kit/$($kit.Folder)" -ForegroundColor Cyan

    $work = Join-Path ([System.IO.Path]::GetTempPath()) ("emberloom-kit-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $work | Out-Null

    try {
        $zip = Join-Path $work 'kit.zip'

        if ($From) {
            $have = Get-ChildItem -Path $From -Filter "*$($kit.Slug)*.zip" -File | Select-Object -First 1
            if (-not $have) { throw "No zip matching '$($kit.Slug)' in $From." }

            Write-Host "  using $($have.Name)"
            Copy-Item $have.FullName $zip
        }
        else {
            $url = Get-ZipUrl -Slug $kit.Slug
            Write-Host "  downloading $(Split-Path $url -Leaf)"
            Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
        }

        Expand-Archive -Path $zip -DestinationPath (Join-Path $work 'out') -Force

        # Found rather than assumed: the folder holding the GLBs is named "GLB format" in every
        # kit so far, but it has moved between being under Models/ and being at the top.
        $models = Get-ChildItem -Path (Join-Path $work 'out') -Recurse -Directory |
            Where-Object { $_.Name -like '*GLB*' } |
            Select-Object -First 1

        if (-not $models) {
            throw "No GLB folder inside $(Split-Path $zip -Leaf). Unpack it by hand (see assets/README.md)."
        }

        if (Test-Path $target) { Remove-Item -Recurse -Force $target }
        New-Item -ItemType Directory -Force -Path $target | Out-Null

        # Only the models and their textures are copied out, by extension. The archive comes off
        # the internet, and there is no reason for anything else in it to land in the project.
        Get-ChildItem -Path $models.FullName -Filter *.glb -File |
            Copy-Item -Destination $target

        # **The textures must come too.** The GLBs reference colormap.png externally rather than
        # embedding it, so a kit folder without its Textures/ loads without error and renders
        # every model pure white — which looks like a lighting bug and is not one.
        $textures = Get-ChildItem -Path (Join-Path $work 'out') -Recurse -Directory |
            Where-Object { $_.Name -eq 'Textures' } |
            Select-Object -First 1

        $painted = 0
        if ($textures) {
            $into = Join-Path $target 'Textures'
            New-Item -ItemType Directory -Force -Path $into | Out-Null

            # Note the trailing *: with -Path pointing at the folder itself, -Include is matched
            # against the folder's own name and quietly copies nothing. That is the bug this script
            # exists to prevent, so it is also the bug it is most embarrassing to ship - and it is
            # why the count below is checked rather than the folder's existence.
            Get-ChildItem -Path (Join-Path $textures.FullName '*') -Include *.png, *.jpg -File |
                Copy-Item -Destination $into

            $painted = (Get-ChildItem -Path $into -File).Count
        }

        $count = (Get-ChildItem -Path $target -Filter *.glb).Count
        Write-Host "  $count models, $painted texture$(if ($painted -eq 1) { '' } else { 's' })" -ForegroundColor Green

        if ($painted -eq 0) {
            # Some kits embed their textures in the GLB and genuinely have none to copy; the rest
            # reference colormap.png externally and render pure white without it, which looks like
            # a lighting bug and is not one. Saying so is cheaper than finding out later.
            Write-Warning "  no textures came with $($kit.Title). If it renders white, that is why."
        }

        $licence = Get-ChildItem -Path (Join-Path $work 'out') -Recurse -File |
            Where-Object { $_.Name -like 'License*' -or $_.Name -like 'LICENSE*' } |
            Select-Object -First 1

        if ($licence) { Copy-Item $licence.FullName (Join-Path $root 'LICENSE-kenney.txt') -Force }

    }
    catch {
        # One kit failing should not cost the other three. A clone with three kits and a warning
        # is a working game missing some scenery; a clone with none is placeholders everywhere.
        Write-Warning "  $($kit.Title): $($_.Exception.Message)"
        $failed += $kit.Title
    }
    finally {
        Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    }
}

Write-Host ''

if ($failed.Count -gt 0) {
    Write-Warning "Could not fetch: $($failed -join ', '). The views draw placeholders for whatever is missing and say so."
    exit 1
}

Write-Host 'Done. The API serves these at /kit — see assets/README.md.' -ForegroundColor Cyan
