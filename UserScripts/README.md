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
    my_controller.c       edit this — a working controller with a speed loop
  user_sim_skeleton/
    user_sim_skeleton.c   or start here — plumbing only, one voltage to change
  camsnap/
    camsnap.c             saves a camera frame to disk, as a picture and as numbers
```

One folder = one controller = one DLL, named after the folder. `MyController/` becomes
`MyController.dll` and appears in the game's picker under that name.

1. In the game: **Single Player → Simulate Controller → Build & Reload → Run controller ▶**.
2. Edit `MyController/my_controller.c` — the game can be running the whole time.
3. Press **Build & Reload** again (it is in the pause menu too). The car keeps driving; the
   code behind it changes.

## Choosing the car

A controller can name the car it wants to be loaded into, with one optional
function:

```c
CTRL_EXPORT int ctrl_get_vehicle(void) { return CTRL_VEHICLE_TT_COUPE; }
```

The Simulate Controller screen then builds that car and ignores its own Vehicle
picker, saying so on the screen so it is not a mystery. `CTRL_VEHICLE_MENU` — and
leaving the function out entirely — means "whatever the menu picked". The list of
numbers is in `Controllers/hal/controller_api.h`: the stock chassis and the nine
built-in cars. A design you saved yourself has no number (it did not exist when
your DLL was compiled), and a car you have not unlocked is refused — pick those
in the menu.

## More controllers

To make a second controller, copy the folder and rename it. Letters, digits and underscores
only — the name has to work as both a build target and a file name. There is no list to add
yourself to and no build file to edit.

## What is not here

The build system, the ABI header and the game's own four controllers live in
[`../Controllers/`](../Controllers). You do not need to go in there to write a controller,
but `Controllers/hal/controller_api.h` is the real contract and it is worth reading once.

Built DLLs land in `UnitySim/Assets/Plugins/x86_64/` and are not committed — they are
rebuilt from source, by the button in the game or by `Controllers/build.ps1`.
