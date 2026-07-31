using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.Pack.Circuits
{
    /// <summary>
    /// Copies the Blender export into the pack.
    ///
    /// A plain file copy plus a Refresh, deliberately: the FBX files are the
    /// deliverable and the import settings are decided by
    /// <see cref="CircuitModelPostprocessor"/>, so there is nothing for this
    /// step to be clever about. It mirrors <c>PackImportMenu.CopyFromResources</c>
    /// and is re-runnable for the same reason — the pack's rule is that
    /// everything under it is generated and a rebuild overwrites it.
    ///
    /// The manifest is copied in as a <c>.json</c>, which Unity imports as a
    /// TextAsset; that is what lets a built scene be checked against the
    /// manifest it was built from without the Blender project being present.
    /// </summary>
    public static class CircuitImport
    {
        /// <summary>Circuit keys the export folder actually holds.</summary>
        public static List<string> Available()
        {
            var keys = new List<string>();
            string src = CircuitPaths.SourceDir;
            if (string.IsNullOrEmpty(src)) return keys;
            foreach (string d in Directory.GetDirectories(src))
            {
                string key = Path.GetFileName(d);
                if (key.StartsWith("_")) continue;      // _marker
                if (File.Exists(Path.Combine(d, key + ".circuit.json")))
                    keys.Add(key);
            }
            keys.Sort(string.CompareOrdinal);
            return keys;
        }

        /// <summary>Copy one circuit's FBX files and manifest into the pack.
        /// Returns the number of files written, or −1 if the source is missing.</summary>
        public static int CopyOne(string key)
        {
            string src = CircuitPaths.SourceDir;
            if (string.IsNullOrEmpty(src))
            {
                CircuitPaths.Err(CircuitPaths.SourceProblem);
                return -1;
            }
            string from = Path.Combine(src, key);
            if (!Directory.Exists(from))
            {
                CircuitPaths.Err("no export for '" + key + "' at " + from);
                return -1;
            }

            CircuitPaths.EnsureLayout(key);
            string to = PackPaths.ToAbsolute(CircuitPaths.DirFor(key));

            // Clear first. A re-export that renames or drops a mesh would
            // otherwise leave the old FBX behind, and the scene builder — which
            // only ever reads the manifest — would not mention it. A stale mesh
            // nobody placed is invisible until it turns up in a build.
            int removed = 0;
            foreach (string sub in new[] { "world", "split", "props" })
            {
                string d = Path.Combine(to, sub);
                if (!Directory.Exists(d)) continue;
                foreach (string f in Directory.GetFiles(d, "*.fbx"))
                {
                    File.Delete(f);
                    string meta = f + ".meta";
                    if (File.Exists(meta)) File.Delete(meta);
                    removed++;
                }
            }

            int n = 0;
            foreach (string sub in new[] { "world", "split", "props" })
            {
                string sd = Path.Combine(from, sub);
                if (!Directory.Exists(sd)) continue;
                Directory.CreateDirectory(Path.Combine(to, sub));
                foreach (string f in Directory.GetFiles(sd, "*.fbx"))
                {
                    File.Copy(f, Path.Combine(to, sub, Path.GetFileName(f)), true);
                    n++;
                }
            }
            string manifest = key + ".circuit.json";
            File.Copy(Path.Combine(from, manifest), Path.Combine(to, manifest), true);
            n++;

            CircuitPaths.Log(string.Format(
                "{0}: copied {1} file(s){2}", key, n,
                removed > 0 ? " (replaced " + removed + " stale FBX)" : ""));
            return n;
        }

        /// <summary>Copy the marker pair used by <see cref="CircuitAxisTest"/>.</summary>
        public static bool CopyMarker()
        {
            string src = CircuitPaths.SourceDir;
            if (string.IsNullOrEmpty(src)) return false;
            string from = Path.Combine(src, "_marker");
            if (!File.Exists(Path.Combine(from, "marker.json"))) return false;

            string rel = CircuitPaths.Root + "/_Marker";
            PackPaths.EnsureFolder(rel);
            string to = PackPaths.ToAbsolute(rel);
            foreach (string f in Directory.GetFiles(from))
                File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            return true;
        }

        /// <summary>The Mesh inside an imported FBX.
        ///
        /// Loaded from the asset rather than instantiated from the model prefab
        /// on purpose. Unity's model hierarchy is not something the exporter
        /// controls — a one-mesh FBX can arrive with the mesh on the root or on a
        /// child depending on the file — and every consumer here wants the mesh,
        /// not a hierarchy. Building the GameObject explicitly means the scene
        /// has exactly the components this code put on it.</summary>
        public static Mesh LoadMesh(string projectRelativeFbx)
        {
            var all = AssetDatabase.LoadAllAssetsAtPath(projectRelativeFbx);
            Mesh best = null;
            int extra = 0;
            foreach (var o in all)
            {
                if (!(o is Mesh m)) continue;
                if (best == null) best = m; else extra++;
            }
            if (best == null)
                CircuitPaths.Warn("no mesh in " + projectRelativeFbx);
            else if (extra > 0)
                CircuitPaths.Warn(projectRelativeFbx + ": " + (extra + 1)
                                  + " meshes, using '" + best.name
                                  + "' — the exporter writes one object per file, "
                                  + "so this file was not written by it");
            return best;
        }
    }
}
