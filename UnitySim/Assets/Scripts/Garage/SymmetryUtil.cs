using UnityEngine;

namespace AIHWSim.Garage
{
    /// <summary>
    /// Mirror-symmetry helpers. Twins are linked by a shared positive
    /// <c>mirrorGroup</c> id (stable across list edits, unlike paired indices).
    /// Mirroring reflects a part across the vehicle's X=0 centreline: x is negated,
    /// wheel yaw and steer direction flip, and a sensor's aim yaw/roll flip while
    /// pitch is kept. Names and the mirrorGroup id are preserved on the twin.
    /// </summary>
    public static class SymmetryUtil
    {
        /// <summary>Parts within this distance of the centreline can't be mirrored.</summary>
        public const float CenterDeadzone = 0.01f;

        public static int NextGroupId(VehicleDesign d)
        {
            int max = 0;
            foreach (var w in d.wheels) if (w.mirrorGroup > max) max = w.mirrorGroup;
            foreach (var s in d.sensors) if (s.mirrorGroup > max) max = s.mirrorGroup;
            if (d.aero != null)
                foreach (var a in d.aero) if (a.mirrorGroup > max) max = a.mirrorGroup;
            if (d.antennas != null)
                foreach (var a in d.antennas) if (a.mirrorGroup > max) max = a.mirrorGroup;
            if (d.lights != null)
                foreach (var l in d.lights) if (l.mirrorGroup > max) max = l.mirrorGroup;
            return max + 1;
        }

        public static WheelSpec FindTwin(VehicleDesign d, WheelSpec w)
        {
            if (w == null || w.mirrorGroup < 0) return null;
            foreach (var o in d.wheels)
                if (!ReferenceEquals(o, w) && o.mirrorGroup == w.mirrorGroup) return o;
            return null;
        }

        public static SensorSpec FindTwin(VehicleDesign d, SensorSpec s)
        {
            if (s == null || s.mirrorGroup < 0) return null;
            foreach (var o in d.sensors)
                if (!ReferenceEquals(o, s) && o.mirrorGroup == s.mirrorGroup) return o;
            return null;
        }

        public static AeroSpec FindTwin(VehicleDesign d, AeroSpec a)
        {
            if (a == null || a.mirrorGroup < 0 || d.aero == null) return null;
            foreach (var o in d.aero)
                if (!ReferenceEquals(o, a) && o.mirrorGroup == a.mirrorGroup) return o;
            return null;
        }

        public static AntennaSpec FindTwin(VehicleDesign d, AntennaSpec a)
        {
            if (a == null || a.mirrorGroup < 0 || d.antennas == null) return null;
            foreach (var o in d.antennas)
                if (!ReferenceEquals(o, a) && o.mirrorGroup == a.mirrorGroup) return o;
            return null;
        }

        public static LightSpec FindTwin(VehicleDesign d, LightSpec l)
        {
            if (l == null || l.mirrorGroup < 0 || d.lights == null) return null;
            foreach (var o in d.lights)
                if (!ReferenceEquals(o, l) && o.mirrorGroup == l.mirrorGroup) return o;
            return null;
        }

        /// <summary>Copy all geometry/config from src into dst, mirrored. Keeps dst's name + group.</summary>
        public static void MirrorInto(WheelSpec src, WheelSpec dst)
        {
            dst.localPos = new Vector3(-src.localPos.x, src.localPos.y, src.localPos.z);
            dst.yaw = -src.yaw;
            dst.radius = src.radius;
            dst.allowsSteering = src.allowsSteering;
            dst.reverseSteering = !src.reverseSteering;
            dst.steerAngle = src.steerAngle;
            dst.powered = src.powered;
            dst.motor = src.motor;
            dst.motorDatasheet = src.motorDatasheet;
            dst.motorEntryMode = src.motorEntryMode;
            // Suspension: strut tilt is side-relative (CarVehicle mirrors the sign
            // by wheel x), so a plain copy IS the mirror. Other params are symmetric.
            dst.suspStiffness = src.suspStiffness;
            dst.suspDampingRatio = src.suspDampingRatio;
            dst.suspTravel = src.suspTravel;
            dst.suspAngleDeg = src.suspAngleDeg;
            dst.suspLength = src.suspLength;
            dst.gripMult = src.gripMult;
            dst.loadSensitivity = src.loadSensitivity;
            dst.balloonPct = src.balloonPct;
            dst.pressureKpa = src.pressureKpa;
            // Both halves of the style pair. Copying only the int would leave the
            // mirror's own key in place, and the key wins — so the left wheel
            // would quietly keep the style the right one just changed away from.
            dst.wheelStyle = src.wheelStyle;
            dst.wheelKey = src.wheelKey;
            dst.massKg = src.massKg;
        }

