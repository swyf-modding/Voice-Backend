<#
    Version stamping, shared by the library and every mod built through it.

    Dot-source this and call Resolve-BuildVersion. A mod's build.ps1 resolves against its OWN
    repository and passes the result down, because this file lives in a submodule: run git here and
    you get mod-lib's tags, not the mod's.

        . (Join-Path $PSScriptRoot 'vendor\ScamWYF.Modding.Core\Version.ps1')
        $buildArgs.BuildVersion = Resolve-BuildVersion -Path $PSScriptRoot

    Why this exists at all: the mod version used to be a "1.0.0" literal typed into [BepInPlugin]. It
    was correct on the first release and wrong on every release after it, because nothing connected it
    to a tag - and it was worse than merely untidy, since Switchboard's Mods tab reads that exact
    string back out of the dll with Cecil and shows it to the player as the mod's version.

    Two output fields, because AssemblyVersion and AssemblyFileVersion must be numeric - the CLR
    rejects "1.0.0-beta.1" outright - so a prerelease tag keeps its numeric part there and carries the
    rest in AssemblyInformationalVersion, which is what Explorer displays.

    This is the same logic as Switchboard's build.ps1, duplicated rather than shared because the two
    are separate repositories with no common dependency. Keep them in step.
#>

function ConvertFrom-Describe {
    param([string]$Describe, [string]$Sha)

    if ([string]::IsNullOrWhiteSpace($Describe)) { $Describe = '0.0.0' }

    $dirty = $Describe.EndsWith('-dirty')
    if ($dirty) { $Describe = $Describe.Substring(0, $Describe.Length - '-dirty'.Length) }

    # Strip "-<n>-g<sha>" from the right first, then split what is left on its first '-'. That order is
    # the whole trick: "v1.1.0-beta.2-1-g9925471" has a dash inside the prerelease, so splitting first
    # would read the tag as "v1.1.0" and lose "beta.2".
    $commits = 0
    $distance = [regex]::Match($Describe, '-(?<n>\d+)-g(?<sha>[0-9a-fA-F]+)$')
    if ($distance.Success) {
        $commits = [int]$distance.Groups['n'].Value
        if (-not $Sha) { $Sha = $distance.Groups['sha'].Value }
        $Describe = $Describe.Substring(0, $distance.Index)
    }

    # Build metadata on the tag itself is dropped, because a commit suffix is appended below and two
    # "+" separators would make the result malformed. Git allows "+" in a tag name.
    $plus = $Describe.IndexOf('+')
    if ($plus -ge 0) { $Describe = $Describe.Substring(0, $plus) }

    $tag = $Describe
    $prerelease = ''
    $dash = $tag.IndexOf('-')
    if ($dash -ge 0) {
        $prerelease = $tag.Substring($dash + 1)
        $tag = $tag.Substring(0, $dash)
    }
    if ($tag -match '^[vV]') { $tag = $tag.Substring(1) }

    # Anything not dot-separated integers is not a version we can stamp: an untagged tree, or a tag
    # like "nightly". Both fall back to 0.0.0 and are marked, rather than failing the build. Up to four
    # components are accepted because AssemblyVersion takes four.
    $numbers = @()
    if ($tag -match '^\d+(\.\d+){0,3}$') {
        foreach ($part in ($tag -split '\.')) { $numbers += [int]$part }
    }
    $untagged = $numbers.Count -eq 0
    if ($untagged) { $numbers = @(0, 0, 0) }
    while ($numbers.Count -lt 3) { $numbers += 0 }
    $numeric = $numbers -join '.'

    $informational = $numeric
    if ($prerelease) { $informational += "-$prerelease" }

    $meta = @()
    if ($untagged) { $meta += 'untagged' }
    if ($commits -gt 0) { $meta += "$commits" }
    if ($Sha) { $meta += "g$Sha" }
    if ($dirty) { $meta += 'dirty' }
    if ($meta.Count) { $informational += '+' + ($meta -join '.') }

    [pscustomobject]@{
        Numeric        = $numeric
        Informational = $informational
        Commit         = $Sha
        Dirty          = $dirty
        Untagged       = $untagged
    }
}

