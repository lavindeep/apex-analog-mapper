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

# Stage 5 feel step inputs, from the stage 1 feel review

Recorded here so the feel step starts from numbers rather than memory. The travel
model behind the percentages is fitted to one stage 0 datum (0.2 mm of about 4 mm at
2.5 percent of W's count span, so `counts = travel^1.23`); treat it as a hint and
re-measure with the maintainer's hands.

## Defaults the reviewer would ship

| binding | target | response (exp, sat, dz) | ramps | mode / conflict | why |
| --- | --- | --- | --- | --- | --- |
| W | RightTrigger | 0.80, 1.00, 0.010 | 0 / 0 | - | Soft (1.6) reads 0 at the firmware's own actuation point and needs half the travel for a quarter throttle. An exponent near 0.8 tracks millimetres under the travel model. Saturation 1.0 because W clips at 4095. Deadzone 0.01 puts first motion at raw ~930 and kills creep from a resting finger. |
| S | LeftTrigger | 0.80, 0.85, 0.010 | 0 / 0 | - | Same pedal feel; S does not clip, so 0.85 puts full brake at about 88 percent of travel instead of needing a bottom-out. |
| A / D | LeftStickX | 0.85, 0.90, 0.010 | 0 / 0 | Position, LastInputWins | Centre resolution without Soft's dead region; 0.90 lets both A and D reach full lock despite their 65-count span difference. Keep Position: rate mode turns depth into steering speed and cannot hold a partial angle. |
| Left / Right, Up / Down | RightStickX / Y | Linear | 120 / 90 | Position, LastInputWins | Mechanical arrows are digital and today snap to full deflection in one tick. |
| buttons | - | Linear | 0 / 0 | - | Buttons are digital and ignore ramps and curves. |

## Things to decide with hands on the keys

- Last-input-wins snaps the stick to centre on the tick the opposite key leaves its
  noise band, and back to the held value when it is released (about 93 percent of
  stick scale in one tick, twice per counter-steer). Digital SOCD does the same. If it
  feels wrong on analog steering, the cheap options are a `Sum` conflict rule
  (`positive - negative`, continuous everywhere) or a per-axis ramp applied only on
  the tick the winner changes. Do not add a blanket axis slew: a fast W tap crosses
  full travel in under one 6 ms sample and a 40 ms ramp would blunt it seven times.
- If rate mode ever becomes a default, give the binding a deadzone of at least 0.01:
  a finger resting a few counts past the band otherwise walks the stick to full lock
  in 12 to 80 s depending on the curve.
- Fallback release follows the binding's own ramp when it is nonzero. A long release
  ramp means a slow throttle drop during a fault; consider whether release should be
  capped at the 50 ms fallback rate.
