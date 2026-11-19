# Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
# SPDX-License-Identifier: GPL-3.0-or-later
<#
  Creates the release signing key, ONCE, for the project owner.

  - The public half is written to update-key\ in two formats (Windows and Mac). Commit those files: both apps are
    built with them and refuse any update that wasn't signed with the private half.
  - The private half then goes straight into the repository's secret RELEASE_SIGNING_KEY (GitHub keeps it
    encrypted; nobody, including you, can read it back) and is deleted from this computer. It is never shown.
  The public files are written first, so a failure part-way never leaves the secret and the files mismatched.

  Needs: GitHub CLI signed in to the owner's account (gh auth status), and OpenSSL (comes with Git for Windows).
  Usage, from the repository folder:   powershell -ExecutionPolicy Bypass -File tools\make-release-key.ps1
#>
# native tools print progress on their error stream; judge them by exit code, not by that text
$ErrorActionPreference = 'Continue'
$repo = 'knox-101/space-hunter'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'update-key'

function Fail($msg) { Write-Host $msg -ForegroundColor Red; exit 1 }

if ((Test-Path (Join-Path $out 'update-key.xml')) -and -not ($args -contains '-Replace')) {
    Fail 'A release key already exists (update-key\). Making a new one stops every installed copy from accepting updates. If you really mean to, run again with -Replace.'
}
$openssl = (Get-Command openssl -ErrorAction SilentlyContinue).Source
if (-not $openssl) { $openssl = 'C:\Program Files\Git\usr\bin\openssl.exe' }
if (-not (Test-Path $openssl)) { Fail 'OpenSSL not found. Install Git for Windows, or put openssl on PATH.' }
cmd /c "gh auth status >nul 2>nul"
if ($LASTEXITCODE -ne 0) { Fail 'GitHub CLI is not signed in. Run: gh auth login' }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('sh-key-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
$private = Join-Path $tmp 'private.pem'
try {
    cmd /c "`"$openssl`" genrsa -out `"$private`" 3072 2>nul"
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $private)) { Fail 'OpenSSL could not create the key.' }

    # 1. public half -> update-key\ (Mac: PKCS#1 DER; Windows: .NET RSA XML)
    New-Item -ItemType Directory $out -Force | Out-Null
    $der = Join-Path $out 'update-key.der'
    cmd /c "`"$openssl`" rsa -in `"$private`" -RSAPublicKey_out -outform DER -out `"$der`" 2>nul"
    if ($LASTEXITCODE -ne 0) { Fail 'Could not write the public key.' }
    $line = cmd /c "`"$openssl`" rsa -in `"$private`" -noout -modulus 2>nul"
    $hex = ($line -split '=')[1].Trim()
    $bytes = New-Object byte[] ($hex.Length / 2)
    for ($i = 0; $i -lt $bytes.Length; $i++) { $bytes[$i] = [Convert]::ToByte($hex.Substring(2 * $i, 2), 16) }
    [IO.File]::WriteAllText((Join-Path $out 'update-key.xml'), '<RSAKeyValue><Modulus>' + [Convert]::ToBase64String($bytes) + '</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>')

    # 2. private half -> repository secret (read from the file; never printed)
    cmd /c "gh secret set RELEASE_SIGNING_KEY --repo $repo < `"$private`""
    if ($LASTEXITCODE -ne 0) { Fail 'Could not store the secret on GitHub. Nothing was published; run this again.' }
}
finally {
    if (Test-Path $private) { Remove-Item $private -Force }
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host ''
Write-Host 'Done. The private key is stored as the RELEASE_SIGNING_KEY secret on GitHub and deleted here.'
Write-Host 'The public key is in update-key\ (update-key.xml and update-key.der). Commit those two files.'
