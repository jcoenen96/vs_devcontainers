<#
.SYNOPSIS
    Runs a .NET project inside the dev container defined next to this script.

.DESCRIPTION
    The "Dev Container" launch profile in Properties/launchSettings.json runs this script, so the Run button in
    Visual Studio starts the app inside the container. It:
      1. checks for Node.js and the devcontainer CLI and offers to install them (winget and npm),
      2. finds a running Docker or Podman,
      3. starts the dev container with "devcontainer up": it reuses the container of this folder, also one that
         VS Code created and may have open, and builds one only the first time,
      4. copies the project's user secrets, your NuGet.Config, the HTTPS development certificate and your Git
         name, email and line ending settings into the container,
      5. stops the app if a previous run is still going in the container,
      6. builds the project and runs it inside the container, in this window, with the launch profile's settings
         (localhost URLs listen on every interface of the container, so forwarded ports reach the app).

    Stopping the app never stops the container, so VS Code and Visual Studio can share it.

    Added by the Dev Containers extension for Visual Studio. Commit it with the project so the profile works
    right after cloning. It works on Windows PowerShell 5.1 and PowerShell 7.

.PARAMETER LaunchProfile
    A launch profile of the project (commandName "Project") to apply inside the container: its applicationUrl,
    environment variables and command line arguments. Leave empty to run without a launch profile.

.PARAMETER Project
    The project folder or project file (.csproj, .fsproj or .vbproj). Defaults to the current folder, which the launch profile sets to the project.

.PARAMETER Config
    The devcontainer.json to use. Defaults to the one next to this script. The workspace folder is always the
    folder that holds this script's .devcontainer folder.

.PARAMETER AppArgs
    Arguments passed to the app.

.PARAMETER Npm
    For a Node.js or TypeScript app: the script of package.json to run in the container, such as dev or start.
    -Project is then the folder with package.json. The packages are installed in the container first when
    node_modules is missing or older than the lock file.

.PARAMETER InspectPort
    With -Npm: start Node.js with --inspect on this port of the container, so a debugger can attach to it
    through the forwarded port.
#>
[CmdletBinding()]
param(
    [string] $LaunchProfile = '',
    [string] $Project = '',
    [string] $Config = '',
    [string[]] $AppArgs = @(),
    # A Node.js or TypeScript app: the package.json script to run (npm run <Npm>) instead of dotnet run.
    [string] $Npm = '',
    # With -Npm: start Node.js with its inspector on this port (0 = off), for attaching a debugger.
    [int] $InspectPort = 0,
    # Used by the extension: copy this computer's settings into a running container, then exit.
    [switch] $SyncOnly,
    [string] $EnginePath = '',
    [string] $ContainerId = '',
    [string] $ContainerUser = 'root',
    [string] $WorkspaceFolder = ''
)

