# Requirements ledger

One line per requirement in `2026-09-19-rewrite.md`. The evidence column is filled by
the trace reviewer at each stage gate with `file:line` and a test name. Empty evidence
after the owning stage closes is a gap.

## Stack and shape

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| S1 | C# on .NET 10 LTS. | 0 | |
| S2 | WPF with WPF-UI, following the OS light or dark theme. | 0, 5 | |
| S3 | Velopack per-user installer, Add or Remove Programs entry, uninstall, delta updates from GitHub Releases, self-contained publish with the size in the README. | 0, 6 | |
| S4 | Three projects (Core, Windows, App), Core has no Windows APIs. | 0, 1 | `src/ApexMapper.Core/ApexMapper.Core.csproj` targets `net10.0` with no package references. |
| S5 | Hardware tests gated by `APEX_HW_TESTS=1`; soak by `APEX_SOAK=1`. | 2, 3 | `HardwareFactAttribute` (`SkipUnless` on `Enabled`). The soak gate is stage 3. |
| S6 | MIT license file present. | 0 | |

## Threads and hot path

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| T1 | Raw Input pump on its own thread, message-only window, `RIDEV_INPUTSINK | RIDEV_DEVNOTIFY`, attributes key events to a device, raises device arrival and removal. | 2 | `Input/RawInputPump.cs` (`RegistrationFlags`, `Run`, `OnInput`, `DeviceChanged`). `RawInputRingTests.The_registration_asks_for_input_from_every_window_plus_device_notifications`, `RawInputPumpTests.Stop_joins_within_half_a_second_and_clears_the_container_cache`; hardware `HardwareTests.Raw_input_sees_a_synthetic_w_with_no_device`. |
| T2 | Hook thread with its own message loop, above-normal priority, runs the watchdog and the stop hotkey. | 2, 3 | `Input/KeyboardHook.cs` (`Start` sets `AboveNormal`, `Run` owns the loop, `OnTimer`, `Handle` raises `StopRequested`). `KeyboardHookLifecycleTests.The_timer_ticks_on_the_hook_thread_and_stop_joins`, `KeyboardHookTests.The_stop_chord_raises_the_handler_and_is_swallowed_and_a_throwing_handler_is_counted`. The watchdog is stage 3. |
| T3 | Sensor thread polls back to back for the needed groups and publishes timestamped snapshots. | 2 | `Hid/SensorPoller.cs` (`Cycle`: every configured group, `Stamp` after the last, `Publish`). `SensorPollerTests.Runs_verifies_firmware_and_publishes_fresh_snapshots`, `One_slow_cycle_leaves_the_snapshot_stale_without_a_fault`. |
| T4 | Engine thread on a high-resolution 1 ms timer, submits when the packed report changes, capped at 500 Hz, publishes a tick timestamp. | 3 | |
| T5 | Hook callback, sensor loop, and engine tick allocate nothing. | 1, 2, 3 | `Engine/Mapper.cs` (arrays sized at construction, `ref` report, no LINQ in the tick). `MapperTests.Tick_allocates_nothing`, `MapperTests.Tick_allocates_nothing_on_the_fallback_gated_rate_and_every_target_paths`. Stage 2: `KeyboardHookTests.The_callback_path_allocates_nothing`, `SensorPollerTests.Cycles_allocate_nothing`. The engine tick's thread is stage 3. |
| T6 | `GCSettings.LatencyMode = SustainedLowLatency` from Start to Stop, restored on every exit path. | 3 | |
| T7 | Hook callback reads `KBDLLHOOKSTRUCT` by pointer, reads a cached foreground flag, reads no window state, makes no blocking call, and takes no lock. | 2 | `KeyboardHook.Callback` and `Handle` (pointer read, `ForegroundFlag` read, `HookPolicy` volatile state, store CAS). `KeyboardHookTests.The_callback_path_allocates_nothing`; hardware `HardwareTests.The_hook_swallows_a_synthetic_w_in_test_mode_and_the_callback_is_cheap` (p99.9 across swallow and pass-through). |
| T8 | Foreground tracker on its own thread with `WINEVENT_OUTOFCONTEXT`. | 2 | `Input/ForegroundTracker.cs` (`Run`). `ForegroundTrackerLifecycleTests.The_current_window_becomes_the_game_when_its_path_is_chosen_and_stop_clears_the_flag`, `Stop_from_the_changed_handler_returns_at_once_and_the_thread_exits`. |

