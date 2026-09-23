# Acceptance checklist

What I run by hand, on my own PC and in Forza Horizon, for every release. The
automated tests cover the logic; this covers the parts only a person at the keyboard
can judge. Tick every box, or write down why one was skipped.

Everything down to the update section runs on the packed build before I push the tag.
The update section needs the release to be public, so it runs right after I publish
it, on a copy of the release before. The first release has nothing to update from.

Record the version, keyboard, firmware and game at the top of the run:

- Version:
- Keyboard and firmware:
- Game and store (Steam, Microsoft Store, Game Pass):
- Date:

## Install

- [ ] SmartScreen shows More info, then Run anyway, and nothing else asks for
      administrator rights.
- [ ] The app starts after the install, with its icon in the title bar, the taskbar,
      the Start menu and the desktop shortcut.
- [ ] With ViGEmBus uninstalled, the Setup card opens by itself, Start asks for the
      driver, and the button opens the official release page. After installing the
      driver and coming back to the window, the card says it is running.

## First run

- [ ] The keyboard card finds the Apex Pro, shows its firmware and says it is tested.
- [ ] The game card lists Forza while it runs, and remembers it after the app restarts.
- [ ] Calibrating W, A, S and D works as the README describes. Holding a key during Set
      released is refused and says why.
- [ ] Start stays off until all four are calibrated, and the status card says which are
      missing.

## Driving

Start mapping before launching the game, then drive for at least 30 minutes.

- [ ] Windows shows one Xbox 360 controller while mapping and none after Stop
      (`joy.cpl`).
- [ ] Half pressing W gives part throttle and pressing it fully gives full throttle.
      The same for S and braking.
- [ ] A and D steer in proportion to how far they are pressed, both ways, and the wheel
      comes back to centre on release.
- [ ] Space, Q, E, R, Tab, Left Shift, G and the arrow keys do what the Forza profile
      maps them to.
- [ ] W, A, S and D never also act as keyboard keys in the game.
- [ ] Nothing stays pressed after a key is released, all race long.
- [ ] The status card reads the keyboard about every 12 ms with the Forza profile,
      and the slowest 1% stays near that. The keyboard answers one sensor group every
      6 ms and the profile reads two.

## Leaving and coming back

- [ ] Alt-tab to Notepad while mapping: W, A, S and D type normally there.
- [ ] Back in the game while holding W: no throttle until W is released and pressed again.
- [ ] Ctrl+Left Alt+F12 stops mapping at once, from inside the game.
- [ ] Unplugging the keyboard mid-race pauses mapping with the controller at rest.
      Plugging it back in resumes, and held keys wait for a release first.
- [ ] Closing the game ends the session and says why.
- [ ] Closing the app while mapping removes the controller and the keyboard works
      normally at once.
- [ ] Sleep and wake while mapping ends the session.
- [ ] A mapped key pressed on another keyboard shows the notice once.

## Update and uninstall

- [ ] The released installer's SHA-256 matches the one in the release notes.
- [ ] Installed on the previous release, the Setup card offers the new one. Update
      downloads it, Restart and update installs it, and the app comes back on the new
      version with its profiles, calibration and settings intact.
- [ ] Update and Restart and update are off while mapping.
- [ ] With an update downloaded and mapping running, opening the app from the Start
      menu only brings its window forward. Mapping carries on, and the update waits for
      a restart.
- [ ] Uninstalling from Settings > Apps removes the app and its shortcuts and leaves
      `%AppData%\ApexAnalogMapper`.

## After the run

- [ ] The log has each session start and end. It names keys only in calibration
      lines and in warnings about a key's calibration.
- [ ] Anything that failed has an issue.
