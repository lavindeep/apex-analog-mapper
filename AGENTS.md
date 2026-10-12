# Apex Analog Mapper

Apex Analog Mapper is a Windows app that reads how far each key on a SteelSeries Apex Pro
is pressed and drives a virtual Xbox 360 controller with it. It reads the keyboard's
vendor HID interface with HidSharp, creates the controller through the ViGEmBus driver,
and shows a WPF window built with WPF-UI. It is in alpha, and people drive with it every
day.

## What we never compromise

1. **The keyboard only gets reads.** The app sends two requests, the firmware version
   and the sensor read, through the allowlist in `src/ApexMapper.Core/Sensors/SensorRequest.cs`.
   Nothing it sends changes firmware, actuation or lighting.
2. **Typing stays private.** Keystrokes stay in memory. The log names a key only in a
   calibration line or a calibration warning. A capture holds single sensor readings.
   `SECURITY.md` states these promises, and it changes in the same PR as the behavior.
3. **The PC ends up as it started.** When mapping stops, crashes or is killed, every key
   is released and the virtual pad is gone (`CrashGuard`, `Watchdog`, the
   Ctrl+Left Alt+F12 stop).
4. **Releases work on real keyboards.** The app offers each new release when it checks
   for updates at start, and one press of Update installs it on the next restart. A
   release passes `docs/acceptance.md` on the tested keyboard first.

## A note from Lavindeep

I like taking complex problems and finding the simplest solution that holds. Build what
the task needs and nothing ahead of it. Ask before adding a package. Propose refactors and
bold ideas instead of slipping them into a change; I'm glad to hear them.

Most keyboards this app could support are ones I don't own. When you work for a
contributor, the evidence from their keyboard matters more than the code. I review the
capture and the clip before I read the diff.

The rest of this file is a set of good defaults for this repo. The contributor's
instructions win over it.

## A small glossary

- **you** means the agent reading this file.
- **I** and **the maintainer** mean Lavindeep.
- **contributor** means the person directing you. They may own a keyboard I don't.
- **user** means a person who runs the app.
- **board** means one connected keyboard. The app tells boards apart by their Windows
  container ID.
- **model** means a product ID in `KnownKeyboards`. Its `SensorSupport` is `Verified`,
  `Unverified` or `Unsupported`, which the window shows as "Tested", "Not tested yet"
  and "Not supported".
- **sensor group** means one reply to the sensor read, which covers 14 sensors. Five
  groups cover 70. A raw reading runs from 0 to 4095. A key reads about 850 at rest, and a slot
  with no key reads under 50.
- **capture** means the JSON file the keyboard card exports for an untested model.
- **session** means mapping is running: the keyboard hook, the sensor poller and the pad.

## The other docs

Read the doc that matches the task before you start it.

- To open an issue or a pull request, read `CONTRIBUTING.md`. It sets what an issue
  needs, the scope and title of a PR, what its description says, and what I'm unlikely
  to merge.
- To file an issue for the contributor, use the form that fits in `.github/ISSUE_TEMPLATE`:
  `bug_report.yml` for a bug and `keyboard_support.yml` for a keyboard model. Questions
  and ideas go in a blank issue.
- To write a PR description, fill the sections of `.github/pull_request_template.md`.
- To change what the app reads, stores, logs or sends, update `SECURITY.md` with it.
- To change what a user sees or does, check `README.md`, which walks through the app.
- To prepare a release, read `docs/acceptance.md`. It is the checklist I run by hand.
- To make a keyboard model work, test an untested one, report one the app can't read,
  or review a PR that does, follow `docs/adding-a-keyboard.md`.

## The ways to hurt someone