## Key state store

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| K1 | One slot per scan code across plain, E0, and E1 pages. | 1 | `Keys/ScanCode.cs`. `ScanCodeTests.Extended_keys_get_distinct_slots_from_their_plain_twins`, `ScanCodeTests.Every_valid_code_round_trips_through_its_slot`. |
| K2 | Each slot holds the digital state, the analog depth, and a gate bit, readable together. | 1 | `Keys/KeyStateStore.cs` (one packed long, one volatile read). `KeyStateStoreTests.Both_values_live_in_one_slot`, `KeyStateStoreTests.Concurrent_writers_on_the_same_slot_lose_no_updates`. |
| K3 | Gate set on session start, return from alt-tab, hook install, keyboard reconnect. | 1, 3 | `KeyStateStore.GateUnknown`. `KeyStateStoreTests.Gate_unknown_gates_analog_keys_and_held_digital_keys_only`. Stage 3 wires the four events. |
| K4 | Gated key contributes zero and its ramp and handover state reset; gating either key of an axis resets the axis's rate and conflict state. | 1 | `Engine/Mapper.cs` `Resolve` and the axis loop. `MapperTests.Gate_during_a_ramp_restarts_the_ramp_from_zero`, `MapperTests.Gating_a_rate_axis_zeroes_it_at_once`, `MapperTests.Gating_one_key_of_a_digital_axis_lets_the_other_steer_from_zero`. |
| K5 | While a key's sensor reading is available only an analog reading inside the noise band clears its gate (a hook key-up never does: rapid trigger). While the reading is unavailable, and for digital keys always, any hook event other than an auto-repeat clears it. | 1 | `KeyStateStore.SetDigital`, `SetAnalog`. `KeyStateStoreTests.Hook_events_never_clear_an_analog_driven_key_while_its_reading_is_available`, `Hook_events_clear_an_analog_driven_key_while_its_reading_is_unavailable`, `Analog_at_rest_clears_an_analog_driven_key_and_a_pressed_reading_does_not`, `Digital_key_up_clears_the_gate_of_a_digital_key_and_a_repeat_does_not`; `MapperTests.Keys_held_at_start_stay_dead_until_released_once`. |
| K6 | Gate is never set by a sensor fault. | 1 | `Mapper.ApplySnapshot` writes NaN and never gates. `MapperTests.Stale_snapshot_never_sets_the_gate`. |
| K7 | The hook owns the digital value and gate clearing; Raw Input only attributes. | 2 | `KeyboardHook.Handle` is the only `SetDigital` caller in the Windows project; `RawInputPump` has no store. `KeyboardHookTests.A_swallowed_press_is_recorded_down_and_not_gated`, `An_injected_event_updates_the_store_outside_test_mode_but_is_not_swallowed`, `A_mapped_press_that_passes_through_is_recorded_down_and_gated`. |

