<#
.SYNOPSIS
    Runs a .NET project inside the dev container defined next to this script.

.DESCRIPTION
    The "Dev Container" launch profile in Properties/launchSettings.json runs this script, so the Run button in
    Visual Studio starts the app inside the container. It:
      1. checks for Node.js and the devcontainer CLI and offers to install them (winget and npm),
      2. finds a running Docker or Podman,
      3. starts the dev container with "devcontainer up" (building it the first time),
      4. stops the app if a previous run is still going in the container,
      5. runs "dotnet run" for the project inside the container, in this window.

    Added by the Dev Containers extension for Visual Studio. Commit it with the project so the profile works
    right after cloning. It works on Windows PowerShell 5.1 and PowerShell 7.

.PARAMETER LaunchProfile
    A launch profile of the project (commandName "Project") for dotnet run to use inside the container, for its
    applicationUrl and environment variables. Leave empty to run without a launch profile.

.PARAMETER Project
    The project folder or .csproj. Defaults to the current folder, which the launch profile sets to the project.

.PARAMETER Config
    The devcontainer.json to use. Defaults to the one next to this script. The workspace folder is always the
    folder that holds this script's .devcontainer folder.

.PARAMETER AppArgs
    Arguments passed to the app.
#>
[CmdletBinding()]
param(
    [string] $LaunchProfile = '',
    [string] $Project = '',
    [string] $Config = '',
    [string[]] $AppArgs = @()
)

# Native tools report failure through exit codes, which are checked below. 'Stop' would turn their stderr output
# into errors on Windows PowerShell 5.1.
$ErrorActionPreference = 'Continue'
$onWindows = $env:OS -eq 'Windows_NT'
$workspace = Split-Path -Parent $PSScriptRoot
$configFile = if ($Config) { $Config } else { Join-Path $PSScriptRoot 'devcontainer.json' }

function Write-Step([string] $text) {
    Write-Host "[dev container] $text" -ForegroundColor Cyan
}

function Stop-WithError([string] $text) {
    Write-Host "[dev container] $text" -ForegroundColor Red
    exit 1
}

function Update-PathFromRegistry {
    # Tools installed by winget or npm are not on this window's PATH until it is reloaded.
    if ($onWindows) {
        $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
            [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + $env:Path
    }
}

function Find-DevContainerCli {
    $command = Get-Command devcontainer -CommandType Application, ExternalScript -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command) { return $null }

    $path = $command.Source
    if ($path -match '\.(cmd|ps1)$') {
        # npm's shims go through cmd.exe or a script. Run the CLI's own entry point with node instead, so the
        # arguments reach it unchanged.
        $script = Join-Path (Split-Path -Parent $path) 'node_modules\@devcontainers\cli\devcontainer.js'
        $node = Get-Command node -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ((Test-Path $script) -and $node) { return @{ File = $node.Source; Prefix = @($script) } }
        return $null
    }

    return @{ File = $path; Prefix = @() }
}

function Install-Prerequisites {
    $nodeMissing = -not (Get-Command node -CommandType Application -ErrorAction SilentlyContinue)
    $commands = @()
    if ($nodeMissing) {
        $commands += 'winget install --id OpenJS.NodeJS.LTS --exact --source winget --accept-source-agreements --accept-package-agreements'
    }
    $commands += 'npm install -g @devcontainers/cli'

    Write-Host ''
    Write-Host 'Running the app in its dev container needs Node.js and the devcontainer CLI, which are not installed.' -ForegroundColor Yellow
    Write-Host 'They can be installed with:'
    $commands | ForEach-Object { Write-Host "    $_" }
    Write-Host ''

    if ($nodeMissing -and -not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Stop-WithError 'winget (App Installer) was not found. Install Node.js LTS from https://nodejs.org, run the npm command above, then start the app again.'
    }

    $answer = Read-Host 'Install them now? [Y/n]'
    if ($answer -and $answer -notmatch '^(y|yes)$') {
        Stop-WithError 'Not installed. Run the commands above, then start the app again.'
    }

    foreach ($command in $commands) {
        Write-Step $command
        Invoke-Expression $command
        if ($LASTEXITCODE -ne 0) { Stop-WithError "'$command' failed with exit code $LASTEXITCODE." }
        Update-PathFromRegistry
    }
}

