using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// One ordered checkpoint gate on a custom track. Attach to a trigger volume;
    /// reports crossings to the <see cref="LapTimer"/>, which enforces order.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public sealed class Checkpoint : MonoBehaviour
    {
        public int index;
        public LapTimer timer;

        private void Reset()
        {
            var col = GetComponent<Collider>();
            if (col != null) col.isTrigger = true;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (timer == null) return;
            var car = other.GetComponentInParent<CarVehicle>();
            if (car == null) return;
            timer.NotifyCheckpoint(car, index);
        }
    }
}