## Analog input

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| A1 | Vendor interface selected by usage 0xFFC0:0x0001, 65-byte reports, same container id as the selected keyboard, exactly one match or nothing, non-exclusive open. | 2 | `Hid/HidVendorDevices.cs` (`Select`, `Open` with `Exclusive` false and 300 ms open timeouts; a known product id is also required). `HidVendorDevicesTests` (three tests); hardware `HardwareTests.The_vendor_interface_answers_the_firmware_query`. |
| A2 | Request layout `[0, command, selector, 0...]`; reply byte 0 must be 0. | 1 | `Sensors/SensorRequest.cs`, `SensorProtocol` byte 0 checks. `SensorProtocolTests.Only_two_commands_can_be_built`, `Structural_checks_reject_bad_replies`. |
| A3 | Command 0x90 parses the firmware string at offset 1. | 1 | `SensorProtocol.ParseFirmware`. `SensorProtocolTests.Firmware_fixture_parses_to_4_9_1`, `Firmware_parsing_rejects_non_version_replies`. |
| A4 | Command 0xD7 with selector 1..5 parses 14 raw and 14 filtered uint16 LE with zero padding at 57..64 and values at most 4095. | 1 | `SensorProtocol.ParseGroup`. `SensorProtocolTests.Rest_fixtures_parse_and_show_which_slots_have_keys`, `Structural_checks_reject_bad_replies`. |
| A5 | Only 0x90 and 0xD7 can ever be written, enforced in one place with a test, and nothing outside the vendor interface performs HID writes. | 1, 2 | `SensorRequest` private constructor, two factories, `WriteTo` refuses anything else including `default`. `SensorProtocolTests.Only_two_commands_can_be_built`. Stage 2: `IVendorStream` and `HidVendorDevices.Open` are internal and HidSharp's compile assets stop at the Windows project. `ArchitectureTests.Only_the_vendor_interface_calls_the_vendor_stream_write` (IL scan), `Only_the_vendor_interface_holds_a_vendor_stream`, `Only_the_vendor_device_file_touches_HidSharp`; `VendorInterfaceTests.The_allowlist_is_enforced_before_any_write`. |
| A6 | 70-slot sensor table with 65 mapped keys, arrows and function row unsupported, per-keyboard overrides from the learn step. | 1 | `Sensors/SensorMap.cs`. `SensorMapTests.Default_table_maps_sixty_five_keys`, `Mechanical_and_ambiguous_keys_are_unsupported`, `Overrides_win_over_the_table_and_can_add_keys`. |
| A7 | Every reply checked against its group signature (absent slots under 50 at rest, recorded at calibration) and for values above 4095; mismatch retires the handle. No input-queue drain. At open the polled groups' recorded signatures are checked pairwise; when any pair is indistinguishable, or a group has no signature, the canary runs every 5 cycles. | 1, 2 | `Sensors/GroupSignature.cs`. `GroupSignatureTests.Every_group_matches_only_itself_at_rest_and_held`, `A_shifted_reply_fails_and_held_keys_do_not`, `A_firmware_reply_in_a_sensor_slot_fails_the_range_check`. Stage 2: `SensorPoller.Cycle` (signature and `IsPlausibleGroup` per reply), `PollerConfig.For` (pairwise check). `SensorPollerTests.A_shifted_reply_fails_its_signature_and_retires_the_handle`, `A_firmware_reply_in_a_sensor_slot_retires_the_handle`, `Held_keys_do_not_fail_the_signature`, `The_canary_runs_every_five_cycles_when_the_polled_groups_cannot_be_told_apart`; hardware `The_poller_cycles_two_groups_against_live_signatures_under_the_threshold_with_no_faults`. |
| A8 | 0x90 canary every 50 cycles; mismatch retires the handle. | 2 | `SensorPoller.Cycle` and `CanaryMatches`. `SensorPollerTests.A_canary_mismatch_retires_the_handle`, `A_canary_with_the_right_version_and_dirty_padding_retires_the_handle`. |
| A9 | Any fault retires the handle, waits on a signalable backoff, reopens, and re-verifies firmware. | 2 | `SensorPoller.Fault`, `Backoff`, `Session`. The first reopen after a fault is immediate; the backoff applies from the second consecutive fault. `SensorPollerTests.A_read_timeout_is_a_fault_and_the_device_is_reopened_at_once`, `No_device_waits_and_stop_during_the_backoff_returns_long_before_the_backoff_elapses`. |
| A10 | Depth from the raw bytes, per the stage 0 measurement. | 1 | `Mapper.ApplySnapshot` reads `Raw`. `MapperTests.Full_profile_tick_maps_depth_and_buttons` (fixtures leave `Filtered` zero). |
| A11 | Freshness is a fixed 60 ms; a read timeout or three consecutive cycles of 100 ms or more is a fault; one slow cycle only stales the snapshot. | 1, 2 | `SensorSnapshot` derives the limit from `FreshnessMs`; `Sensors/CycleStats.cs`. `SensorSnapshotTests.Freshness_is_sixty_milliseconds_in_the_clock_s_ticks`, `CycleStatsTests.One_slow_cycle_is_not_a_fault_but_three_in_a_row_are`. Stage 2: `VendorInterface.TimeoutMs` (150 ms). `VendorInterfaceTests.A_timeout_is_a_fault`, `SensorPollerTests.Three_slow_cycles_in_a_row_are_a_fault`, `One_slow_cycle_leaves_the_snapshot_stale_without_a_fault`. |
| A16 | Calibration flags a clipping key (full press at the ceiling, `IsClipping`), stores 4095, and tells the user; a key at 4095 for seconds during a session is a plausibility warning. | 1, 5 | `KeyCalibration.IsClipping`. `NormalizerTests.A_key_that_reaches_the_ceiling_is_clipping`. Telling the user and the in-session warning are stage 5. |
| A17 | Calibration is per key; spans are never shared between keys. | 1, 4 | `Engine/CompiledProfile.cs` carries each key's own calibration. `MapperTests.Spans_are_never_shared_between_keys`. |
| A18 | The response preview shows counts as well as depth so the near-rest compression is visible; the feel step examines that region. | 5 | |
| A12 | Status card shows measured p50 and p99 cycle period. | 5 | |
| A13 | Calibration per container id and firmware: rest, full, noise band, sensor index. | 1, 4 | `Calibration/KeyCalibration.cs` fields. `NormalizerTests.Calibration_rejects_a_span_too_small_for_the_band`. The container-id and firmware keyed store is stage 4. |
| A14 | Depth re-normalised above the noise band per the design formula; in-band reads as released. | 1 | `Calibration/Normalizer.cs`. `NormalizerTests.Leaving_the_band_has_no_step`, `Inside_the_band_is_exactly_zero_and_at_rest`, `Wrong_side_of_rest_and_beyond_full_press_clamp_on_an_ascending_key`, `Descending_travel_normalises_the_same_way`. |
| A15 | Firmware change warns and keeps calibration data. | 4, 5 | |

