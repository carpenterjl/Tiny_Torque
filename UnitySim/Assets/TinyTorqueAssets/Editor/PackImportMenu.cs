using System.IO;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.Pack
{
    /// <summary>
    /// Step 1 of the pack pipeline: copy the shipping meshes out of the three
    /// <c>Resources/</c> folders into the pack's categorised tree.
    ///
    /// The pack is deliberately a COPY, not a set of references — it is meant to be
    /// a self-contained kit you could hand to someone. The cost of that choice is
    /// drift: a re-export into <c>Resources/</c> leaves the pack stale. <see
    /// cref="PackValidator"/> hashes both sides and names anything that has
    /// diverged, and re-running this menu item re-copies everything, so the fix is
    /// always one click.
    ///
    /// Nothing in the game references these copies. They get fresh GUIDs, they live
    /// outside any <c>Resources/</c> folder, and no scene in Build Settings touches
    /// them — so the pack contributes zero bytes to a player build.
    /// </summary>
    public static class PackImportMenu
    {
        [MenuItem("Tools/TinyTorque Assets/1. Copy source meshes", priority = 100)]
        public static void CopyFromResources()
        {
            int copied = 0, skipped = 0, failed = 0;
            PackPaths.EnsureLayout();

            try
            {
                AssetDatabase.StartAssetEditing();

                foreach (var e in PackPaths.All())
                {
                    if (string.IsNullOrEmpty(e.sourcePath)) continue;  // pack-native (arena)
                    PackPaths.EnsureFolder(Path.GetDirectoryName(e.modelPath).Replace('\\', '/'));

                    if (SameBytes(e.sourcePath, e.modelPath)) { skipped++; continue; }

                    if (File.Exists(PackPaths.ToAbsolute(e.modelPath)))
                        AssetDatabase.DeleteAsset(e.modelPath);

                    if (AssetDatabase.CopyAsset(e.sourcePath, e.modelPath)) copied++;
                    else { failed++; PackPaths.Warn($"copy failed: {e.sourcePath} -> {e.modelPath}"); }
                }

                // The four baked liveries ride along with the bodies they texture.
                foreach (string tex in PackPaths.LiveryTextures())
                {
                    string key = Path.GetFileNameWithoutExtension(tex);      // body_coupe_paint
                    string car = key.Replace("body_", "").Replace("_paint", "");
                    string cat = PackPaths.VehicleCategory("body_" + car);
                    string dir = PackPaths.ModelDirFor(PackKind.VehiclePart, cat);
                    PackPaths.EnsureFolder(dir);
                    string dst = dir + "/" + key + ".png";

                    if (SameBytes(tex, dst)) { skipped++; continue; }
                    if (File.Exists(PackPaths.ToAbsolute(dst))) AssetDatabase.DeleteAsset(dst);
                    if (AssetDatabase.CopyAsset(tex, dst)) copied++;
                    else { failed++; PackPaths.Warn($"copy failed: {tex} -> {dst}"); }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }

            PackPaths.Log($"COPY {copied} copied, {skipped} already current, {failed} failed");
            if (failed > 0)
                Debug.LogError(PackPaths.Tag + " copy step had failures — see warnings above.");
        }

        /// <summary>Byte comparison so a re-run is cheap and does not churn .meta
        /// GUIDs for assets that have not changed (which would silently break every
        /// prefab that already references them).</summary>
        private static bool SameBytes(string aProjectRel, string bProjectRel)
        {
            string a = PackPaths.ToAbsolute(aProjectRel), b = PackPaths.ToAbsolute(bProjectRel);
            if (!File.Exists(a) || !File.Exists(b)) return false;
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            return PackHash.Of(a) == PackHash.Of(b);
        }
    }

    /// <summary>Content hash shared by the copy step and the validator's drift
    /// check, so "unchanged" means the same thing in both places.</summary>
    public static class PackHash
    {
        public static string Of(string absolutePath)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            using (var s = File.OpenRead(absolutePath))
                return System.BitConverter.ToString(md5.ComputeHash(s));
        }
    }
}