function Resolve-BuildVersion {
    [CmdletBinding()]
    param(
        # Repository to read the tag from. A mod passes its own root, not this script's directory.
        [Parameter(Mandatory = $true)][string]$Path,

        # Overrides the tag entirely. What a build from a source archive with no .git needs.
        [string]$Explicit
    )

    if ($Explicit) { return ConvertFrom-Describe $Explicit '' }

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Write-Warning "git was not found, so the version cannot be read from a tag. Pass -Version to stamp one."
        return ConvertFrom-Describe '0.0.0' ''
    }

    # A native command's stderr becomes a terminating error under $ErrorActionPreference='Stop' on
    # PowerShell 7, and "fatal: not a git repository" is exactly what an untracked tree produces. Run
    # the calls with error handling relaxed so a non-zero exit is something to inspect below, not a
    # crash - a mod checked out without its .git is a normal thing, not a broken build.
    $previous = $ErrorActionPreference
    $previousNative = $PSNativeCommandUseErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $PSNativeCommandUseErrorActionPreference = $false
    try {
        $root = & git -C $Path rev-parse --show-toplevel 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $root) {
            Write-Warning "Not a git checkout, so the version cannot be read from a tag. Pass -Version to stamp one."
            return ConvertFrom-Describe '0.0.0' ''
        }

        # --always so an untagged tree still describes rather than failing, and --dirty so a build from a
        # modified tree cannot be mistaken for a release.
        $describe = & git -C $Path describe --tags --dirty --always 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $describe) {
            Write-Warning "git describe failed, so the version could not be read. Pass -Version to stamp one."
            return ConvertFrom-Describe '0.0.0' ''
        }

        $sha = & git -C $Path rev-parse --short HEAD 2>$null
        return ConvertFrom-Describe $describe.Trim() "$($sha.Trim())"
    } finally {
        $ErrorActionPreference = $previous
        $PSNativeCommandUseErrorActionPreference = $previousNative
    }
}

function Write-BuildInfoSource {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$OutFile,
        [Parameter(Mandatory = $true)]$BuildVersion
    )

    $dir = Split-Path -Parent $OutFile
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

    # PluginBuildInfo rather than something plainer: this is compiled against the game's own
    # assemblies, so a generic name like BuildInfo risks colliding with a type in one of them. It is a
    # const so that a mod can pass it straight to [BepInPlugin], whose arguments must be compile-time
    # constants - which is the entire reason the version lives here instead of being a literal.
    # Two constants, and the split is not cosmetic.
    #
    # PluginBuildInfo.Version goes in [BepInPlugin], and BepInEx parses that argument with
    # `new System.Version(...)` inside a try/catch: if it throws, the attribute's Version is left null
    # and Chainloader logs "Skipping type [X] because its version is invalid" and never loads the plugin.
    # System.Version accepts two to four dot-separated integers and nothing else, so "1.2.3+g0a1b2c3" -
    # a perfectly good SemVer string, and what the assembly attribute wants - makes a mod silently not
    # load. Numeric is padded to three components and capped at four precisely so it always parses.
    #
    # PluginBuildInfo.Informational carries the commit and is for the assembly attribute and for display.
    @"
// Generated by build.ps1 from the git tag. Do not edit, and do not commit - this file is obj\.
[assembly: System.Reflection.AssemblyVersion("$($BuildVersion.Numeric)")]
[assembly: System.Reflection.AssemblyFileVersion("$($BuildVersion.Numeric)")]
[assembly: System.Reflection.AssemblyInformationalVersion("$($BuildVersion.Informational)")]

internal static class PluginBuildInfo
{
    /// <summary>
    /// For [BepInPlugin]. Must stay parseable by System.Version: two to four dot-separated integers,
    /// no prefix, no prerelease, no build metadata. BepInEx silently refuses to load a plugin whose
    /// version string it cannot parse.
    /// </summary>
    public const string Version = "$($BuildVersion.Numeric)";

    /// <summary>Tag, prerelease and commit. For the assembly attribute and for display, never for [BepInPlugin].</summary>
    public const string Informational = "$($BuildVersion.Informational)";
}
"@ | Set-Content -LiteralPath $OutFile -Encoding UTF8
}