## Fallback

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| F1 | Analog drives a key while its reading is available: snapshot fresh, group read, key calibrated. | 1 | `Mapper.Resolve` (`analogAvailable`), `Mapper.ApplySnapshot`. `MapperTests.Full_profile_tick_maps_depth_and_buttons`, `A_fresh_snapshot_missing_a_group_falls_back_only_the_keys_in_it`. |
| F2 | Stale or faulted sensor after a valid start falls back to the hook's digital state. | 1, 3 | `Mapper.ApplySnapshot` writes NaN when stale. `MapperTests.Sensor_fault_mid_press_ramps_to_full_in_fifteen_ms_and_recovery_ramps_back_to_the_live_depth`, `Dead_sensor_at_start_still_drives_analog_keys_from_the_hook`. Stage 3 owns the warning. |
| F3 | One ramp per key, set to the depth every analog tick, so fallback starts from the last analog value. | 1 | `Mapper.Resolve` seeds the ramp every analog tick. `MapperTests.Sensor_fault_mid_press_ramps_to_full_in_fifteen_ms_and_recovery_ramps_back_to_the_live_depth`. |
| F4 | Fallback rate is the binding's rate, or 50 ms full scale when the binding's rate is zero. | 1 | `Mapper.RampFor`. Same test (0.7 to 1.0 in 15 ms at 50 ms full scale); `RampTests.Seed_then_ramp_starts_from_the_seed`. |
| F5 | Recovery from fallback ramps the output from the fallback value to the live depth at the fallback rate; with the key at rest the ramp finishes its descent. Nothing holds the fallback value once the reading is back. | 1 | `Mapper.Resolve` converging state. `MapperTests.Sensor_fault_mid_press_ramps_to_full_in_fifteen_ms_and_recovery_ramps_back_to_the_live_depth` (1.0 to 0.7 over 15 ms), `Release_under_fallback_ramps_down_and_recovery_at_rest_finishes_the_ramp`. |
| F6 | Fallback warning with the reason shown while active. | 5 | |
| F7 | Start refused until every analog key in the profile is calibrated; Start points at the calibration card. | 3, 5 | |