1. **Hardware tests act on the real desktop.** With `APEX_HW_TESTS=1`, some tests type W
   into whatever window is in front and others push full stick and button input through
   a virtual pad. `APEX_ELEVATED_CHECK=1` adds a UAC prompt, `APEX_UNPLUG_CHECK=1` waits
   for a cable pull, and `APEX_SOAK=1` runs for minutes. Run hardware tests only after
   the contributor agrees, and only the ones they agree to. [Hardware tests](#hardware-tests)
   lists the safe set.
2. **App data is live.** Every build of the app, installed or not, reads and writes
   `%APPDATA%\ApexAnalogMapper`: the contributor's profiles, calibration, settings and
   log. There is no override. Copy the folder before you run a build, and leave it in
   place afterwards.
3. **A tag ships.** `.github/workflows/release.yml` publishes a release from an annotated
   `v*` tag, and the app offers it to users the next time it checks for updates. Leave
   tags to the maintainer. A test build is a local build or a CI artifact.
4. **A new request reaches people's keyboards.** Extending the allowlist is a maintainer
   decision, agreed in an issue before the code.

## Hit every surface

A change that works where you tested it is usually missing somewhere else. Before you
call a change done, walk this list:

- **Cards.** The status, keyboard, game, profile, calibration and setup cards each react
  to board and session state. A new state needs a decision on each card. The order of
  checks in `StatusViewModel.FindBlocker` decides which message the status card shows.
- **The HID layer.** What reaches the keyboard is enforced in `HidVendorDevices` and
  `SensorRequest`, not only in the cards.
- **Screen readers.** Text that changes uses `views:Live.Text`, and a button without
  text content gets `AutomationProperties.Name`. `WindowRenderingTests` checks both.
- **Light and dark.** `WindowRenderingTests` renders every card in both themes and fails
  on any binding error. Add a scenario for a new window state.
- **Documented promises.** The docs in [The other docs](#the-other-docs) describe
  behavior. Update the ones your change makes wrong.
- **The version.** `Directory.Build.props` holds it. Examples in the README, the issue
  forms, `scripts/pack.ps1` and `release.yml` repeat it, so search for the old version.

## Commands

The .NET 10 SDK from `global.json` builds the app, on Windows only.

- `dotnet build ApexAnalogMapper.slnx -c Release`. Analyzer warnings and code style
  fail the build.
- `dotnet test ApexAnalogMapper.slnx -c Release` runs everything in about 25 seconds.
  Tests that need the keyboard or the desktop skip unless `APEX_HW_TESTS` or `APEX_SOAK`
  is 1.
- To run one class or test, use the Microsoft Testing Platform switches:
  `dotnet test --project tests\ApexMapper.App.Tests -c Release --filter-class "*KeyboardViewModelTests"`.
  `--filter-method "<namespace>.<class>.<method>"` takes a full name and can repeat.
- To see the window, set `APEX_SCREENSHOTS` to a folder and run `WindowRenderingTests`.
  It saves every card in light and dark.
- CI packs every PR with Velopack. The artifact `apex-analog-mapper-ci-<run number>`
  holds `ApexAnalogMapper-win-Portable.zip`. Extract it into a new, empty folder and run
  `current\ApexAnalogMapper.exe`. The CI version is `0.1.0-ci.<run number>`, so it
  offers the latest release as an update. Leave Update alone.

### Hardware tests

These five read the keyboard and touch nothing else. Get the contributor's agreement,
then check that the app is closed, that exactly one Apex Pro is plugged in, and that
nobody touches the keyboard for the ten seconds they run. On a "Not tested yet" model
they send the same sensor read the keyboard card asks consent for, so tell the
contributor that. On a "Not supported" model the three sensor tests fail without
sending it.

```
$t = 'ApexMapper.Windows.Tests.Hardware.HardwareTests'
$env:APEX_HW_TESTS = '1'
try {
	dotnet test --project tests\ApexMapper.Windows.Tests -c Release `
		--filter-method "$t.Discovery_finds_the_connected_board_with_its_vendor_interface" `
		--filter-method "$t.The_vendor_interface_answers_the_firmware_query" `
		--filter-method "$t.The_poller_cycles_two_groups_against_live_signatures_under_the_threshold_with_no_faults" `
		--filter-method "$t.The_poller_reads_all_five_groups_the_way_the_live_sensor_does_with_no_faults" `
		--filter-method "$t.A_live_reply_that_fails_its_signature_is_kept_exactly_as_the_keyboard_sent_it"
} finally {
	Remove-Item Env:APEX_HW_TESTS
}
```

Expect 5 run and 5 passed. A test that types or drives the pad runs only when the
contributor asks for it by name.

### What you can do with the keyboard

Besides the five tests, you can start a build and read its window through Windows UI
Automation from PowerShell (`Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes`).
Every card's text and every control's name are there, so you can confirm what the
keyboard and calibration cards say. You can't press keys. A capture and a clip need the
contributor's hands, so ask them for those, and leave the buttons that read sensors or
save calibration to them.

## Verifying

- Run the tests you touched, then the whole suite before a PR.
- Test doubles live in `tests/ApexMapper.App.Tests/ViewModels/Fakes.cs` (`AppHarness`,
  `FakeSensor`) and `tests/ApexMapper.Windows.Tests/Hid/FakeVendorStream.cs`. Real
  replies from the maintainer's TKL are in `tests/ApexMapper.Windows.Tests/fixtures`.
- The sensor poll cycle allocates nothing, and `SensorPollerTests` checks it. Keep
  allocation off that path.
- A window change gets a `WindowRenderingTests` scenario and screenshots from it.
- A change to the sensor path, the hook or the pad names the keyboard, firmware and game
  it ran on.

## Pull requests

- Upload screenshots and clips to the PR. Captures are the one kind of evidence the repo
  keeps, in `tests/ApexMapper.Core.Tests/Captures`.
- End the description with the model and harness that did the work.
- Cursor Bugbot and a security reviewer comment on every PR. Check each finding against
  the code, fix the real ones, and reply with the reason for the rest.

## Where code lives

- `src/ApexMapper.Core` is logic with no Windows APIs: keys, calibration, response
  curves, the sensor protocol, the engine, profiles and JSON storage.
- `src/ApexMapper.Windows` is the HID interface, the low-level hook, Raw Input, the
  ViGEm pad, and the session with its timing.
- `src/ApexMapper.App` is the WPF window, its view models, storage paths and Velopack
  updates. `Program.cs` runs Velopack before any WPF code.
- `tests/` has one project per source project. `ApexMapper.Harness` is a child process
  for the tests that kill or crash a process.

## Taste

- Text in the window says what happened and what to do next, in plain words.
- Comments sit above the member and say why. They change with the code beneath them.
- If a rule here fights the task, say so and get the maintainer's sign-off before you
  break it.
