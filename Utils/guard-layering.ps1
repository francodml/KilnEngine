param()

# Enforces the Kiln layering rule. Kiln is a general-purpose engine, layered:
#
#   Game -> Kiln.Runtime -> Kiln.Render -> Kiln.Core
#
#   Kiln.Core         entities, components, transforms, time, resource identity.
#                     No device, no window, OS-neutral framework.
#   Kiln.Render       render scene, extraction, views, passes, IRenderDevice.
#                     Describes rendering, performs none of it. No graphics API.
#   Kiln.Render.<api> one project per backend; the only place GL/Vulkan/D3D
#                     symbols may appear.
#   Kiln.Runtime      the host: window, device creation, backend selection,
#                     input capture, threads, channels, frame loop.
#
# A game references Kiln. Kiln never references a game. Concretely:
#
#   1. An engine project references only other engine projects (Kiln.*).
#   2. No source file in a Kiln.* project imports a namespace outside
#      System, Microsoft or Kiln. That catches a game leaking in through a
#      linked file or global using, and rogue third-party dependencies.
#   3. Device, window and graphics-API packages appear only where they belong:
#        Kiln.Runtime        the host - window and input capture
#        Kiln.Render.<api>   one project per backend - the graphics API itself
#      Kiln.Core and Kiln.Render (the API-agnostic layer) may not have them.
#   4. Kiln.Core targets no OS-specific framework (no -windows suffix), whether
#      that framework is set in the project file or inherited from the nearest
#      Directory.Build.props above it.
#
# The direction of dependency is the whole point of the split; enforce it in the
# build rather than in review.
#
# ASCII only: this script is invoked through Windows PowerShell 5.1, which reads
# the file as ANSI and mangles non-ASCII characters. An em dash or a section
# sign produces a parse error, not a warning.

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# Packages that bind an assembly to a device, a window, or a Windows-only imaging
# stack. Only the host and the graphics backends may take these.
$devicePackagePattern = '^(Silk\.NET\.(Windowing|Input|OpenGL|Direct3D|GLFW|SDL|Assimp)|Hexa\.NET\.ImGui|Vortice\.|SharpDX|System\.Drawing\.Common|SixLabors\.|OpenTK|Veldrid|SkiaSharp)'

# Root namespaces an engine source file may import. Anything else is either a
# game leaking in or a third-party dependency that has not been argued for.
$allowedUsingRoots = @('System', 'Microsoft', 'Kiln')

# Matches a using directive and captures its root namespace, in either form:
#   [global] using [static] [global::]Root.Name;
#   [global] using Alias = [global::]Root.Name...;
# A using declaration ('using var x = ...;', 'using FileStream f = ...;') and a
# using statement ('using (...)') do not match, because neither has a bare
# qualified name followed by ';' nor an identifier followed by '='.
$usingDirectivePattern = '^\s*(?:global\s+)?using\s+(?:static\s+)?(?:[A-Za-z_]\w*\s*=\s*(?:global::)?([A-Za-z_]\w*)|(?:global::)?([A-Za-z_]\w*)(?:\.[A-Za-z_]\w*)*\s*;)'

# 'using Alias = int;' is legal C# 12; a built-in type keyword is not a namespace.
$builtinTypeKeywords = @('bool', 'byte', 'sbyte', 'char', 'decimal', 'double', 'float',
    'int', 'uint', 'nint', 'nuint', 'long', 'ulong', 'short', 'ushort',
    'object', 'string', 'dynamic', 'void')