## Keyboard blocking

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| B1 | Swallow a mapped key only while the game is foreground. | 2 | `HookPolicy.Decide`. `HookPolicyTests.B1_a_mapped_key_is_swallowed_only_while_the_game_is_foreground`; hardware `HardwareTests.The_hook_swallows_a_synthetic_w_in_test_mode_and_the_callback_is_cheap` (the asynchronous key state as the witness). |
| B2 | Never swallow injected events (outside test mode). | 2 | `HookPolicy.Decide` and `IsStopChord`. `HookPolicyTests.B2_injected_events_pass_unless_test_mode_is_on`, `An_injected_f12_is_not_the_stop_chord_outside_test_mode`. |
| B3 | Key-up swallowed only if its key-down was swallowed, and the game is still foreground. | 2 | `HookPolicy.Decide` (the extra term covers the gap between the flag dropping and `ForegroundLost`; fails safe). `HookPolicyTests.B3_a_key_up_is_swallowed_only_if_its_down_was`, `A_key_up_after_the_flag_dropped_passes_even_before_the_tracker_edge`. |
| B4 | Keys physically down at hook install are marked passed. | 2 | `KeyboardHook.MarkKeysAlreadyDown` (marked passed and gated, not asserted down). `HookPolicyTests.B4_a_key_down_at_install_passes_until_released`. |
| B5 | Ctrl, Alt, or Win chords pass through and release held mapped keys. | 2 | `HookPolicy.Decide` (modifier bits, `ReleaseHeld`), resynchronised from the asynchronous key state on the hook timer. `HookPolicyTests.B5_a_modifier_chord_passes_through_and_releases_held_mapped_keys`, `Every_reserved_modifier_counts`, `Synced_modifiers_replace_the_observed_ones`. |
| B6 | Ctrl, Alt, Win, F12 cannot be mapped. | 1, 5 | `ScanCode.IsReserved`; `Binding.Validate`. `ScanCodeTests.Reserved_keys_are_ctrl_alt_win_and_f12`, `ProfileJsonTests.Invalid_content_is_refused`. The editor is stage 5. |
| B7 | On foreground loss, swallowed-down keys are marked passed; nothing is injected. | 2 | `HookPolicy.ForegroundLost`; a repeat after the flag dropped passes too. `HookPolicyTests.B7_foreground_loss_hands_held_keys_back_without_injecting`, `Auto_repeat_follows_the_first_down`, `Decide_and_foreground_loss_race_without_corrupting_the_slot_state`. The wiring from `Changed` is stage 3. |
| B8 | Game matched by executable path, re-resolved on each foreground change. | 2 | `ForegroundResolver.Resolve`, `ForegroundTracker.Evaluate` (each event, every 250 ms, and on `GamePath`). `ForegroundResolverTests.The_match_survives_a_restart_with_a_new_process_id`, `ForegroundTrackerTests.The_flag_follows_the_game_and_changed_fires_once_per_transition`, `Choosing_the_game_after_it_is_already_in_front_is_picked_up_on_re_evaluation`. |
| B9 | `ApplicationFrameHost` unwrapped to the `CoreWindow` owner. | 2 | `ForegroundResolver.Resolve`. `ForegroundResolverTests.A_frame_host_window_is_unwrapped_to_the_core_window_owner`, `A_frame_host_window_without_a_core_window_stays_the_frame_host`. |
| B10 | Elevated game (relative to this process) detected and reported; an unreadable token is its own state. | 2, 5 | `ForegroundResolver.Resolve` (`Elevation`), `Win32WindowSystem.IsCurrentProcessElevated`. `ForegroundResolverTests.An_elevated_game_is_invisible_only_while_this_process_is_not_elevated`, `An_unreadable_game_token_is_its_own_state_and_does_not_give_focus`, `ForegroundTrackerTests.An_elevated_game_leaves_the_flag_down_and_is_reported`. The card is stage 5; one manual check with an elevated window is open (stage 2 ledger L31). |
| B11 | Mapped key from another keyboard produces a one-time notice; README states the limitation. | 2, 5, 6 | Stage 2: `RawKeyEvent.Device` and `RawInputPump.ContainerIdOf`. `RawInputRingTests.A_device_change_forgets_the_cached_container_id_and_a_failed_lookup_is_never_cached`. The notice and the README are stages 5 and 6. |

## Output and safety

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| O1 | ViGEm in-process on the engine thread with atomic submit. | 3 | |
| O2 | Connect sequence: prime with `LeftStickX = 1`, zero, loop until XInput reads all-zero, tolerate `UserIndexNotReported`, 2 s timeout. | 3 | |
| O3 | Packing: symmetric +/-32767 sticks, 0..255 triggers, non-finite to neutral. | 1 | `Engine/PadReport.cs`. `PadReportTests.Sticks_pack_symmetrically`, `Triggers_pack_to_a_byte`. |
| O4 | Zero before disconnect; disconnect idempotent. | 3 | |
| O5 | Process death unplugs the pad, proven by the kill test, or the contingency applies. | 0, 3 | |
| O6 | Watchdog on the hook timer: tick older than 200 ms claims pad ownership, zeros, unplugs, unhooks. | 3 | |
| O7 | Pad ownership token checked by the engine before every submit. | 3 | |
| O8 | Unhandled-exception handler zeros, unplugs, unhooks, restores GC mode. | 3 | |
| O9 | Driver exception on the engine thread stops the session with the reason shown. | 3, 5 | |
| O10 | Ctrl+Alt+F12 handled on the hook thread. | 2, 3 | `KeyboardHook.Handle` raises `StopRequested` on the hook thread; `HookPolicy.IsStopChord` (edge-triggered, left Alt only, never injected outside test mode). `KeyboardHookTests.The_stop_chord_raises_the_handler_and_is_swallowed_and_a_throwing_handler_is_counted`, `HookPolicyTests.The_stop_chord_is_ctrl_alt_f12_from_observed_modifiers`, `Only_ctrl_with_left_alt_makes_f12_the_stop_chord`. The session wiring is stage 3. |
| O11 | Driver check is present-and-running; missing shows version, browser button, re-check on focus; present-but-not-started shown distinctly; never downloads or runs the installer. | 3, 5 | |
| O12 | Controller disconnect detected on the hook timer's XInput check. | 3 | |

