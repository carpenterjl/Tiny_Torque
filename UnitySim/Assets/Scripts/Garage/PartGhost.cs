using AIHWSim.Bridge;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Garage
{
    /// <summary>
    /// A translucent stand-in that follows the cursor while placing or moving a
    /// part. Built from the same <see cref="PartVisualFactory"/> geometry as the
    /// real part (so what you drag is what you get), tinted green/red to show
    /// whether the current drop point is valid. No colliders, on the Ignore-Raycast
    /// layer, so the placement raycast passes straight through it to the body.
    /// </summary>
    public sealed class PartGhost
    {
        public GameObject Root { get; private set; }
        public float Yaw;                 // heading being edited during the drag

        private Material _mat;

        private static readonly Color ValidTint   = new Color(0.40f, 1.00f, 0.55f, 0.45f);
        private static readonly Color InvalidTint = new Color(1.00f, 0.40f, 0.40f, 0.40f);

        public static PartGhost ForWheel(float radius, bool powered, float yaw)
        {
            var g = new PartGhost { Yaw = yaw };
            g.Root = new GameObject("wheel_ghost");
            g.Root.layer = PartVisualFactory.VizLayer;
            var holder = new GameObject("viz").transform;
            holder.SetParent(g.Root.transform, false);
            PartVisualFactory.BuildWheelViz(holder, radius, powered, -1f);
            g.Finish();
            return g;
        }

        public static PartGhost ForAero(AeroKind kind, float angleDeg, float sizeScale, float yaw)
        {
            var g = new PartGhost { Yaw = yaw };
            g.Root = new GameObject("aero_ghost");
            g.Root.layer = PartVisualFactory.VizLayer;
            PartVisualFactory.BuildAeroViz(g.Root.transform, kind, angleDeg, sizeScale);
            g.Finish();
            return g;
        }

        public static PartGhost ForAntenna(float tiltDeg, float sizeScale, float yaw, int style = 0)
        {
            var g = new PartGhost { Yaw = yaw };
            g.Root = new GameObject("antenna_ghost");
            g.Root.layer = PartVisualFactory.VizLayer;
            PartVisualFactory.BuildAntennaViz(g.Root.transform, tiltDeg, sizeScale, style);
            g.Finish();
            return g;
        }

        public static PartGhost ForLight(int style, float sizeScale, float yaw)
        {
            var g = new PartGhost { Yaw = yaw };
            g.Root = new GameObject("light_ghost");
            g.Root.layer = PartVisualFactory.VizLayer;
            PartVisualFactory.BuildLightViz(g.Root.transform, style, sizeScale);
            g.Finish();
            return g;
        }

        public static PartGhost ForBattery()
        {
            var g = new PartGhost { Yaw = 0f };
            g.Root = new GameObject("battery_ghost");
            g.Root.layer = PartVisualFactory.VizLayer;
            PartVisualFactory.BuildBatteryViz(g.Root.transform);
            g.Finish();
            return g;
        }

        public static PartGhost ForSensor(SensorType kind, float yaw)
        {
            var g = new PartGhost { Yaw = yaw };
            g.Root = new GameObject("sensor_ghost");
            g.Root.layer = PartVisualFactory.VizLayer;
            switch (kind)
            {
                case SensorType.Camera:     PartVisualFactory.BuildCameraViz(g.Root.transform); break;
                case SensorType.Encoder:    PartVisualFactory.BuildEncoderViz(g.Root.transform); break;
                case SensorType.Suspension: PartVisualFactory.BuildSuspensionViz(g.Root.transform); break;
                default:                    PartVisualFactory.BuildTofViz(g.Root.transform); break;
            }
            g.Finish();
            return g;
        }

        private void Finish()
        {
            _mat = PartVisualFactory.MakeGhostMat(ValidTint);
            PartVisualFactory.ApplyMaterial(Root, _mat);
        }

        public void SetPose(Vector3 worldPos, Quaternion worldRot)
        {
            if (Root != null) Root.transform.SetPositionAndRotation(worldPos, worldRot);
        }

        public void SetValid(bool ok)
        {
            if (_mat != null) _mat.color = ok ? ValidTint : InvalidTint;
        }

        public void Destroy()
        {
            if (Root != null) Object.Destroy(Root);
            if (_mat != null) Object.Destroy(_mat);
            Root = null;
        }
    }
}
