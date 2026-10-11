# Signed Windows installer

Muesli ships on Windows as one signed MSIX downloaded from muesli.works. Users double-click it and
Windows' App Installer installs it. No Windows App Runtime or Visual C++ redistributable needs to be
installed first: both are carried inside the package.

## 1. Get a code-signing certificate (once)

Buy an **OV code-signing certificate** in the company's legal name (Sectigo, SSL.com, DigiCert and
others). Since 2023 the private key must live on a hardware token or a cloud HSM; either works as
long as the certificate shows up in `certmgr.msc` → Personal on the build PC (token vendors install
a driver that does this).

Note the certificate's exact subject, e.g. `CN=Muesli Works Pvt Ltd, O=Muesli Works Pvt Ltd, L=City,
S=State, C=IN`, and its thumbprint:

```powershell
Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Format-List Subject, Thumbprint, NotAfter
```

The subject becomes the package publisher. **Keep it identical across releases**: Windows treats a
package with a different publisher as a different app, so users would not get an upgrade.

## 2. Build and sign

From a clean checkout of the release commit (`git status` empty), with the pinned .NET SDK on PATH:

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
.\scripts\build-signed-release.ps1 `
  -CertificateThumbprint <thumbprint> `
  -ExpectedPublisherSubject '<exact subject from step 1>'
```

This builds the Release MSIX, writes the publisher into it, signs it with a DigiCert RFC 3161
timestamp (so it stays valid after the certificate expires), verifies the signature, and writes
hashes to `artifacts\signed-release-hashes.json`. The installer is
`artifacts\msix\Muesli.Windows.WinUI_<version>_x64.msix`.

## 3. Test on a clean PC, then publish

Install it on a PC that has never had Muesli: first run, microphone prompt, model download,
dictation, a meeting, Start on login, then uninstall. Then upload the `.msix` to muesli.works.

For each new release, bump `MuesliVersion` in `Directory.Build.props` (and the manifest `Version`)
so Windows installs it as an upgrade; history in `%APPDATA%\muesli` is kept across upgrades and
uninstall.

## Notes

- A new certificate has no SmartScreen reputation yet, so early downloads may show a warning that
  fades as installs accumulate.
- Updates are manual in v1: users download the new `.msix` from the website.