# Resolves the target framework(s) a project actually builds with, the way
# MSBuild does: Directory.Build.props is imported above the project body, so a
# framework set in the project file wins, and otherwise the nearest props file
# on the way up supplies it. Reading the csproj alone is not enough - a project
# that declares nothing is not portable, it is inheriting from somewhere.
# Scope is Directory.Build.props only; a framework set in a .targets file, which
# MSBuild imports after the project body, is not resolved here.
# Returns objects carrying the defining file and the value, so a hit can name
# the file the developer has to open.
function Get-EffectiveFrameworkNodes {
    param(
        [System.IO.FileInfo] $Project,
        [xml] $ProjectXml,
        [string] $RepoRoot
    )

    $found = New-Object System.Collections.Generic.List[psobject]

    $inProject = @(@($ProjectXml.SelectNodes("//TargetFramework")) + @($ProjectXml.SelectNodes("//TargetFrameworks")) |
        Where-Object { $null -ne $_ })

    if ($inProject.Count -gt 0) {
        foreach ($node in $inProject) {
            $found.Add([pscustomobject]@{ Source = $Project.FullName; Value = $node.InnerText })
        }
        return $found
    }

    # Walk up for the props file. MSBuild imports only the nearest one, so stop
    # at the first that exists whether or not it defines a framework.
    $directory = $Project.DirectoryName
    while (-not [string]::IsNullOrEmpty($directory)) {
        $props = Join-Path $directory "Directory.Build.props"
        if (Test-Path -LiteralPath $props -PathType Leaf) {
            [xml]$propsXml = Get-Content -LiteralPath $props -Raw
            $nodes = @(@($propsXml.SelectNodes("//TargetFramework")) + @($propsXml.SelectNodes("//TargetFrameworks")) |
                Where-Object { $null -ne $_ })
            foreach ($node in $nodes) {
                $found.Add([pscustomobject]@{ Source = $props; Value = $node.InnerText })
            }
            return $found
        }

        # The repo root bounds the walk: a props file outside the checkout is
        # not something CI can see or this guard can speak for.
        if ($directory.TrimEnd('\') -ieq $RepoRoot.TrimEnd('\')) { break }

        $parent = Split-Path -Parent $directory
        if ($parent -eq $directory) { break }
        $directory = $parent
    }

    return $found
}

Push-Location $repoRoot
try {
    $hits = New-Object System.Collections.Generic.List[string]

    $engineProjects = @(Get-ChildItem -Path $repoRoot -Filter "Kiln.*.csproj" -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' })

    # This repo is the engine. Finding no engine projects means the guard is
    # misplaced or the tree is broken, not that there is nothing to check.
    if ($engineProjects.Count -eq 0) {
        Write-Error ("No Kiln.*.csproj found under '{0}'. The layering guard must run from the engine repo." -f $repoRoot)
    }

    foreach ($project in $engineProjects) {
        $projectDir  = $project.DirectoryName
        $projectName = [System.IO.Path]::GetFileNameWithoutExtension($project.Name)
        $relative    = $project.FullName.Substring($repoRoot.Length).TrimStart('\')
        $isCore      = $projectName -eq "Kiln.Core"

        # The host needs a window; a backend needs its graphics API. Nothing else does.
        $mayUseDevice = ($projectName -eq "Kiln.Runtime") -or ($projectName -like "Kiln.Render.*")

        [xml]$xml = Get-Content -LiteralPath $project.FullName -Raw

        # 1. Project references must stay inside the engine.
        foreach ($reference in $xml.SelectNodes("//ProjectReference")) {
            $include = $reference.GetAttribute("Include")
            if ([string]::IsNullOrWhiteSpace($include)) { continue }
            $referencedName = [System.IO.Path]::GetFileNameWithoutExtension($include)
            if ($referencedName -notlike "Kiln.*") {
                $hits.Add(("{0}: references '{1}'. An engine project may reference only other Kiln.* projects." -f $relative, $referencedName))
            }
        }

        # 3. Device and graphics-API packages stay in the host and the backends.
        if (-not $mayUseDevice) {
            foreach ($package in $xml.SelectNodes("//PackageReference")) {
                $include = $package.GetAttribute("Include")
                if ([string]::IsNullOrWhiteSpace($include)) { continue }
                if ($include -match $devicePackagePattern) {
                    $hits.Add(("{0}: package '{1}'. Only Kiln.Runtime and Kiln.Render.<api> may reference a device, window, or imaging library." -f $relative, $include))
                }
            }
        }

        if ($isCore) {
            # 4. Kiln.Core stays OS-neutral, whether the framework is declared in
            #    the project file or inherited from a Directory.Build.props.
            $frameworks = @(Get-EffectiveFrameworkNodes -Project $project -ProjectXml $xml -RepoRoot $repoRoot)
            foreach ($framework in $frameworks) {
                if ($null -eq $framework) { continue }
                if ($framework.Value -notmatch '-(windows|android|ios|maccatalyst|tvos)') { continue }

                $source = $framework.Source
                if ($source.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $source = $source.Substring($repoRoot.Length).TrimStart('\')
                }

                if ($source -eq $relative) {
                    $hits.Add(("{0}: target framework '{1}' is OS-specific. Kiln.Core must target a portable framework." -f $relative, $framework.Value))
                }
                else {
                    $hits.Add(("{0}: target framework '{1}' inherited from {2} is OS-specific. Kiln.Core must target a portable framework." -f $relative, $framework.Value, $source))
                }
            }
        }

        # 2. Source-level leakage, which a project reference check alone would miss
        #    (linked files, global usings, packages pulled in transitively).
        $sources = @(Get-ChildItem -Path $projectDir -Filter "*.cs" -Recurse -File |
            Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' })

        foreach ($source in $sources) {
            $sourceRelative = $source.FullName.Substring($repoRoot.Length).TrimStart('\')
            $lines = @(Get-Content -LiteralPath $source.FullName)
            for ($i = 0; $i -lt $lines.Length; $i++) {
                $match = [regex]::Match($lines[$i], $usingDirectivePattern)
                if (-not $match.Success) { continue }

                $root = $match.Groups[1].Value
                if ([string]::IsNullOrEmpty($root)) { $root = $match.Groups[2].Value }

                if ($allowedUsingRoots -contains $root) { continue }
                if ($builtinTypeKeywords -contains $root) { continue }

                $hits.Add(("{0}:{1}: imports '{2}'. Engine code may only import System, Microsoft or Kiln namespaces." -f $sourceRelative, ($i + 1), $root))
            }
        }
    }

    if ($hits.Count -gt 0) {
        $hits | Select-Object -First 200 | ForEach-Object { Write-Host $_ }
        if ($hits.Count -gt 200) {
            Write-Host ("... {0} more hit(s)." -f ($hits.Count - 200))
        }
        Write-Error "Engine layering violated."
    }

    Write-Host ("OK: layering guard passed over {0} engine project(s)." -f $engineProjects.Count)
}
finally {
    Pop-Location
}
