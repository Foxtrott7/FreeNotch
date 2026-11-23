# Winget Manifest Templates (`Foxtrott7.FreeNotch`)

These templates prepare the Windows Package Manager (`winget`) submission to [`microsoft/winget-pkgs`](https://github.com/microsoft/winget-pkgs).

## First Submission (after publishing a release with `Setup.exe`)

1. Publish a GitHub Release (e.g. `v1.0.3`) that includes `FreeNotch-v1.0.3-win-x64-setup.exe` and `SHA256SUMS.txt`.
2. Copy the SHA-256 hash of `FreeNotch-v1.0.3-win-x64-setup.exe` from `SHA256SUMS.txt` and update the templates:
   ```powershell
   .\installer\winget\update-manifests.ps1 -Version 1.0.3 -InstallerSha256 "<SHA256>"
   ```
3. Validate the manifests locally with `winget`:
   ```powershell
   winget validate --manifest .\installer\winget
   ```
4. Fork `microsoft/winget-pkgs` and copy the 4 `.yaml` files into:
   ```text
   manifests/f/Foxtrott7/FreeNotch/<VERSION>/
   ```
5. Open a pull request to `microsoft/winget-pkgs` (title: `New package: Foxtrott7.FreeNotch version <VERSION>`).

## Future Releases

Once accepted in `microsoft/winget-pkgs`, subsequent releases can be submitted automatically via `wingetcreate`:

```powershell
wingetcreate update Foxtrott7.FreeNotch --version <VERSION> --urls https://github.com/Foxtrott7/FreeNotch/releases/download/v<VERSION>/FreeNotch-v<VERSION>-win-x64-setup.exe --submit
```
