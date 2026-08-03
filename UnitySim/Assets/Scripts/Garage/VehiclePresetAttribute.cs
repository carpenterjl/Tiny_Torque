using UnityEngine;

namespace AIHWSim.Garage
{
    /// <summary>
    /// Marks a <c>string</c> field as the name of a <see cref="VehiclePresets"/> row,
    /// so the inspector draws it as a dropdown of the real cars instead of a text box.
    ///
    /// The string stays the persisted form — it is what <see cref="VehiclePresets.Resolve"/>
    /// takes and what a save file or a mission request already carries — but a typed
    /// name is a name that can be wrong, and wrong here is silent: the bootstrap
    /// resolves nothing and builds the stock car. The dropdown removes the only way
    /// to author that mistake.
    ///
    /// Same trade as <c>FloorTypeAttribute</c>: the value is unchanged, the way it is
    /// chosen is not. One drawer rather than a popup per inspector, so every field
    /// naming a car offers the same list and cannot drift as presets are appended.
    /// </summary>
    public sealed class VehiclePresetAttribute : PropertyAttribute
    {
        /// <summary>
        /// Whether empty is offered as a choice. True where an empty name means
        /// "no opinion, keep the caller's own default" — which is what
        /// <c>LevelSettings.defaultDesignName</c> means. False where a car must
        /// be named for the field to do anything at all.
        /// </summary>
        public readonly bool allowEmpty;

        /// <summary>What the empty entry is called, when it is offered.</summary>
        public readonly string emptyLabel;

        public VehiclePresetAttribute(bool allowEmpty = true, string emptyLabel = "(none)")
        {
            this.allowEmpty = allowEmpty;
            this.emptyLabel = emptyLabel;
        }
    }
}
