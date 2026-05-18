# Apex Pro Universal Analog Mapper

Windows 11 tray app that maps an Apex Pro keyboard to a virtual Xbox controller for any controller-compatible game.

- Spec: `docs/superpowers/specs/2026-05-17-apex-pro-analog-mapper-design.md`
- Plans: `docs/superpowers/plans/`

## Build

- macOS (cross-platform core only): `dotnet build ApexAnalogMapper.CrossPlatform.slnf`
- Windows (full app): `dotnet build ApexAnalogMapper.sln`

## Test

- `dotnet test ApexAnalogMapper.CrossPlatform.slnf`
