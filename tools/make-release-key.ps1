# Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
# SPDX-License-Identifier: GPL-3.0-or-later
<#
  Creates the release signing key, ONCE, for the project owner.

  - The private half goes straight into the repository's secret RELEASE_SIGNING_KEY (GitHub keeps it encrypted;
    nobody, including you, can read it back) and is then deleted from this computer. It is never shown.
  - The public half is written to update-key\ in two formats (Windows and Mac). Commit those files: both apps are
    built with them and refuse any update that wasn't signed with the private half.

  Needs: GitHub CLI signed in to the owner's account (gh auth status), and OpenSSL (comes with Git for Windows).
  Usage, from the repository folder:   powershell -ExecutionPolicy Bypass -File tools\make-release-key.ps1
#>
$ErrorActionPreference = 'Stop'
$repo = 'knox-101/space-hunter'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'update-key'

if ((Test-Path (Join-Path $out 'update-key.xml')) -and -not ($args -contains '-Replace')) {
    Write-Host 'A release key already exists (update-key\). Making a new one would stop every installed copy from'
    Write-Host 'accepting updates. If you really mean to, run again with -Replace.'
    exit 1
}
$openssl = (Get-Command openssl -ErrorAction SilentlyContinue).Source
if (-not $openssl) { $openssl = 'C:\Program Files\Git\usr\bin\openssl.exe' }
if (-not (Test-Path $openssl)) { throw 'OpenSSL not found. Install Git for Windows, or put openssl on PATH.' }
gh auth status 1>$null 2>$null
if ($LASTEXITCODE -ne 0) { throw 'GitHub CLI is not signed in. Run: gh auth login' }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('sh-key-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
$private = Join-Path $tmp 'private.pem'
try {
    & $openssl genrsa -out $private 3072 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'OpenSSL could not create the key.' }

    # private half -> repository secret (read from the file; never printed)
    Get-Content $private -Raw | gh secret set RELEASE_SIGNING_KEY --repo $repo
    if ($LASTEXITCODE -ne 0) { throw 'Could not store the secret on GitHub.' }

    # public half -> update-key\ (Mac: PKCS#1 DER; Windows: .NET RSA XML)
    New-Item -ItemType Directory $out -Force | Out-Null
    & $openssl rsa -in $private -RSAPublicKey_out -outform DER -out (Join-Path $out 'update-key.der') 2>$null
    $hex = ((& $openssl rsa -in $private -noout -modulus 2>$null) -split '=')[1].Trim()
    $bytes = New-Object byte[] ($hex.Length / 2)
    for ($i = 0; $i -lt $bytes.Length; $i++) { $bytes[$i] = [Convert]::ToByte($hex.Substring(2 * $i, 2), 16) }
    $xml = '<RSAKeyValue><Modulus>' + [Convert]::ToBase64String($bytes) + '</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>'
    [IO.File]::WriteAllText((Join-Path $out 'update-key.xml'), $xml)
}
finally {
    if (Test-Path $private) { Remove-Item $private -Force }
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host ''
Write-Host 'Done. The private key is stored as the RELEASE_SIGNING_KEY secret on GitHub and deleted here.'
Write-Host 'The public key is in update-key\ (update-key.xml and update-key.der). Commit those two files.'
