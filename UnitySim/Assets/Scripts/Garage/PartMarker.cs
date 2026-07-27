using UnityEngine;

namespace AIHWSim.Garage
{
    /// <summary>Which kind of design part a marker represents.</summary>
    public enum PartType { Wheel, Sensor, Aero, Battery, Antenna, Light }

    /// <summary>
    /// Tags a clickable marker in the garage preview with the design part it
    /// stands for, so a scene raycast can resolve a click back to a wheel or
    /// sensor index. Carries its renderer + base colour for selection highlight.
    /// </summary>
    public sealed class PartMarker : MonoBehaviour
    {
        public PartType type;
        public int index;
        public Renderer rend;
        public Renderer dir;          // the aim/heading line (toggleable)
        public Color baseColor = Color.white;
    }
}
