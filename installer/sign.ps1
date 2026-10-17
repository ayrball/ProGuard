<#
.SYNOPSIS
    SIGNING HOOK: Authenticode-signs the given files. Does nothing unless PROGUARD_SIGNING is set.
.DESCRIPTION
    build.ps1 calls this twice: for the exes before they go into the MSI, then for the MSI.
    It is OFF by default and the repository holds no credentials. To switch it on, set PROGUARD_SIGNING and the
    variables for one provider (in CI: repository variables and secrets, see .github/workflows/release.yml):

    PROGUARD_SIGNING=pfx              A code-signing certificate in a .pfx file (an OV certificate, say).
        PROGUARD_SIGN_PFX               path to the .pfx, or
        PROGUARD_SIGN_PFX_BASE64        the .pfx as base64 (a CI secret)
        PROGUARD_SIGN_PFX_PASSWORD      its password

    PROGUARD_SIGNING=trusted-signing  Azure Trusted Signing, through signtool and Microsoft's dlib.
        PROGUARD_SIGN_TS_ENDPOINT       e.g. https://eus.codesigning.azure.net
        PROGUARD_SIGN_TS_ACCOUNT        the code signing account name
        PROGUARD_SIGN_TS_PROFILE        the certificate profile name
        PROGUARD_SIGN_TS_DLIB           path to Azure.CodeSigning.Dlib.dll (NuGet: Microsoft.Trusted.Signing.Client,
                                        bin\x64)
        AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET   an app registration with the
                                        "Trusted Signing Certificate Profile Signer" role (read by the dlib)

    PROGUARD_SIGNING=signpath         SignPath, through its SignPath PowerShell module (installed if missing).
        PROGUARD_SIGN_SIGNPATH_TOKEN          API token of a CI user
        PROGUARD_SIGN_SIGNPATH_ORGANIZATION   organization id
        PROGUARD_SIGN_SIGNPATH_PROJECT        project slug
        PROGUARD_SIGN_SIGNPATH_POLICY         signing policy slug
        PROGUARD_SIGN_SIGNPATH_CONFIGURATION  artifact configuration slug (optional; it must accept a single
                                              .exe or .msi file)

    Optional for all: PROGUARD_SIGN_TIMESTAMP_URL (an RFC 3161 server; "none" skips timestamping, for tests only),
    PROGUARD_SIGNTOOL (path to signtool.exe; by default the newest one in the Windows 10/11 SDK).

    The pfx route is exercised in CI with a throw-away self-signed certificate; the other two are untested until an
    account exists.
#>
param([Parameter(Mandatory)][string[]]$Path)

$ErrorActionPreference = "Stop"
$mode = "$env:PROGUARD_SIGNING".Trim().ToLowerInvariant()

if (-not $mode -or $mode -eq "none") {
    Write-Host "Signing is off (PROGUARD_SIGNING is not set): $($Path.Count) file(s) left unsigned." -ForegroundColor DarkGray
    return
}

function Get-Required([string]$name) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if (-not $value) { throw "PROGUARD_SIGNING=$mode needs $name." }
    $value
}

function Get-SignTool {
    if ($env:PROGUARD_SIGNTOOL) { return $env:PROGUARD_SIGNTOOL }
    $found = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
    if (-not $found) { throw "signtool.exe not found: install the Windows SDK or set PROGUARD_SIGNTOOL." }
    $found.FullName
}

# /tr and /td for an RFC 3161 timestamp, so signatures stay valid after the certificate expires.
function Get-TimestampArguments([string]$default) {
    $url = if ($env:PROGUARD_SIGN_TIMESTAMP_URL) { $env:PROGUARD_SIGN_TIMESTAMP_URL } else { $default }
    if ($url -eq "none") { return @() }
    @("/tr", $url, "/td", "SHA256")
}

function Invoke-SignTool([string[]]$arguments) {
    $signtool = Get-SignTool
    foreach ($file in $Path) {
        Write-Host "Signing $file..."
        & $signtool sign @arguments $file
        if ($LASTEXITCODE -ne 0) { throw "signtool failed for $file (exit code $LASTEXITCODE)." }
    }
}

switch ($mode) {
    "pfx" {
        $pfx = $env:PROGUARD_SIGN_PFX
        $temp = $null
        if (-not $pfx) {
            $temp = Join-Path ([IO.Path]::GetTempPath()) "proguard-sign-$([guid]::NewGuid()).pfx"
            [IO.File]::WriteAllBytes($temp, [Convert]::FromBase64String((Get-Required "PROGUARD_SIGN_PFX_BASE64")))
            $pfx = $temp
        }
        try {
            Invoke-SignTool (@("/fd", "SHA256") + @(Get-TimestampArguments "http://timestamp.digicert.com") +
                @("/f", $pfx, "/p", (Get-Required "PROGUARD_SIGN_PFX_PASSWORD")))
        } finally {
            if ($temp) { Remove-Item -Force $temp -ErrorAction SilentlyContinue }
        }
    }
    "trusted-signing" {
        $metadata = Join-Path ([IO.Path]::GetTempPath()) "proguard-trusted-signing-$([guid]::NewGuid()).json"
        @{
            Endpoint               = Get-Required "PROGUARD_SIGN_TS_ENDPOINT"
            CodeSigningAccountName = Get-Required "PROGUARD_SIGN_TS_ACCOUNT"
            CertificateProfileName = Get-Required "PROGUARD_SIGN_TS_PROFILE"
        } | ConvertTo-Json | Set-Content -Encoding UTF8 $metadata
        try {
            Invoke-SignTool (@("/v", "/fd", "SHA256") + @(Get-TimestampArguments "http://timestamp.acs.microsoft.com") +
                @("/dlib", (Get-Required "PROGUARD_SIGN_TS_DLIB"), "/dmdf", $metadata))
        } finally {
            Remove-Item -Force $metadata -ErrorAction SilentlyContinue
        }
    }
    "signpath" {
        if (-not (Get-Module -ListAvailable -Name SignPath)) {
            Install-Module -Name SignPath -Scope CurrentUser -Force
        }
        Import-Module SignPath
        $request = @{
            CIUserToken       = Get-Required "PROGUARD_SIGN_SIGNPATH_TOKEN"
            OrganizationId    = Get-Required "PROGUARD_SIGN_SIGNPATH_ORGANIZATION"
            ProjectSlug       = Get-Required "PROGUARD_SIGN_SIGNPATH_PROJECT"
            SigningPolicySlug = Get-Required "PROGUARD_SIGN_SIGNPATH_POLICY"
        }
        if ($env:PROGUARD_SIGN_SIGNPATH_CONFIGURATION) { $request.ArtifactConfigurationSlug = $env:PROGUARD_SIGN_SIGNPATH_CONFIGURATION }
        foreach ($file in $Path) {
            Write-Host "Submitting $file to SignPath..."
            Submit-SigningRequest @request -InputArtifactPath $file -OutputArtifactPath $file -WaitForCompletion -Force
        }
    }
    default { throw "Unknown PROGUARD_SIGNING '$mode': use pfx, trusted-signing, signpath or none." }
}

# Fail the build rather than ship a file the provider quietly left unsigned.
foreach ($file in $Path) {
    $signature = Get-AuthenticodeSignature $file
    if (-not $signature.SignerCertificate) { throw "$file has no signature after signing ($($signature.Status))." }
    Write-Host "Signed $([IO.Path]::GetFileName($file)): $($signature.SignerCertificate.Subject) ($($signature.Status))"
}
