# Windows distribution and signing

The selected public distribution channel is **Microsoft Store MSIX**, under the reserved app
**Muesli: Voice & Meeting Notes** (Store ID `9NL9X2427H2F`). See
[WINDOWS_STORE_RELEASE.md](WINDOWS_STORE_RELEASE.md) for the build and upload instructions.

The Store signs the package after certification. The Store build does not require an SSL.com
account, eSigner subscription, code-signing certificate, signing secrets, or a cloud-signing workflow.
Unsigned Store submission packages must not be offered as publicly installable website downloads.
The website will link to the Store listing after publication.

`build-signed-release.ps1` and `sign-windows-release.ps1` remain legacy helpers for independently
signed, direct-download MSIX packages. They are not called by the Store workflow. A separate trusted
certificate would be needed if that distribution channel is introduced later.