        public static void MirrorInto(SensorSpec src, SensorSpec dst)
        {
            dst.kind = src.kind;
            dst.localPos = new Vector3(-src.localPos.x, src.localPos.y, src.localPos.z);
            dst.aimEuler = new Vector3(src.aimEuler.x, -src.aimEuler.y, -src.aimEuler.z);
            dst.range = src.range;
            dst.coneRays = src.coneRays;
            dst.coneAngle = src.coneAngle;
            dst.wheelIndex = src.wheelIndex;
            dst.cprTicks = src.cprTicks;
            dst.encoderGearRatio = src.encoderGearRatio;
            dst.camWidth = src.camWidth;
            dst.camHeight = src.camHeight;
            dst.camFov = src.camFov;
            dst.camRateHz = src.camRateHz;
            dst.massKg = src.massKg;
            dst.noiseStd = src.noiseStd;
            dst.noiseQuant = src.noiseQuant;
            dst.driftRate = src.driftRate;
            dst.updateRateHz = src.updateRateHz;
            dst.latencyMs = src.latencyMs;
        }

        public static void MirrorInto(AeroSpec src, AeroSpec dst)
        {
            dst.kind = src.kind;
            dst.localPos = new Vector3(-src.localPos.x, src.localPos.y, src.localPos.z);
            dst.yawDeg = -src.yawDeg;
            dst.angleDeg = src.angleDeg;
            dst.sizeScale = src.sizeScale;
            dst.massKg = src.massKg;
        }

        public static void MirrorInto(AntennaSpec src, AntennaSpec dst)
        {
            dst.localPos = new Vector3(-src.localPos.x, src.localPos.y, src.localPos.z);
            dst.yawDeg = -src.yawDeg;
            dst.tiltDeg = src.tiltDeg;
            dst.antennaStyle = src.antennaStyle;
            dst.sizeScale = src.sizeScale;
            dst.massKg = src.massKg;
        }

        /// <summary>Push the edited part's state onto its linked twin (if any).</summary>
        public static void SyncTwin(VehicleDesign d, WheelSpec edited)
        {
            var twin = FindTwin(d, edited);
            if (twin != null) MirrorInto(edited, twin);
        }

        public static void SyncTwin(VehicleDesign d, SensorSpec edited)
        {
            var twin = FindTwin(d, edited);
            if (twin != null) MirrorInto(edited, twin);
        }

        public static void SyncTwin(VehicleDesign d, AeroSpec edited)
        {
            var twin = FindTwin(d, edited);
            if (twin != null) MirrorInto(edited, twin);
        }

        public static void SyncTwin(VehicleDesign d, AntennaSpec edited)
        {
            var twin = FindTwin(d, edited);
            if (twin != null) MirrorInto(edited, twin);
        }

        public static void MirrorInto(LightSpec src, LightSpec dst)
        {
            dst.localPos = new Vector3(-src.localPos.x, src.localPos.y, src.localPos.z);
            dst.yawDeg = -src.yawDeg;
            dst.style = src.style;
            dst.sizeScale = src.sizeScale;
            dst.massKg = src.massKg;
        }

        public static void SyncTwin(VehicleDesign d, LightSpec edited)
        {
            var twin = FindTwin(d, edited);
            if (twin != null) MirrorInto(edited, twin);
        }
    }
}
