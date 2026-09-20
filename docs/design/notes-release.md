# Release workflow notes carried from the old tree

Logic worth keeping from the deleted `.github/workflows/release.yml`, for stage 6.

## Version from tag

```powershell
$version = $env:GITHUB_REF_NAME.TrimStart('v')
if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
  Write-Error "Tag '$env:GITHUB_REF_NAME' does not look like a semantic version"
  exit 1
}
Add-Content -Path $env:GITHUB_ENV -Value "PRODUCT_VERSION=$version"
```

## Checksums

```powershell
$hash = (Get-FileHash $asset -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $asset -Leaf)" | Add-Content -Path release-assets/SHA256SUMS.txt -Encoding ascii
```

## Release notes from the annotated tag

`actions/checkout` peels the tag to a lightweight ref, so re-fetch the tag object:

```powershell
git fetch origin --force "+refs/tags/$($env:GITHUB_REF_NAME):refs/tags/$($env:GITHUB_REF_NAME)"
git tag -l --format='%(contents)' $env:GITHUB_REF_NAME | Set-Content -Path release-notes.md -Encoding utf8
```

Fail the job if the annotation is empty. Mark the release as a prerelease when the
version contains a hyphen. The job needs `permissions: contents: write`.

With Velopack, `vpk upload github` creates the release and uploads the package; the
hashes are appended afterwards with `gh release edit --notes-file`.