## Session

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| E1 | Start preconditions: keyboard, running game, driver running, all analog keys calibrated; connect pad, install hook, gate all. | 3 | |
| E2 | Foreground loss: pad zeroed, held keys handed back, gate set for return, session stays Running. | 3 | |
| E3 | Foreground regained: mapping resumes; held keys dead until released; status text says so. | 3, 5 | |
| E4 | Keyboard unplugged: Paused with hook kept and pad zeroed; auto-resume on reconnect with gate set. | 3 | |
| E5 | Stop on game exit, controller disconnect, resume from sleep, user Stop, hotkey, app close: zero, unplug, unhook, ungate. | 3 | |
| E6 | Game remembered by executable path; selected keyboard persisted. | 4 | |
| E7 | Updates never applied during a session. | 6 | |
| E8 | Editing the active profile stops the session. | 3, 5 | |
| E9 | Shutdown order and bounds as in the plan; Stop returns within bound with a blocked read or a backoff in progress. | 3 | |
| E10 | Rolling 1 MB log with one background writer; hot threads never write it; no key presses logged. | 4 | |

## Profile model

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| P1 | Profile is a name plus bindings; key binding to button or trigger; axis binding of two keys to a stick axis. | 1 | `Profiles/Profile.cs`, `Bindings/Binding.cs`. `ProfileJsonTests.Forza_round_trips`, `CompiledProfileTests.Forza_profile_is_valid`. |
| P2 | Response is exponent, saturation, deadzone, with presets linear, soft, aggressive. | 1 | `Response/Response.cs`, validated on load by `Binding.Validate`. `ResponseTests.Presets_are_monotone_from_zero_to_one`, `Presets_differ_at_mid_travel`, `BindingValidationTests.Key_binding_rules`, `ProfileJsonTests.Malformed_documents_are_refused_with_a_message_and_never_throw`. |
| P3 | Press and release ramps for digital keys and fallback. | 1 | `Bindings/Binding.cs` ramps, `Engine/Ramp.cs`, `Mapper.RampFor`. `RampTests.*`, `MapperTests.Gate_during_a_ramp_restarts_the_ramp_from_zero`; buttons ignore ramps: `MapperTests.Buttons_are_digital_and_ignore_ramp_and_response`. |
| P4 | Conflict rule last-input-wins (default) or neutral. | 1 | `Engine/Conflict.cs`. `ConflictTests.Last_input_wins_then_hands_over_on_release`, `Neutral_centres_while_both_are_held`. |
| P5 | Axis mode position or rate; Forza default chosen in the feel step. | 1, 5 | `Engine/RateState.cs`, `Mapper` axis loop. `RateStateTests.*`, `MapperTests.Rate_mode_integrates_steering`. Default chosen in stage 5. |
| P6 | Forza profile ships as default and reset. | 1, 4 | `Profiles/DefaultProfiles.cs`. `CompiledProfileTests.Forza_profile_is_valid`, `ProfileJsonTests.Forza_round_trips`. Reset is stage 4. |
| P7 | JSON with a version integer, atomic write, one `.bak`, `.corrupt` rename. | 1 | `Profiles/ProfileJson.cs`, `Storage/JsonDocuments.cs`, `Storage/JsonFile.cs`. `ProfileJsonTests.Newer_version_is_refused_and_older_is_read`, `JsonFileTests.Save_creates_the_file_without_a_bom_and_the_second_save_keeps_a_backup`, `Corrupt_primary_recovers_from_the_backup_and_is_kept_aside`, `Missing_primary_recovers_from_the_backup`, `A_locked_primary_is_unavailable_and_left_alone`. |
| P8 | Data folder `%AppData%\ApexAnalogMapper`; old folder never touched. | 4 | |