# Native tools report failure through exit codes, which are checked below. 'Stop' would turn their stderr output
# into errors on Windows PowerShell 5.1.
$ErrorActionPreference = 'Continue'
if ($PSVersionTable.PSEdition -eq 'Desktop' -and $env:PSModulePath) {
    # Started from PowerShell 7 (Visual Studio opened from a pwsh prompt, for example), Windows PowerShell inherits
    # PowerShell 7's module folders, and their modules fail to load here. Keep the Windows PowerShell ones.
    $env:PSModulePath = (($env:PSModulePath -split ';') | Where-Object { $_ -and $_ -notmatch '\\PowerShell\\(7\\)?Modules' }) -join ';'
}
# DEVCONTAINERS_RUN_LOG=<file> keeps a transcript of the run, for diagnosing a window that closed too fast.
if ($env:DEVCONTAINERS_RUN_LOG) { Start-Transcript -Path $env:DEVCONTAINERS_RUN_LOG -Append | Out-Null }
$onWindows = $env:OS -eq 'Windows_NT'
$workspace = Split-Path -Parent $PSScriptRoot
# The extension runs a copy of this script from a temporary folder and says which folder the container mounts.
if ($WorkspaceFolder) { $workspace = $WorkspaceFolder.TrimEnd('\', '/') }
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

function Test-ContainerEngine([string] $path) {
    # The engine's version if it answers and runs Linux containers, else $null. An engine in Windows containers
    # mode cannot run dev containers.
    $version, $os = "$(& $path version --format '{{.Server.Version}} {{.Server.Os}}' 2>$null)" -split ' ', 2
    if ($LASTEXITCODE -eq 0 -and $version -and $os -ne 'windows') { return $version }
    return $null
}

function Get-PodmanMachineState([string] $podman) {
    $state = "$(& $podman machine inspect --format '{{.State}}' 2>&1)".Trim().ToLowerInvariant()
    if ($LASTEXITCODE -ne 0) { if ($state -match 'does not exist|no such|not found') { return 'none' } else { return 'unknown' } }
    return $state
}

function Wait-ContainerEngine([string] $path, [string] $what) {
    Write-Step "Waiting for $what to start (up to 3 minutes)"
    $deadline = (Get-Date).AddMinutes(3)
    while ((Get-Date) -lt $deadline) {
        $version = Test-ContainerEngine $path
        if ($version) { return $version }
        Start-Sleep -Seconds 3
    }
    return $null
}

function Find-ContainerEngine {
    # DEVCONTAINERS_RUNTIME=docker or podman picks one; otherwise Docker is preferred when it is running.
    $names = if ($env:DEVCONTAINERS_RUNTIME) { @($env:DEVCONTAINERS_RUNTIME) } else { @('docker', 'podman') }
    $installed = @()
    foreach ($name in $names) {
        $command = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $command) { continue }
        $installed += , @($name, $command.Source)
        $version = Test-ContainerEngine $command.Source
        if ($version) {
            Write-Step "Using $name $version"
            return $command.Source
        }
    }

    if ($installed.Count -eq 0) {
        Stop-WithError ('Neither Docker nor Podman was found (tried ' + ($names -join ', ') + '). Install Docker Desktop or Podman Desktop, start it, and run again.')
    }

    # Installed but not answering: wait when it is starting, or offer to start it.
    $dockerDesktop = if ($onWindows) { Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe' } else { $null }
    foreach ($engine in $installed) {
        $name, $path = $engine
        if ($name -eq 'docker' -and $dockerDesktop -and (Test-Path $dockerDesktop)) {
            if (-not (Get-Process 'Docker Desktop' -ErrorAction SilentlyContinue)) {
                $answer = Read-Host 'Docker Desktop is installed but not running. Start it now? [Y/n]'
                if ($answer -and $answer -notmatch '^(y|yes)$') { continue }
                Start-Process $dockerDesktop
            }
            $version = Wait-ContainerEngine $path 'Docker Desktop'
            if ($version) { Write-Step "Using docker $version"; return $path }
        }
        if ($name -eq 'podman') {
            $state = Get-PodmanMachineState $path
            if ($state -eq 'starting') {
                $version = Wait-ContainerEngine $path 'the Podman machine'
                if ($version) { Write-Step "Using podman $version"; return $path }
            } elseif ($state -in 'stopped', 'configured', 'none') {
                $prompt = if ($state -eq 'none') { 'Podman has no machine yet. Create and start one now (podman machine init, then start)? [Y/n]' } else { 'The Podman machine is not running. Start it now? [Y/n]' }
                $answer = Read-Host $prompt
                if ($answer -and $answer -notmatch '^(y|yes)$') { continue }
                if ($state -eq 'none') { & $path machine init; if ($LASTEXITCODE -ne 0) { Stop-WithError 'podman machine init failed (see above).' } }
                & $path machine start
                $version = Wait-ContainerEngine $path 'the Podman machine'
                if ($version) { Write-Step "Using podman $version"; return $path }
            }
        }
    }

    Stop-WithError ('No running container engine was found (tried ' + ($names -join ', ') + '). Start Docker Desktop or the Podman machine and run again. Docker Desktop must use Linux containers.')
}

function Get-ProblemAdvice([string] $text) {
    # The next step for common failures of devcontainer up. The extension's ProblemAdvisor knows the same ones.
    switch -Regex ($text) {
        'no space left on device|not enough space on the disk' { return "The container engine ran out of disk space. Free space with 'docker system prune' (or 'podman system prune')." }
        'port is already allocated|address already in use' { return 'A port the container publishes (appPort or a Compose ports entry) is already in use. Stop the program that uses it, or change the port.' }
        'pull access denied|unauthorized|authentication required' { return "The registry refused access to the image. Check its name, and sign in with 'docker login' (or 'podman login')." }
        'tls handshake timeout|dial tcp|i/o timeout|no such host|temporary failure in name resolution|ENOTFOUND|ETIMEDOUT' { return 'The engine could not reach the network. Check the connection, a proxy or VPN, then run again.' }
        'failed to (solve|build)|error building image|executor failed running' { return 'Building the image failed; the failing step is above. Fix the Dockerfile or feature and run again.' }
        'postCreateCommand|postStartCommand|onCreateCommand|updateContentCommand' { return 'A lifecycle command of devcontainer.json failed; its output is above.' }
        'NETSDK1045|does not support targeting \.NET|A compatible \.NET SDK was not found' { return "The container's .NET SDK does not match the project's target framework or global.json. Use an image with that .NET version, or set the dotnet feature's version in devcontainer.json, and rebuild the container." }
    }
    return $null
}

function ConvertTo-LabelPath([string] $path) {
    # Label paths compare as paths: on Windows without case and with either separator, never with a trailing one.
    if ($onWindows) { $path = $path.Replace('/', '\') }
    $separator = if ($onWindows) { '\' } else { '/' }
    $trimmed = $path.TrimEnd($separator)
    if ($trimmed.Length -eq 0 -or ($onWindows -and $trimmed -match '^[A-Za-z]:$')) { return $trimmed + $separator }
    return $trimmed
}

function Test-SamePath([string] $a, [string] $b) {
    if (-not $a -or -not $b) { return $false }
    $a = ConvertTo-LabelPath $a
    $b = ConvertTo-LabelPath $b
    if ($onWindows) { return $a -ieq $b }
    return $a -ceq $b
}

function Get-DevContainerIdLabels {
    # The devcontainer CLI finds "the" container of a folder by its labels. VS Code writes C:\ as c:\, so the exact
    # labels of an existing container (VS Code's included) are looked up and passed with --id-label; the CLI then
    # reuses that container instead of creating a second one. A new container gets labels the way VS Code writes
    # them, so VS Code reuses it too.
    $folderLabel = 'devcontainer.local_folder'
    $configLabel = 'devcontainer.config_file'
    $ids = @(& $engine ps -a --filter "label=$folderLabel" --format '{{.ID}}' 2>$null | Where-Object { $_ })
    if ($LASTEXITCODE -eq 0 -and $ids.Count -gt 0) {
        $found = $null
        foreach ($line in @(& $engine inspect --format '{{.State.Running}} {{json .Config.Labels}}' $ids 2>$null)) {
            $running, $json = "$line" -split ' ', 2
            if (-not $json) { continue }
            $labels = $json | ConvertFrom-Json
            $folder = $labels.$folderLabel
            $config = $labels.$configLabel
            if (-not $config -or -not (Test-SamePath $folder $workspace) -or -not (Test-SamePath $config $configFile)) { continue }
            if (-not $found -or $running -eq 'true') { $found = @($folder, $config) }
            if ($running -eq 'true') { break }
        }

        if ($found) {
            Write-Step "Reusing the existing dev container of this folder"
            return @('--id-label', "$folderLabel=$($found[0])", '--id-label', "$configLabel=$($found[1])")
        }
    }

    $folder = ConvertTo-LabelPath $workspace
    $config = ConvertTo-LabelPath $configFile
    if ($onWindows) {
        $folder = $folder.Substring(0, 1).ToLowerInvariant() + $folder.Substring(1)
        $config = $config.Substring(0, 1).ToLowerInvariant() + $config.Substring(1)
    }

    return @('--id-label', "$folderLabel=$folder", '--id-label', "$configLabel=$config")
}

function Invoke-DevContainer([string[]] $arguments) {
    & $cli.File @($cli.Prefix + $arguments)
}

function Get-UserSecretsId {
    # The project file (or the one project file in the project folder) holds the id 'dotnet user-secrets init' added.
    $file = if (Test-Path $projectPath -PathType Leaf) { Get-Item $projectPath } else { Get-ChildItem $projectPath -File | Where-Object { $_.Extension -in '.csproj', '.fsproj', '.vbproj' } | Select-Object -First 1 }
    if (-not $file) { return $null }
    $match = Select-String -Path $file.FullName -Pattern '<UserSecretsId>\s*([^<\s]+)\s*</UserSecretsId>' | Select-Object -First 1
    if ($match) { return $match.Matches[0].Groups[1].Value }
    return $null
}

function Get-HostNuGetConfig {
    # The user's NuGet.Config, with what only works on this computer removed: local and network feeds, Windows
    # paths, and passwords that Windows encrypted (DPAPI) and Linux cannot read.
    $path = if ($onWindows) { Join-Path $env:APPDATA 'NuGet\NuGet.Config' } else { Join-Path $HOME '.nuget/NuGet/NuGet.Config' }
    if (-not (Test-Path $path)) { return $null }
    try { [xml] $xml = Get-Content -Raw -Path $path } catch { Write-Step "Not copying $path to the container: it is not valid XML."; return $null }
    $root = $xml.configuration
    if (-not $root) { return $null }

    $removed = @()
    if ($root.packageSources) {
        foreach ($source in @($root.packageSources.SelectNodes('add'))) {
            if ($source.value -notmatch '^https?://') {
                $removed += $source.key
                [void] $root.packageSources.RemoveChild($source)
            }
        }
    }

    foreach ($section in @('disabledPackageSources', 'packageSourceMapping', 'packageSourceCredentials')) {
        $node = $root.SelectSingleNode($section)
        if (-not $node) { continue }
        foreach ($child in @($node.ChildNodes)) {
            $key = if ($child.key) { $child.key } else { [System.Xml.XmlConvert]::DecodeName($child.LocalName) }
            if ($removed -contains $key) { [void] $node.RemoveChild($child) }
        }
    }

    if ($root.packageSourceCredentials) {
        foreach ($source in @($root.packageSourceCredentials.ChildNodes)) {
            foreach ($entry in @($source.SelectNodes('add[@key="Password"]'))) {
                Write-Step ("The NuGet password of '" + [System.Xml.XmlConvert]::DecodeName($source.LocalName) + "' is encrypted for Windows and is not copied. Use a ClearTextPassword or a credential provider for feeds the container restores from.")
                [void] $source.RemoveChild($entry)
            }
        }
    }

    foreach ($section in @('config', 'packageRestore', 'bindingRedirects')) {
        $node = $root.SelectSingleNode($section)
        if (-not $node) { continue }
        foreach ($entry in @($node.SelectNodes('add'))) {
            if ($entry.value -match '^([A-Za-z]:[\\/]|\\\\)') { [void] $node.RemoveChild($entry) }
        }
    }

    return [System.Text.Encoding]::UTF8.GetBytes($xml.OuterXml)
}

function Get-HostNpmConfig {
    # The user's .npmrc: registries and their tokens. Settings with Windows paths (prefix, cache, a script shell)
    # mean nothing in the container and are left out.
    $file = Join-Path $HOME '.npmrc'
    if (-not (Test-Path $file)) { return $null }
    $lines = Get-Content $file | Where-Object { $_ -notmatch '^\s*(prefix|cache|script-shell|userconfig|globalconfig|tmp)\s*=' -and $_ -notmatch '=\s*[A-Za-z]:\\' }
    if (-not $lines) { return $null }
    return [System.Text.Encoding]::UTF8.GetBytes(($lines -join "`n") + "`n")
}

function Get-HostDevCertificate {
    # The ASP.NET Core HTTPS development certificate Windows trusts ('dotnet dev-certs https --trust'), identified
    # by its marker extension. Only Windows keeps it in a certificate store PowerShell can read.
    # Read through .NET, not the Cert: drive: that needs a PowerShell module, which fails to load in Windows
    # PowerShell when it inherited PowerShell 7's module path.
    if (-not $onWindows) { return $null }
    $now = Get-Date
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('My', 'CurrentUser')
    try { $store.Open('ReadOnly') } catch { return $null }
    return $store.Certificates |
        Where-Object { $_.HasPrivateKey -and $_.NotAfter -gt $now -and ($_.Extensions | Where-Object { $_.Oid.Value -eq '1.3.6.1.4.1.311.84.1.1' }) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

function ConvertTo-ShellLiteral([string] $value) {
    # A single-quoted sh word: safe for any value.
    return "'" + $value.Replace("'", "'\''") + "'"
}

function Get-LaunchProfile([string] $name) {
    # The project's launch profile (commandName "Project") to run with, as dotnet run would apply it.
    if (-not $name) { return $null }
    $folder = if (Test-Path $projectPath -PathType Leaf) { Split-Path -Parent $projectPath } else { $projectPath }
    $file = Join-Path (Join-Path $folder 'Properties') 'launchSettings.json'
    if (-not (Test-Path $file)) { Stop-WithError "The launch profile '$name' was not found: $file does not exist." }
    try { $settings = Get-Content -Raw $file | ConvertFrom-Json } catch { Stop-WithError "$file is not valid JSON: $($_.Exception.Message)" }
    $found = $settings.profiles.PSObject.Properties | Where-Object { $_.Name -eq $name } | Select-Object -First 1
    if (-not $found) { Stop-WithError "The launch profile '$name' was not found in $file." }
    return $found.Value
}

function ConvertTo-ContainerUrls([string] $urls) {
    # localhost in the container is the container's own loopback: the extension's port forwarding reaches it, but
    # appPort, other Compose services and the container's address do not. Listen on every interface instead.
    return (($urls -split ';' | Where-Object { $_.Trim() } | ForEach-Object {
        $_.Trim() -replace '^([A-Za-z]+://)(localhost|127\.0\.0\.1|\[::1\])(?=[:/]|$)', '${1}0.0.0.0'
    }) -join ';')
}

function Get-HostGitSettings {
    # The Git settings that make Git in the container behave like Git on Windows: who commits, the line ending
    # conversion this repository's working tree was checked out with, and a few habits (aliases, default branch).
    # Credential helpers, editors and paths are Windows specific and stay out.
    $git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $git) { return @() }
    $settings = @()
    $lines = @(& $git.Source config --global --get-regexp '^(user\.(name|email)|init\.defaultbranch|pull\.rebase|push\.autosetupremote|alias\..*)$' 2>$null)
    foreach ($line in $lines) {
        $key, $value = "$line" -split ' ', 2
        if ($key) { $settings += , @($key, "$value") }
    }

    # Effective for this repository: with core.autocrlf=true on Windows the files have CRLF line endings, and Git
    # in the container needs the same setting, or it reports every file as changed.
    $autocrlf = "$(& $git.Source -C $workspace config --get core.autocrlf 2>$null)".Trim()
    if ($autocrlf) { $settings += , @('core.autocrlf', $autocrlf) }
    return $settings
}

function Sync-HostState([string] $containerId, [string] $user, [switch] $NoProject) {
    # Copies what the app needs from this computer into the container user's home, so it runs there as it does on
    # Windows: user secrets, NuGet feeds and their credentials, the HTTPS development certificate and Git settings.
    # Files go through stdin as base64, one "path data" line each; then a generated setup script runs once. Both
    # avoid double quotes in arguments, which Windows PowerShell 5.1 passes on unescaped.
    $files = @()
    $setup = @('cd $HOME')
    $secretsId = if ($NoProject) { $null } else { Get-UserSecretsId }
    if ($secretsId) {
        $secrets = if ($onWindows) { Join-Path $env:APPDATA "Microsoft\UserSecrets\$secretsId\secrets.json" } else { Join-Path $HOME ".microsoft/usersecrets/$secretsId/secrets.json" }
        if (Test-Path $secrets) {
            $files += , @(".microsoft/usersecrets/$secretsId/secrets.json", [System.IO.File]::ReadAllBytes($secrets))
            Write-Step 'Copying the user secrets of this project into the container'
        }
    }

    $nuget = Get-HostNuGetConfig
    if ($nuget) {
        $files += , @('.nuget/NuGet/NuGet.Config', $nuget)
        Write-Step 'Copying your NuGet.Config (feeds and credentials) into the container'
    }

    $npmrc = Get-HostNpmConfig
    if ($npmrc) {
        $files += , @('.npmrc', $npmrc)
        Write-Step 'Copying your .npmrc (registries and tokens) into the container'
    }

    $certificate = Get-HostDevCertificate
    if ($certificate) {
        # Imported only when the container does not have this certificate yet. --clean replaces any other
        # development certificate, so Kestrel picks the one Windows trusts.
        $password = [guid]::NewGuid().ToString('N')
        $files += , @('.vs-devcontainer/devcert.pfx', $certificate.Export('Pfx', $password))
        $setup += 'if [ ! -f .dotnet/corefx/cryptography/x509stores/my/' + $certificate.Thumbprint + '.pfx ] && command -v dotnet >/dev/null 2>&1; then'
        $setup += '  echo ''[dev container] Importing the HTTPS development certificate Windows trusts'''
        $setup += '  dotnet dev-certs https --clean --import .vs-devcontainer/devcert.pfx -p ' + $password + ' >/dev/null || echo ''[dev container] Importing the HTTPS development certificate failed.'''
        $setup += 'fi'
        $setup += 'rm -f .vs-devcontainer/devcert.pfx'
    }

    $gitSettings = @(Get-HostGitSettings)
    $setup += 'if command -v git >/dev/null 2>&1; then'
    foreach ($setting in $gitSettings) {
        $setup += '  git config --global ' + (ConvertTo-ShellLiteral $setting[0]) + ' ' + (ConvertTo-ShellLiteral $setting[1])
    }
    # The workspace is mounted from Windows and belongs to another user as far as Linux knows; without this Git
    # refuses to work in it ("detected dubious ownership").
    $setup += '  git config --global --get-all safe.directory | grep -qx ''\*'' || git config --global --add safe.directory ''*'''
    $setup += 'fi'
    if ($gitSettings.Count -gt 0) { Write-Step 'Copying your Git name, email and line ending settings into the container' }

    # One script per run: projects started together (multiple startup projects) each copy at the same time.
    $setupFile = ".vs-devcontainer/setup-$PID.sh"
    $files += , @($setupFile, [System.Text.Encoding]::UTF8.GetBytes(($setup -join "`n") + "`n"))
    $lines = $files | ForEach-Object { $_[0] + ' ' + [Convert]::ToBase64String($_[1]) }
    $write = 'tr -d ''\r'' | while read p d; do mkdir -p $(dirname $HOME/$p) && echo $d | base64 -d > $HOME/$p && chmod 600 $HOME/$p || exit 1; done'
    $lines | & $engine exec -i -u $user $containerId sh -c $write
    if ($LASTEXITCODE -ne 0) { Write-Step 'Copying into the container failed; the app may miss secrets, feeds or the certificate.'; return }

    & $engine exec -u $user $containerId sh -c "sh `$HOME/$setupFile; s=`$?; rm -f `$HOME/$setupFile; exit `$s"
    if ($LASTEXITCODE -ne 0) { Write-Step 'Setting up the container for this computer failed (see above).' }
}

if ($SyncOnly) {
    # Called by the extension after Open in Container (without a project) and before debugging (with one).
    $engine = $EnginePath
    if ($WorkspaceFolder) { $workspace = $WorkspaceFolder }
    if ($Project) {
        # Before debugging: also the project's user secrets.
        $projectPath = (Resolve-Path $Project).Path
        Sync-HostState $ContainerId $ContainerUser
    } else {
        Sync-HostState $ContainerId $ContainerUser -NoProject
    }
    exit 0
}

if (-not (Test-Path $configFile)) { Stop-WithError "The dev container configuration $configFile was not found." }
$configFile = (Resolve-Path $configFile).Path

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
$common = @('--workspace-folder', $workspace, '--config', $configFile, '--docker-path', $engine) + (Get-DevContainerIdLabels)

# Projects started together (multiple startup projects) would each create the container the first time. One at a
# time: the others then find it running.
$sha = [System.Security.Cryptography.SHA256]::Create()
$lockName = 'vs-devcontainer-' + [BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($workspace.ToLowerInvariant()))).Replace('-', '').Substring(0, 16)
$startLock = New-Object System.Threading.Mutex($false, $lockName)
try { [void] $startLock.WaitOne() } catch [System.Threading.AbandonedMutexException] { }

Write-Step "Starting the dev container for $workspace (the first time this builds it, which takes a while)"
$upOutput = Invoke-DevContainer (@('up') + $common)
if ($LASTEXITCODE -ne 0) {
    $advice = Get-ProblemAdvice ($upOutput -join "`n")
    Stop-WithError ("devcontainer up failed: " + ($upOutput | Select-Object -Last 1) + $(if ($advice) { "`n$advice" } else { '' }))
}

$up = $upOutput | Where-Object { "$_" -match '^\{.*"outcome"' } | Select-Object -Last 1 | ConvertFrom-Json
if ($up.containerId) {
    $user = if ($up.remoteUser) { $up.remoteUser } else { 'root' }
    Sync-HostState $up.containerId $user
}
$startLock.ReleaseMutex()

# A run stopped from Visual Studio ends this window but not the app inside the container. Stop it, so the port is
# free again. Each run records its process group in a pid file. (No "--" before the group: dash, the sh of
# Debian based images, rejects it.)
$pidFile = '/tmp/vs-run-' + (($relativeProject -replace '[^A-Za-z0-9_.-]', '_').Trim('.')) + '.pid'
$stopPrevious = "if [ -f $pidFile ]; then kill -TERM -`$(cat $pidFile) 2>/dev/null && sleep 1; rm -f $pidFile; fi; true"
Invoke-DevContainer (@('exec') + $common + @('sh', '-c', $stopPrevious)) | Out-Null

if ($Npm) {
    # A Node.js or TypeScript app. node_modules is installed by the container's npm, for Linux: packages with native
    # code (esbuild, sharp, ...) installed on Windows do not run there. When devcontainer.json mounts a volume on
    # node_modules (recommended, see docs/typescript.md), the engine creates it as root; hand it to the user first.
    $folder = "'" + $relativeProject + "'"
    $user = if ($up.remoteUser) { $up.remoteUser } else { 'root' }
    $remoteFolder = ConvertTo-ShellLiteral (($up.remoteWorkspaceFolder.TrimEnd('/') + '/' + $relativeProject) -replace '/\.$', '')
    & $engine exec -u root $up.containerId sh -c "cd $remoteFolder 2>/dev/null && [ -d node_modules ] && find node_modules -maxdepth 0 -user root ! -user $user -exec chown $user {} +; true" | Out-Null

    $install = 'command -v npm >/dev/null 2>&1 || { echo [dev container] Node.js is not installed in the container. Add the feature ghcr.io/devcontainers/features/node:1 to devcontainer.json, or use a Node.js image, and rebuild the container. >&2; exit 127; }; ' +
        'if [ -f pnpm-lock.yaml ]; then i=''corepack pnpm install --frozen-lockfile''; elif [ -f yarn.lock ]; then i=''corepack yarn install --immutable''; elif [ -f package-lock.json ]; then i=''npm ci''; else i=''npm install''; fi; ' +
        'm=node_modules/.package-lock.json; [ -f node_modules/.modules.yaml ] && m=node_modules/.modules.yaml; [ -f node_modules/.yarn-state.yml ] && m=node_modules/.yarn-state.yml; ' +
        'if [ ! -f $m ] || [ package.json -nt $m ] || { [ -f package-lock.json ] && [ package-lock.json -nt $m ]; }; then echo [dev container] Installing packages in the container: $i; $i || exit $?; fi'

    if ($InspectPort -gt 0) {
        # Without npm in between: npm is a Node.js program too and would take the inspector port itself.
        $scripts = (Get-Content -Raw (Join-Path $projectPath 'package.json') | ConvertFrom-Json).scripts
        $command = $scripts.PSObject.Properties | Where-Object { $_.Name -eq $Npm } | Select-Object -First 1
        if (-not $command) { Stop-WithError "package.json has no script named '$Npm'." }
        # The pre script (a build step such as 'tsc', as npm would run it) runs first, without the inspector: the
        # debugger would otherwise attach to the compiler.
        $run = 'export PATH=$PWD/node_modules/.bin:$PATH; '
        $pre = $scripts.PSObject.Properties | Where-Object { $_.Name -eq ('pre' + $Npm) } | Select-Object -First 1
        if ($pre) { $run += 'sh -c ' + (ConvertTo-ShellLiteral $pre.Value) + ' || exit $?; ' }
        $run += 'export NODE_OPTIONS=--inspect=0.0.0.0:' + $InspectPort + '; exec sh -c ' + (ConvertTo-ShellLiteral $command.Value)
        Write-Step "Running in the container: npm script '$Npm' of $relativeProject, with the Node.js inspector on port $InspectPort"
    } else {
        $run = 'exec npm run ' + (ConvertTo-ShellLiteral $Npm)
        if ($AppArgs.Count -gt 0) { $run += ' -- ' + (($AppArgs | ForEach-Object { ConvertTo-ShellLiteral $_ }) -join ' ') }
        Write-Step "Running in the container: npm run $Npm in $relativeProject"
    }

    # HOST: dev servers that read it (Create React App, Nuxt and others) listen on every interface of the container.
    Invoke-DevContainer (@('exec') + $common + @('setsid', '-w', 'sh', '-c', "echo `$`$ > $pidFile; cd $folder || exit 1; export HOST=0.0.0.0; $install; $run"))
    exit $LASTEXITCODE
}

# The container's .NET SDK has to build the project's target framework. Say what to change before the build fails
# with NETSDK1045, which does not say what to do in a dev container.
$projectFile = Get-ChildItem -LiteralPath $projectPath -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in '.csproj', '.fsproj', '.vbproj' } | Select-Object -First 1
if ($projectFile) {
    $majors = @([regex]::Matches((Get-Content -Raw $projectFile.FullName), '<TargetFrameworks?>([^<]*)</TargetFrameworks?>') |
        ForEach-Object { $_.Groups[1].Value -split ';' } |
        ForEach-Object { if ($_ -match '^\s*net(\d+)\.\d') { [int] $Matches[1] } } |
        Where-Object { $_ -ge 5 })
    $sdks = @(Invoke-DevContainer (@('exec') + $common + @('sh', '-c', 'command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks; true')) 2>$null |
        ForEach-Object { if ("$_" -match '^(\d+)\.(\d+)\.(\d+)') { [int] $Matches[1] } })
    if ($sdks.Count -eq 0) {
        Stop-WithError 'The dev container has no .NET SDK. Use a .NET image (mcr.microsoft.com/devcontainers/dotnet) or add the feature ghcr.io/devcontainers/features/dotnet:2 to devcontainer.json, then rebuild the container.'
    }
    $newest = ($sdks | Measure-Object -Maximum).Maximum
    $tooNew = @($majors | Where-Object { $_ -gt $newest })
    if ($tooNew.Count -gt 0) {
        Write-Host ("[dev container] The project targets net$($tooNew[0]).0, but the newest .NET SDK in the dev container is version $newest. " +
            "Use a .NET $($tooNew[0]) image, or set the dotnet feature's version in devcontainer.json, and rebuild the container.") -ForegroundColor Yellow
    }
}

# Build into the container's own file system, not obj/ and bin/ in the mounted folder: Windows bind mounts (Podman and
# Docker through WSL) do not let the container user set file times there, which fails the build (MSB3374), and
# Visual Studio's own builds on the host use those folders too. (No double quotes: Windows PowerShell passes them on
# unescaped.)
$artifacts = '--property:UseArtifactsOutput=true --property:ArtifactsPath=$HOME/.vs-devcontainer/build'
# Projects started together (multiple startup projects) share the build output, so they build one at a time.
$build = "exec 9>/tmp/vs-run-build.lock; flock 9 2>/dev/null; dotnet build '$relativeProject' $artifacts || exit `$?; flock -u 9 2>/dev/null; exec 9>&-"

# The launch profile applies as dotnet run would apply it (environment variables, applicationUrl as
# ASPNETCORE_URLS, command line arguments), except that localhost URLs listen on every interface of the container.
$environment = @()
$arguments = @()
$launchSettings = Get-LaunchProfile $LaunchProfile
if ($launchSettings) {
    if ($launchSettings.environmentVariables) {
        foreach ($variable in $launchSettings.environmentVariables.PSObject.Properties) { $environment += "$($variable.Name)=$($variable.Value)" }
    }
    if ($launchSettings.applicationUrl) { $environment += 'ASPNETCORE_URLS=' + (ConvertTo-ContainerUrls $launchSettings.applicationUrl) }
    if ($launchSettings.commandLineArgs) { $arguments += , $launchSettings.commandLineArgs }
}
$arguments += @($AppArgs | ForEach-Object { ConvertTo-ShellLiteral $_ })

$run = "dotnet run --no-build --project '$relativeProject' $artifacts --no-launch-profile"
if ($environment.Count -gt 0) { $run = 'env ' + (($environment | ForEach-Object { ConvertTo-ShellLiteral $_ }) -join ' ') + ' ' + $run }
if ($arguments.Count -gt 0) { $run += ' -- ' + ($arguments -join ' ') }

Write-Step ("Running in the container: " + $(if ($LaunchProfile) { "$relativeProject with the '$LaunchProfile' profile" } else { $relativeProject }))
Invoke-DevContainer (@('exec') + $common + @('setsid', '-w', 'sh', '-c', "echo `$`$ > $pidFile; $build; exec $run"))
exit $LASTEXITCODE
