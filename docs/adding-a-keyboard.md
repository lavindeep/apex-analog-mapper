# Add support for your keyboard

The app reads key depth from the original Apex Pro TKL, the one model I own. If you own
another Apex Pro, you can help add it. This guide is for you, and for the coding agent you
point at this repo.

Every path ends with evidence I can check without your keyboard. For a model the app can
read, that is a capture the tests read and a short clip of the app on your board. For one
it can't, it is an issue with what your keyboard told the app.

## Find your path

Choose your keyboard on the keyboard card, then follow the path for what the card says.

- **Not tested yet.** The app knows the model, but nobody has checked it. Follow
  [Check a model the app knows](#check-a-model-the-app-knows). If the check passes, the
  code change is two lines.
- **Not supported.** The app knows the model and never reads its sensors. Follow
  [Report a model the app can't read](#report-a-model-the-app-cant-read).
- **Your keyboard isn't in the list.** The app doesn't know its product ID. Follow
  [Add a model the app doesn't know](#add-a-model-the-app-doesnt-know).

## What your agent can do

An agent on your PC can build the app and run the tests. With your agreement, it can run
the five hardware tests in [AGENTS.md](../AGENTS.md#hardware-tests), which only read the
keyboard, and it can start your build and read the cards' text. It can't press keys, so the
capture and the clip need your hands.

## Check a model the app knows

### Take a capture

You need the latest release and the keyboard. No code.

1. On the keyboard card, read the text above **Accept the risk and read the sensors**,
   then press it. If you already accepted the risk, press **Check the sensors** instead.
2. When the card says "Keep your hands off the keyboard for a moment", let go of it.
3. When the card says "Press any key all the way down and hold it", hold **W** all the way
   down. Use W and no other key. The tests check that W moved the sensor the app expects
   for W.
4. Keep holding W until the card says "The sensors respond to key presses." If it says
   anything else, follow [If the check fails](#if-the-check-fails).
5. Press **Export a capture** and save the file. The card lists what the file holds. It
   has no record of your typing. Each reading shows which keys were down at that moment,
   which should be W alone.

If you'd rather not change code, stop here. Open a
[keyboard support issue](https://github.com/lavindeep/apex-analog-mapper/issues/new?template=keyboard_support.yml)
and attach the capture. Anyone who builds the app can take it from there.

### Make the change

1. The app saves the capture as `apex-capture-<product id>.json`. Rename it to
   `<product id>-<firmware>.json`, for example `1642-1.2.0.json`, and copy it unchanged
   into `tests/ApexMapper.Core.Tests/Captures`. Create that folder if it isn't there.
2. In `src/ApexMapper.Core/Sensors/KnownKeyboards.cs`, set the model to
   `SensorSupport.Verified` and add the firmware to the model's `VerifiedFirmware` list.
   Each firmware in that list needs its own capture. Update the doc comment on `Model`
   that names the verified models.
3. In the README, add the model and firmware to "What you need".
4. Run `dotnet test ApexAnalogMapper.slnx -c Release`. `TestedKeyboardEvidenceTests`
   reads your capture. If it fails, its message says what is wrong, for example "Holding
   W should raise sensor 16 by at least 300 counts. It moved 12." Follow
   [If the check fails](#if-the-check-fails) and leave the test as it is.

### Prove it on your keyboard

The clip shows that the app reads W, A, S and D on your board with no calibration and no
Learn step. Your build reads and writes the same app data as your installed copy, so set
that aside first.

1. Close the installed app. While it runs, starting your build only brings the installed
   app's window forward.
2. Copy your app data somewhere safe, then move your calibration out of the way, in
   PowerShell:

   ```
   Copy-Item "$env:APPDATA\ApexAnalogMapper" "$HOME\ApexAnalogMapper-backup-$(Get-Date -Format yyyyMMdd-HHmm)" -Recurse
   Rename-Item "$env:APPDATA\ApexAnalogMapper\calibration" calibration-saved
   ```

   If the second line says the folder doesn't exist, you have no calibration to move.
3. Build with `dotnet build ApexAnalogMapper.slnx -c Release`, then start
   `src\ApexMapper.App\bin\Release\net10.0-windows\ApexAnalogMapper.exe`.
4. Start recording. Any screen recorder works, such as the Snipping Tool's video mode. A
   phone video that shows your hand and the screen together is stronger evidence. Keep
   the clip under 10 MB so GitHub accepts it.
5. Open the keyboard card. It names your model, says "Tested" under it, and shows the
   firmware.
6. On the profile card, choose Forza.
7. Open the calibration card. Each of W, A, S and D says "Not calibrated. With the key up,
   press Set released." If a row asks you to press **Learn**, your build doesn't treat
   the model as tested, so check the change.
8. Press W, A, S and D one at a time, halfway and then fully. Each key's bar follows that
   key, and no other bar moves. Press no button on the card.
9. Stop recording and close your build. Put your calibration back:

   ```
   Rename-Item "$env:APPDATA\ApexAnalogMapper\calibration-saved" calibration
   ```

10. If your agent can run commands on this PC, you can let it run the five hardware tests
    in [AGENTS.md](../AGENTS.md#hardware-tests). Before it starts, close the app, unplug
    every Apex Pro except this one, and keep your hands off the keyboard until it
    finishes, about ten seconds.

Open a pull request with the capture, the change, and the clip. Paste the hardware test
summary if your agent ran them.

## If the check fails

Run it once more. The keyboard card offers the check only while the app treats the model
as untested. If your build already marks the model `Verified`, use the installed release
when it shows the model as "Not tested yet". Otherwise set the model back to
`SensorSupport.Unverified` in your build and build it again. Press **Check the sensors**,
keep your hands off until the card asks for a key, then hold W all the way down until the
card answers. If you are fixing a failed test, export the new capture, replace the old
one, and mark the model `Verified` again.

If it fails again, the keyboard answers differently from the TKL, or its keys sit on
different sensors. Press **Export a capture** and open a
[keyboard support issue](https://github.com/lavindeep/apex-analog-mapper/issues/new?template=keyboard_support.yml).
Paste the exact message the card or the test showed, and attach the capture. If the
keyboard sent a reply the app couldn't read, the capture holds it. Together with the
message, that tells me which of the two it is.

## Report a model the app can't read

The app sends the keyboard two requests, from an allowlist in
`src/ApexMapper.Core/Sensors/SensorRequest.cs`: the firmware version and the sensor read.
A model that doesn't answer the sensor read needs a different request. That changes what
the app sends to people's keyboards, so it starts in an issue, and I decide whether it
ships.

Open a
[keyboard support issue](https://github.com/lavindeep/apex-analog-mapper/issues/new?template=keyboard_support.yml)
with:

- The model's name as SteelSeries sells it.
- Its Hardware Ids line from Device Manager. [Add a model the app doesn't know](#add-a-model-the-app-doesnt-know)
  says where to find it.
- The firmware the keyboard card shows.
- How it was connected: USB cable, wireless receiver, or Bluetooth.
- The capture, if the card offered **Export a capture**.

If you study SteelSeries firmware, [HallJoy's firmware research](https://github.com/PashOK7/HallJoy/blob/056ba64f9db5f918bf16dc4a89df4b52234c8d24/docs/current/STEELSERIES_FAMILY_REVIEW_2026-09-25.md)
maps the command tables of several families. A request is worth proposing when a command
table names it as a read. Add to the issue the request as hex and where you found it. If
you already have the keyboard's replies to it, add them as hex: with no key pressed, with
W halfway, and with W fully down. Share hex only, never a USB capture file, because a USB
capture records every key you press while it runs.

Send a new request to your keyboard only through a test build we agree on in the issue.
Once a request ships in one, it needs the same capture and clip as
[Check a model the app knows](#check-a-model-the-app-knows).

## Add a model the app doesn't know

1. Find the product ID. In Device Manager, open your keyboard's properties. On
   **Details**, choose **Hardware Ids**. The value has the form `HID\VID_1038&PID_XXXX`,
   and `XXXX` is the product ID.
2. If you'd rather not change code, open a
   [keyboard support issue](https://github.com/lavindeep/apex-analog-mapper/issues/new?template=keyboard_support.yml)
   with the model's name as SteelSeries sells it and that Hardware Ids line, and stop here.
3. Add a line to `Models` in `src/ApexMapper.Core/Sensors/KnownKeyboards.cs` with the
   product ID, the model's name as SteelSeries sells it, `SensorSupport.Unverified`, and
   an empty firmware list.
4. Close the installed app and copy your app data somewhere safe with the `Copy-Item`
   command in step 2 of [Prove it on your keyboard](#prove-it-on-your-keyboard). Your
   build reads and writes the same folder.
5. Build and start the app. The keyboard card now shows the model as "Not tested yet".
   Follow [Check a model the app knows](#check-a-model-the-app-knows) with your build.
