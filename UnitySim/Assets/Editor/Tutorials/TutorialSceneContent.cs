using System.Collections.Generic;
using AIHWSim.Tutorial;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// The written lessons, as data the scene builder turns into
    /// <see cref="TutorialStep"/> objects.
    ///
    /// It lives on the EDITOR side because it is only ever read once — when a
    /// scene that does not exist yet is created. After that the scene file is
    /// the truth and this is history: hand-editing the steps in the inspector is
    /// the expected workflow, and nothing here will overwrite that (see
    /// <see cref="TutorialSceneBuilder"/>'s create-if-missing rule).
    ///
    /// Text is written to be read once, at speed, by somebody who wants to be
    /// driving. Short sentences, one idea per step, and the control names left as
    /// {placeholders} so they come out right on whatever the player is holding
    /// and whatever they have rebound (see <c>TutorialText</c>).
    /// </summary>
    internal static class TutorialSceneContent
    {
        /// <summary>One authored step, plus where to put its trigger volume.</summary>
        internal struct Spec
        {
            public string title;
            public string body;
            public string banner;
            public TutorialCondition condition;
            public TutorialInput input;
            public float amount;
            public float seconds;
            public string token;

            /// <summary>Trigger centre for a TriggerVolume step, in world space.
            /// The builder makes the volume and wires it up.</summary>
            public Vector3 triggerAt;
            public Vector3 triggerSize;
        }

        private static Spec Say(string title, string body, float seconds = 0f,
                                string banner = "") => new Spec
                                {
                                    title = title,
                                    body = body,
                                    banner = banner,
                                    // A timer for the short ones, a button for
                                    // anything long enough that a fast reader
                                    // would resent waiting and a slow one would
                                    // miss it.
                                    condition = seconds > 0f
                                        ? TutorialCondition.Timer
                                        : TutorialCondition.Continue,
                                    seconds = seconds > 0f ? seconds : 1f,
                                };

        private static Spec Hold(string title, string body, TutorialInput input,
                                 float amount = 0.4f, float seconds = 1.2f,
                                 string banner = "") => new Spec
                                 {
                                     title = title,
                                     body = body,
                                     banner = banner,
                                     condition = TutorialCondition.InputHeld,
                                     input = input,
                                     amount = amount,
                                     seconds = seconds,
                                 };

        private static Spec Drive(string title, string body, Vector3 at,
                                  string banner = "", float sx = 9f, float sz = 3f) => new Spec
                                  {
                                      title = title,
                                      body = body,
                                      banner = banner,
                                      condition = TutorialCondition.TriggerVolume,
                                      triggerAt = at,
                                      triggerSize = new Vector3(sx, 3f, sz),
                                      seconds = 0.4f,
                                  };

        private static Spec Speed(string title, string body, float mps,
                                  string banner = "") => new Spec
                                  {
                                      title = title,
                                      body = body,
                                      banner = banner,
                                      condition = TutorialCondition.SpeedReached,
                                      amount = mps,
                                      seconds = 0.5f,
                                  };

        private static Spec Wait(string title, string body, TutorialCondition condition,
                                 string token = "", string banner = "") => new Spec
                                 {
                                     title = title,
                                     body = body,
                                     banner = banner,
                                     condition = condition,
                                     token = token,
                                     seconds = 0.5f,
                                 };

        /// <summary>The lesson for a tutorial id, or an empty list — the builder
        /// falls back to a worked placeholder so a new catalogue row still makes
        /// a scene you can open.</summary>
        internal static List<Spec> For(string id) => id switch
        {
            "single_player" => SinglePlayer(),
            "arcade" => Arcade(),
            "sim_controllers" => SimControllers(),
            "sim_sensors" => SimSensors(),
            "sim_firmware" => SimFirmware(),
            "sim_ipc" => SimIpc(),
            "mode_race" => ModeRace(),
            "mode_derby" => ModeDerby(),
            "mode_ctf" => ModeCtf(),
            "mode_soccer" => ModeSoccer(),
            _ => new List<Spec>(),
        };

        // ---- the lessons -------------------------------------------------------

        private static List<Spec> SinglePlayer() => new List<Spec>
        {
            Say("Welcome to Tiny Torque",
                "You are driving a small radio-controlled car, simulated properly — " +
                "it has a real motor, a real battery and real tyres, and it will " +
                "behave like it.\n\nNothing here can be failed. Skip out any time " +
                "from the pause menu."),
            Hold("Pull away",
                 "Hold {throttle}. The car takes a moment to move: a motor has to " +
                 "spin up before it can push anything.",
                 TutorialInput.Throttle, banner: "Rolling"),
            Drive("Follow the markers",
                  "Steer with {steer} and drive through the gate ahead.",
                  new Vector3(0f, 1.2f, 6f), banner: "Nice line"),
            Drive("Round the corner",
                  "Slow down before you turn, not during. A car that is still " +
                  "braking mid-corner is a car that understeers into the wall.",
                  new Vector3(14f, 1.2f, 14f), banner: "Through", sx: 3f, sz: 9f),
            Hold("Stop it",
                 "Hold {brake}. Braking is not reverse — the car stops, then " +
                 "backs up if you keep holding.",
                 TutorialInput.Brake, seconds: 1f, banner: "Stopped"),
            Say("If you get stuck",
                "Press {respawn} and the car is put back on the track near where " +
                "it was. Nobody is timing you.", seconds: 6f),
            Say("That's the driving",
                "Races, arenas and free roam all use the same car and the same " +
                "controls. Everything else is rules on top.", seconds: 6f,
                banner: "Lesson complete"),
        };

        private static List<Spec> Arcade() => new List<Spec>
        {
            Say("Arcade mode",
                "Same car, looser rules. Grip is more forgiving, you can carry " +
                "items, and sliding is something you are rewarded for rather than " +
                "punished for."),
            Speed("Get some speed up",
                  "Get moving — about jogging pace will do.", 4f, banner: "Good"),
            Hold("Break traction",
                 "Hold {handbrake} while turning. The back steps out; hold the " +
                 "slide and you build boost for as long as you keep it.",
                 TutorialInput.Handbrake, seconds: 0.8f, banner: "Drifting"),
            Drive("Grab an item",
                  "Drive through the box. What you get is random, and what you " +
                  "get is partly decided by how badly you are doing.",
                  new Vector3(0f, 1.2f, 10f), banner: "Got one"),
            Say("Use it",
                "Press {item} to fire whatever you picked up. Some things go " +
                "forwards, some go behind you, and some just make you faster.",
                seconds: 6f),
            Say("That's arcade",
                "Turn it on per race in the Single Player setup. It changes the " +
                "handling model too, so a lap time here means nothing next door.",
                seconds: 6f, banner: "Lesson complete"),
        };

        private static List<Spec> SimControllers() => new List<Spec>
        {
            Say("The other half of this game",
                "Underneath the racing is a test bench. The car is a real vehicle " +
                "model: a brushed motor with a stall torque and a free speed, a " +
                "battery that sags, tyres with a slip curve, and a chassis with " +
                "measured drag.\n\nThat means something you write can drive it, " +
                "and the result means something."),
            Hold("Drive it yourself first",
                 "Hold {throttle} and feel how it picks up. That lag is the motor " +
                 "and the mass, not input delay — a controller has to deal with " +
                 "the same thing.",
                 TutorialInput.Throttle, banner: "Feel that?"),
            Speed("Watch the numbers",
                  "Get up to speed and look at the telemetry on screen. Everything " +
                  "the car knows about itself is a named channel, sampled every " +
                  "control step and logged if you ask for it.", 5f),
            Say("Who is driving",
                "Every car has one driver: you, the built-in bot, a firmware DLL, " +
                "or an external app. They all write to the same actuator vector — " +
                "motor volts, steering, brake — so nothing gets an advantage from " +
                "being the one in charge.", seconds: 8f),
            Say("Where to go next",
                "Single Player ▸ Simulate Controller is where you point the game " +
                "at code and watch it drive. The lessons after this one cover the " +
                "sensors, writing the firmware, and driving from another program.",
                seconds: 8f, banner: "Lesson complete"),
        };

        private static List<Spec> SimSensors() => new List<Spec>
        {
            Say("What the car can see",
                "A controller only knows what its sensors tell it. This car can " +
                "carry distance sensors, wheel encoders, an IMU, a line sensor and " +
                "a small camera — the same parts you would bolt to a real one."),
            Say("Distance sensors",
                "A time-of-flight sensor reports metres to the first thing in " +
                "front of it, and nothing about what that thing is. Out of range " +
                "reads as its maximum, which is not the same as 'clear' — that " +
                "distinction has ended a lot of real robots.", seconds: 9f),
            Drive("Watch one work",
                  "Drive up to the wall ahead and stop close to it. Watch the " +
                  "front distance channel fall as you approach.",
                  new Vector3(0f, 1.2f, 12f), banner: "That's the reading"),
            Say("Encoders and the IMU",
                "Encoders count wheel rotations — they tell you how far the WHEELS " +
                "went, which is only how far the CAR went while the tyres are " +
                "gripping. The IMU gives you rotation rate and acceleration, and " +
                "it drifts. Together they are better than either alone.",
                seconds: 10f),
            Say("The camera",
                "A small greyscale image, a few times a second. Deliberately " +
                "small: it is there for line-following and gates, not for looking " +
                "at.", seconds: 7f),
            Say("Every channel is logged",
                "Turn on telemetry logging in Settings and you get a CSV of every " +
                "channel at the control rate — which is how you find out why a " +
                "controller did something odd on lap three.", seconds: 8f,
                banner: "Lesson complete"),
        };

        private static List<Spec> SimFirmware() => new List<Spec>
        {
            Say("Your code, driving this car",
                "The car can be driven by a C function you write. It gets the " +
                "sensor readings and sets the actuators, once per control step — " +
                "the same shape as firmware on a real board, because that is what " +
                "it is meant to become."),
            Say("Where it lives",
                "UserScripts/ in the project folder. One folder per controller, " +
                "with your .c files in it. There is a guide in there that spells " +
                "out the three functions the game calls.", seconds: 8f),
            Say("Building it",
                "The game builds it for you — there is a button on the Simulate " +
                "Controller screen, and another in the pause menu so you can " +
                "rebuild and hot-swap without leaving the drive.", seconds: 8f),
            Say("Start from the numbers",
                "Tools/index.html has a Control Loop Lab that derives this car's " +
                "plant constants and generates a controller with the gains already " +
                "filled in. Starting from a car that drives beats starting from a " +
                "blank file.", seconds: 9f),
            Hold("Meanwhile, drive it yourself",
                 "Hold {throttle} once more. Whatever you write is going to have " +
                 "to do this.",
                 TutorialInput.Throttle, banner: "Lesson complete"),
        };

        private static List<Spec> SimIpc() => new List<Spec>
        {
            Say("Driving from another program",
                "A separate application on this machine can take a car over, read " +
                "its sensors and change its settings, live. It talks to the game " +
                "over a named pipe.\n\nIt is off until you turn it on."),
            Say("Turn on the bridge",
                "Options ▸ Remote Control, in the main menu or the pause menu " +
                "here. The toggle takes effect immediately — no restart.",
                seconds: 8f),
            Wait("Connect your app",
                 "Start your control app now. When it connects and says hello, " +
                 "this step will finish on its own.\n\nNo app yet? " +
                 "Tools/ipc-test-client.ps1 is a working one in PowerShell, and " +
                 "Docs/ipc-protocol.md is the spec to write your own against.",
                 TutorialCondition.IpcConnected, banner: "Connected"),
            Say("What it can do",
                "Acquire a car and drive it — either normalized pedals with the " +
                "assists on, or the raw actuator vector a firmware would write. " +
                "Subscribe to telemetry channels at whatever rate you want. Stream " +
                "the camera. Load tracks, spawn cars, change tuning.", seconds: 10f),
            Say("The safety rule",
                "A car under external control that stops hearing from its app " +
                "brakes itself after half a second. A client that crashes " +
                "mid-corner hands the car back rather than leaving it pinned at " +
                "full throttle.", seconds: 9f, banner: "Lesson complete"),
        };

        private static List<Spec> ModeRace() => new List<Spec>
        {
            Say("Racing",
                "Laps around a circuit against bots. First to the flag wins; " +
                "everything else is detail."),
            Drive("Cross the line",
                  "Drive through the start line. Your lap timer starts here and " +
                  "stops here.",
                  new Vector3(0f, 1.2f, 6f), banner: "Lap started"),
            Drive("Hit every checkpoint",
                  "Checkpoints have to be taken in order. Cutting the course skips " +
                  "one, and a lap missing a checkpoint does not count.",
                  new Vector3(16f, 1.2f, 12f), banner: "Checkpoint", sx: 3f, sz: 9f),
            Say("Track limits",
                "With limits on, cutting a corner gives back the time you gained. " +
                "It is a setup option, not a rule of the game.", seconds: 7f),
            Say("The opposition",
                "Bots come in three difficulties and follow a racing line the " +
                "track carries. Rubber-banding is optional and off by default — " +
                "leave it off if you want an honest result.", seconds: 8f,
                banner: "Lesson complete"),
        };

        private static List<Spec> ModeDerby() => new List<Spec>
        {
            Say("Demolition derby",
                "A walled arena, no laps, and nowhere to be. Last car still " +
                "running wins."),
            Speed("Build up a hit",
                  "Damage comes from closing speed, so a hit is worth what you " +
                  "brought to it. Get some speed up.", 5f, banner: "That'll do it"),
            Drive("Ram the target",
                  "Drive hard into the block ahead. Watch your own health as well " +
                  "as theirs — a head-on costs you nearly as much as them.",
                  new Vector3(0f, 1.2f, 9f), banner: "Contact"),
            Say("Where to hit",
                "The side and the back of a car are worth more than the nose. " +
                "Getting a car sideways and then pushing it into a wall is the " +
                "whole game.", seconds: 8f),
            Say("Your car remembers",
                "Damage shows: panels dent where they were hit, and enough of it " +
                "breaks pieces off. It is the same body model you build in the " +
                "Vehicle Studio.", seconds: 8f, banner: "Lesson complete"),
        };

        private static List<Spec> ModeCtf() => new List<Spec>
        {
            Say("Capture the flag",
                "Two teams, two flags, two bases. Take theirs, get it back to " +
                "yours, repeat until somebody hits the score."),
            Drive("Pick up the flag",
                  "Drive over the flag ahead to carry it. You cannot drop it on " +
                  "purpose — being hit is what drops it.",
                  new Vector3(0f, 1.2f, 8f), banner: "Flag taken"),
            Drive("Take it home",
                  "Get it back to your base. Carrying slows you down, which is " +
                  "the whole reason anyone ever catches a carrier.",
                  new Vector3(0f, 1.2f, -14f), banner: "Captured"),
            Say("Getting hit",
                "A hard enough hit drops the flag where you were. It sits there " +
                "for anyone — including whoever just hit you.", seconds: 7f),
            Say("It takes two jobs",
                "Somebody has to run and somebody has to stop their runner. A team " +
                "where everyone runs loses to a team where one person waits.",
                seconds: 8f, banner: "Lesson complete"),
        };

        private static List<Spec> ModeSoccer() => new List<Spec>
        {
            Say("Soccer",
                "Two teams, one ball, two goals. The ball is much heavier than you " +
                "are, and that is the entire difficulty."),
            Drive("Reach the ball",
                  "Get to the ball. You will not move it much by arriving slowly.",
                  new Vector3(0f, 1.2f, 8f), banner: "On the ball"),
            Speed("Hit it properly",
                  "Back off, build speed, and drive through it. Momentum is all " +
                  "you have — there is no kick button.", 6f, banner: "That moved it"),
            Say("Going up",
                "Press {jump} to hop, and {boost} to burn the tank. Both exist for " +
                "the ball in the air, which is where most of the goals are.",
                seconds: 8f),
            Say("Play the space",
                "Chasing the ball with everyone else is how a team loses. Somebody " +
                "sitting where the ball is about to be beats four cars where it " +
                "is.", seconds: 8f, banner: "Lesson complete"),
        };
    }
}