## Keyboards

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| H1 | 0x1610 and 0x1614 verified; the listed other product ids get try-it. | 1, 5 | `Sensors/KnownKeyboards.cs`. `SensorSnapshotTests.Known_keyboards_table_marks_only_gen_1_as_verified`. Try-it flow is stage 5. |
| H2 | Try-it sends 0x90 only first; non-version reply stops with export offered. | 5 | |
| H3 | Consent naming the risk before any 0xD7 on an unverified board. | 5 | |
| H4 | Replies all zero, all identical, or unchanged on press are rejected. | 1 | `SensorProtocol.IsPlausibleGroup`, `LearnStep.AnythingMoved`. `SensorProtocolTests.Plausibility_rejects_all_zero_and_all_identical_groups`, `LearnStepTests.Nothing_moving_enough_is_reported`. |
| H5 | Learn step finds the sensor index per prompted key and rejects ambiguity. | 1, 5 | `Sensors/LearnStep.cs`. `LearnStepTests.Finds_the_sensor_that_moved`, `Two_keys_pressed_is_ambiguous`, `Thresholds_are_exact_at_their_boundaries`. Prompting is stage 5. |
| H6 | Unverified banner; export JSON with firmware, product id, report lengths, container id, captures. | 1, 5 | `Sensors/CaptureExport.cs`. `LearnStepTests.Capture_export_round_trips`. Banner is stage 5. |
| H7 | Firmware verified list warns rather than rejects. | 1, 5 | `KnownKeyboards.IsVerified` returns a flag rather than refusing. `SensorSnapshotTests.Known_keyboards_table_marks_only_gen_1_as_verified`. The warning is stage 5. |

## Window

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| W1 | Status card: state, reason, Start and Stop, cycle p50 and p99, submit rate, warnings. | 5 | |
| W2 | Keyboard card: boards, firmware, verified or try-it. | 5 | |
| W3 | Game card: visible windows with executable, remembered game, elevated warning. | 5 | |
| W4 | Profile card: list, bindings editor with key capture and target, response picker with live preview. | 5 | |
| W5 | Calibration card: live raw bar per key, Set released, Set fully pressed, learn step, noise at rest. | 5 | |
| W6 | Setup card: driver state and browser button, Steam Input note, start-before-game note, update check with Update button. | 5, 6 | |
| W7 | Feel step recorded in `measurements.md`. | 5 | |

## Install, update, docs, CI

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| U1 | Update check on launch, cached, silent on failure, never blocks startup, prereleases off by default. | 6 | |
| U2 | Release workflow on tag: pack, `SHA256SUMS.txt`, upload, hashes in notes. | 6 | |
| U3 | README covers install and size, SmartScreen, driver link, Steam Input, start order, blocking limitation, alt-tab note, calibration, troubleshooting, build, never-inject stance. | 6 | |
| U4 | `docs/acceptance.md` in-game checklist. | 6 | |
| U5 | CI on `main`, `claude/**`, and pull requests: build, non-hardware tests, pack artifact. | 0 | |

## Measurements and tests

| ID | Requirement | Stage | Evidence |
| --- | --- | --- | --- |
| M1 | `measurements.md` records every stage 0 number and the five decisions. | 0 | |
| M2 | Fixtures from the real board under `docs/design/fixtures/`. | 0 | |
| M3 | `HardwareThresholds.cs` filled from measurements with headroom; stage 7 confirms agreement. | 3, 7 | |
| M4 | Loopback test asserts packet-number rate and p99 submit-to-readback. | 3 | |
| M5 | Kill test with a hook that swallows injected input in test mode and a real foreground window. | 3 | |
| M6 | Wedge test blocks inside a submit; watchdog claims ownership and unplugs; the wedged call never touches the driver again. | 3 | |
| M7 | Soak: working set, handles, keys at zero, GC pause budget, gen 2 count. | 3 | |
| M8 | Each stage's review findings recorded before fixes, with citations and test output. | all | `docs/design/reviews/stage-0.md`, `docs/design/reviews/stage-1.md` (stage 1 so far). |