function Find-ContainerEngine {
    # DEVCONTAINERS_RUNTIME=docker or podman picks one; otherwise Docker is preferred when it is running.
    $names = if ($env:DEVCONTAINERS_RUNTIME) { @($env:DEVCONTAINERS_RUNTIME) } else { @('docker', 'podman') }
    foreach ($name in $names) {
        $command = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $command) { continue }
        $version = & $command.Source version --format '{{.Server.Version}}' 2>$null
        if ($LASTEXITCODE -eq 0 -and $version) {
            Write-Step "Using $name $version"
            return $command.Source
        }
    }

    Stop-WithError ('No running container engine was found (tried ' + ($names -join ', ') + '). Start Docker Desktop or Podman and run again.')
}

function Invoke-DevContainer([string[]] $arguments) {
    & $cli.File @($cli.Prefix + $arguments)
}

if (-not (Test-Path $configFile)) { Stop-WithError "The dev container configuration $configFile was not found." }

$projectPath = if ($Project) { $Project } else { (Get-Location).Path }
$projectPath = (Resolve-Path $projectPath).Path
if (-not $projectPath.StartsWith($workspace, [StringComparison]::OrdinalIgnoreCase)) {
    Stop-WithError "The project ($projectPath) is not inside the dev container's folder ($workspace)."
}

# Path of the project relative to the workspace folder, which is where commands start inside the container.
$relativeProject = $projectPath.Substring($workspace.Length).TrimStart('\', '/').Replace('\', '/')
if (-not $relativeProject) { $relativeProject = '.' }

$cli = Find-DevContainerCli
if (-not $cli) {
    Install-Prerequisites
    $cli = Find-DevContainerCli
    if (-not $cli) { Stop-WithError 'The devcontainer CLI still cannot be found. Open a new window or restart Visual Studio, then run again.' }
}

$engine = Find-ContainerEngine
$common = @('--workspace-folder', $workspace, '--config', $configFile, '--docker-path', $engine)

Write-Step "Starting the dev container for $workspace (the first time this builds it, which takes a while)"
$upOutput = Invoke-DevContainer (@('up') + $common)
if ($LASTEXITCODE -ne 0) {
    Stop-WithError ("devcontainer up failed: " + ($upOutput | Select-Object -Last 1))
}

# A run stopped from Visual Studio ends this window but not the app inside the container. Stop it, so the port is
# free again. Each run records its process group in a pid file. (No "--" before the group: dash, the sh of
# Debian based images, rejects it.)
$pidFile = '/tmp/vs-run-' + (($relativeProject -replace '[^A-Za-z0-9_.-]', '_').Trim('.')) + '.pid'
$stopPrevious = "if [ -f $pidFile ]; then kill -TERM -`$(cat $pidFile) 2>/dev/null && sleep 1; rm -f $pidFile; fi; true"
Invoke-DevContainer (@('exec') + $common + @('sh', '-c', $stopPrevious)) | Out-Null

$run = "dotnet run --project '$relativeProject'"
$run += if ($LaunchProfile) { " --launch-profile '$LaunchProfile'" } else { ' --no-launch-profile' }
if ($AppArgs.Count -gt 0) { $run += ' -- ' + (($AppArgs | ForEach-Object { "'" + ($_ -replace "'", "'\''") + "'" }) -join ' ') }

Write-Step "Running in the container: $run"
Invoke-DevContainer (@('exec') + $common + @('setsid', '-w', 'sh', '-c', "echo `$`$ > $pidFile; exec $run"))
exit $LASTEXITCODE
