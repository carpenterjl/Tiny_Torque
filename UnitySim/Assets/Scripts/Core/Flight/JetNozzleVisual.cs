using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core.Flight
{
    /// <summary>
    /// Turns a nozzle cylinder with the jet's actual nozzle state. Pure
    /// cosmetics: it READS <see cref="PlaneVehicle.NozzleDeg"/> and moves a
    /// collider-less primitive — the force was already applied by the physics.
    /// Its whole value is honesty of a different kind: the nozzle you can see is
    /// the nozzle the model is using, so a transition that looks half-finished
    /// IS half-finished.
    /// </summary>
    public sealed class JetNozzleVisual : MonoBehaviour
    {
        public PlaneVehicle plane;

        private void Update()
        {
            if (plane == null) return;
            // Cylinder axis is local Y; Euler(90,0,0) lays it along +Z (the aft
            // exhaust line), and the tilt swings it down with the thrust vector.
            transform.localRotation =
                Quaternion.AngleAxis(-plane.NozzleDeg, Vector3.right)
                * Quaternion.Euler(90f, 0f, 0f);
        }
    }
}
