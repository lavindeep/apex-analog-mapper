# Contributing

Apex Analog Mapper is a personal project by one maintainer. Bug reports, questions and
pull requests are welcome, and everything happens in public on GitHub: issues and pull
requests are the only channels. I do not answer email about the project.

## Issues

Open an issue for a bug, a question, or a feature idea. Pick the matching template
when there is one. A useful bug report has:

- The app version (from the About card or the release you installed).
- Your keyboard model and firmware version, and whether SteelSeries GG was running.
- The game and how it was launched (Steam, Store, Game Pass), and whether it runs as
  administrator.
- What you did, what you expected, and what happened instead.
- The session log, if the app produced one. It never records which keys were pressed.

Security problems go in a public issue too. There is no private channel and no bounty.

Feature ideas are welcome as issues. Some limits come from Windows rather than the
code: a low-level keyboard hook has no device identity, so mapped keys are blocked on
every keyboard, and an elevated game is invisible to a non-elevated hook.

## Pull requests

Small and focused wins. One fix or one feature per pull request, with a title in
conventional commit form (`fix(windows): ...`, `feat(core): ...`, `docs: ...`).

Before you open one:

1. For anything beyond a small fix, open an issue first so we can agree on the shape.
   That saves you from writing a change I will not merge.
2. No new package dependencies without asking in that issue.
3. Run the tests. The build treats analyzer warnings as errors, so a clean local build
   is the same bar as CI.

What to put in the description:

- What changed, in a few sentences.
- Why it should exist: the bug it fixes or the behaviour it adds, with the issue link.
- How you tested it. If it touches the hook, the sensor path, or the pad, say which
  keyboard and game you tried it with.
- Before and after screenshots for any change to the window.

CI runs on every push and an automated reviewer leaves comments. Read its findings and
either fix them or reply with why they do not apply; both are fine. I verify every
finding myself before merging.

What I am unlikely to merge: large rewrites, drive-by refactors, changes that widen
the scope of the app, or anything that adds a dependency or a background service. If
in doubt, ask in an issue first.

## Building and testing

You need the .NET 10 SDK on Windows.

```
dotnet test ApexAnalogMapper.slnx -c Release
```

Hardware tests need an Apex Pro plugged in and run only with `APEX_HW_TESTS=1`. Two
of them inject W keystrokes, which land in whatever window is in the foreground, so
focus an empty editor first:

```
$env:APEX_HW_TESTS = '1'
dotnet test ApexAnalogMapper.slnx -c Release
```

Tests are named as sentences (`A_key_up_is_swallowed_only_if_its_down_was`) and each
one checks one behaviour. Add a test for any behaviour you change; skip tests that
only prove a feature was deleted.

## Style

`.editorconfig` is the style guide and the compiler enforces it. Comments explain
why, not what, and go above the method or class rather than on every line. Keep
comments current when you change the code beneath them.

## Expectations

I review as time allows. A pull request can sit for a while, get closed, or be
reimplemented differently later. Opening one does not create an obligation on my side,
and I will say why when I close one.
