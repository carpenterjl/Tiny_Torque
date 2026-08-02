namespace AIHWSim.Core.Flight
{
    /// <summary>
    /// Pilot input: four axes and two edges. Deliberately a SEPARATE seam from
    /// <see cref="IDriverInputSource"/> rather than an extension of it.
    ///
    /// <b>Why not just add pitch/roll/yaw to the driver interface.</b> It has six
    /// implementors, and one of them — <c>Net.NetworkInputSource</c> — feeds the
    /// versioned LAN wire. Adding three axes there would put aircraft controls in
    /// front of a protocol the aircraft must never reach, and would cost stubs on
    /// every bot and race-line follower besides. Two interfaces that share nothing
    /// is the honest description of two vehicles that share nothing.
    ///
    /// <b>Throttle is [0,1], not [-1,1].</b> An ESC-driven propeller has no
    /// reverse. The car's throttle is signed because a car does.
    /// </summary>
    public interface IPilotInputSource
    {
        /// <summary>[0,1]. Held, not sprung — see <see cref="PilotInputSource"/>.</summary>
        float Throttle();
        /// <summary>[-1,1], positive = roll right (stick right).</summary>
        float Roll();
        /// <summary>[-1,1], positive = nose up (stick back).</summary>
        float Pitch();
        /// <summary>[-1,1], positive = nose right (right rudder).</summary>
        float Yaw();

        /// <summary>Edge: put the aircraft back on the strip.</summary>
        bool ResetPressed();
        /// <summary>Edge: cycle ground station / chase / boresight.</summary>
        bool ViewTogglePressed();

        /// <summary>[0,1] nozzle tilt target for a vectored-thrust aircraft:
        /// 0 = full aft, 1 = full down. A DEFAULT member rather than an abstract
        /// one, deliberately: only the human source has nozzles to command, and a
        /// default keeps <c>ScriptedPilot</c> — and therefore every scripted
        /// flight test — at a zero-line diff. A propeller aircraft never reads
        /// the slot it feeds.</summary>
        float NozzleTarget() => 0f;
    }
}
