# UserScripts

Write the firmware that drives a car in the game, in C, without opening a terminal.

**Start here: open [`guide.html`](guide.html) in a browser.** It has pictures, and it is the
document this README exists to point at. The game can open it for you — Single Player →
Simulate Controller → *Open the guide*.

## The thirty-second version

```
UserScripts/
  lib/tt_controller.h     helper library, shared by every script below
  MyController/
    my_controller.c       edit this
```

One folder = one controller = one DLL, named after the folder. `MyController/` becomes
`MyController.dll` and appears in the game's picker under that name.

1. In the game: **Single Player → Simulate Controller → Build & Reload → Run controller ▶**.
2. Edit `MyController/my_controller.c` — the game can be running the whole time.
3. Press **Build & Reload** again (it is in the pause menu too). The car keeps driving; the
   code behind it changes.

To make a second controller, copy the folder and rename it. Letters, digits and underscores
only — the name has to work as both a build target and a file name. There is no list to add
yourself to and no build file to edit.

## What is not here

The build system, the ABI header and the game's own four controllers live in
[`../Controllers/`](../Controllers). You do not need to go in there to write a controller,
but `Controllers/hal/controller_api.h` is the real contract and it is worth reading once.

Built DLLs land in `UnitySim/Assets/Plugins/x86_64/` and are not committed — they are
rebuilt from source, by the button in the game or by `Controllers/build.ps1`.
