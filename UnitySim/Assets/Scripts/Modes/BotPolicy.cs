using AIHWSim.Core;
using UnityEngine;

namespace AIHWSim.Modes
{
    /// <summary>
    /// What a bot should be driving at, per mode.
    ///
    /// The decision lives here rather than in <see cref="BotDriver"/> for the
    /// reason the arcade layer's item policy does: a bot knows how to get to a
    /// point and nothing else, and the object that knows where the point IS —
    /// the mode's director — sits in a layer Core must not depend on. So the
    /// director calls <c>SetChaseTarget</c> and the driver stays ignorant.
    ///
    /// Called from each director's tick at a few hertz rather than every frame;
    /// the hold window on the seam covers the gaps.
    /// </summary>
    public static class BotPolicy
    {
        /// <summary>How often a bot re-picks what it is chasing. Cheap, but a
        /// per-frame re-target makes a bot jitter between two equal choices.</summary>
        public const float RepickSec = 0.4f;

        private static BotDriver DriverOf(MatchRacer r) =>
            r?.rig?.input?.source as BotDriver;

        // ---- demolition ------------------------------------------------------

        /// <summary>
        /// Hunt the weakest car in reach, preferring one that is already hurt —
        /// a derby is about finishing cars off, and a bot that always chases the
        /// nearest target ends up shoulder-to-shoulder with the healthiest one.
        /// </summary>
        public static void Derby(DerbyDirector dir, MatchRacer self)
        {
            var bot = DriverOf(self);
            if (bot == null || self.car == null) return;

            MatchRacer best = null;
            float bestScore = float.MaxValue;
            foreach (var other in dir.Racers)
            {
                if (other == self || !other.alive || other.car == null) continue;
                float d = Vector3.Distance(self.car.transform.position, other.car.transform.position);
                // Distance, discounted by how nearly dead they are.
                float score = d * (0.45f + other.Health01 * 0.55f);
                if (score >= bestScore) continue;
                bestScore = score; best = other;
            }

            if (best == null)
            {
                bot.ClearChaseTarget();
                return;
            }

            // Aim slightly PAST the target, so the hit lands square instead of
            // the bot arriving alongside and trading paint (which costs it too).
            Vector3 lead = best.car.transform.position
                         + best.car.transform.forward * 0.25f;
            bot.SetChaseTarget(lead, 1f, RepickSec * 2f);
        }

        // ---- capture the flag -------------------------------------------------

        /// <summary>
        /// Carrying → run home. Otherwise chase, in order: the enemy flag if it
        /// is takeable, our own flag if it is loose (returning it is worth more
        /// than a capture attempt), and the enemy carrier if there is one.
        /// </summary>
        public static void Ctf(CtfDirector dir, MatchRacer self, Flag own, Flag theirs, Vector3 homeBase)
        {
            var bot = DriverOf(self);
            if (bot == null || self.car == null) return;

            if (self.carrying >= 0) { bot.SetChaseTarget(homeBase, 1f, RepickSec * 2f); return; }

            if (own != null && own.state == Flag.State.Dropped)
            {
                bot.SetChaseTarget(own.transform.position, 1f, RepickSec * 2f);
                return;
            }
            if (theirs != null && theirs.state == Flag.State.Carried && theirs.carrier?.car != null)
            {
                bot.SetChaseTarget(theirs.carrier.car.transform.position, 1.1f, RepickSec * 2f);
                return;
            }
            if (theirs != null)
            {
                bot.SetChaseTarget(theirs.transform.position, 1f, RepickSec * 2f);
                return;
            }
            bot.ClearChaseTarget();
        }

        // ---- soccer -----------------------------------------------------------

        /// <summary>
        /// Drive at the point on the far side of the ball from the goal being
        /// attacked, which is the simplest target that produces a shot rather
        /// than a shove: get behind it, and the contact sends it forward.
        /// Falls back to defending when the ball is behind the car and near our
        /// own goal.
        /// </summary>
        public static void Soccer(SoccerDirector dir, MatchRacer self,
            Vector3 ballPos, Vector3 attackGoal, Vector3 defendGoal)
        {
            var bot = DriverOf(self);
            if (bot == null || self.car == null) return;

            Vector3 me = self.car.transform.position;
            Vector3 fromGoal = ballPos - attackGoal;
            fromGoal.y = 0f;
            if (fromGoal.sqrMagnitude < 1e-4f) fromGoal = Vector3.forward;
            Vector3 strikePoint = ballPos + fromGoal.normalized * 0.45f;

            // If the ball is between us and our own goal we are out of position:
            // go home first rather than chasing it into our own net.
            float ballToOwn = Vector3.Distance(ballPos, defendGoal);
            float meToOwn = Vector3.Distance(me, defendGoal);
            if (ballToOwn < meToOwn - 0.5f)
            {
                bot.SetChaseTarget(defendGoal, 1.1f, RepickSec * 2f);
                return;
            }

            bot.SetChaseTarget(strikePoint, 1f, RepickSec * 2f);

            // Boost when lined up and still far out; a bot that boosts into a
            // close ball just launches it at random.
            Vector3 toStrike = strikePoint - me;
            toStrike.y = 0f;
            bool lined = Vector3.Angle(self.car.transform.forward, toStrike) < 25f;
            bot.BoostRequested = lined && toStrike.magnitude > 2.5f;

            // And jump for a ball that is genuinely above the car.
            if (ballPos.y - me.y > 0.35f && toStrike.magnitude < 1.2f) bot.RequestJump();
        }
    }
}
