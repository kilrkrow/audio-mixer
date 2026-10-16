using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace audio_mixer
{
    /// <summary>
    /// NAudio's wrappers (MMDevice, MMDeviceEnumerator, AudioSessionControl, ...) Dispose() but never
    /// release the COM objects they wrap; the runtime only releases those RCWs when a GC happens to run.
    /// This tray app allocates very little, so GCs are rare (none in 90 s of idle measurement) while every
    /// poll leaves several live IMMDevice/session COM objects behind, each holding a registry key handle
    /// and native memory. ComRelease walks a wrapper's fields and releases the COM objects deterministically.
    /// Only call it on objects that are finished with (it makes the wrapped COM objects unusable).
    /// </summary>
    public static class ComRelease
    {
        private static readonly ConcurrentDictionary<Type, FieldInfo[]> FieldCache = new();

        /// <summary>Walks only NAudio's CoreAudioApi wrapper types.</summary>
        public static readonly Func<Type, bool> NAudioWrappers =
            t => t.Namespace != null && t.Namespace.StartsWith("NAudio.CoreAudioApi", StringComparison.Ordinal);

        /// <summary>
        /// Releases every COM object reachable from <paramref name="root"/> through instance fields of types
        /// accepted by <paramref name="walk"/> (default: NAudio wrappers). Returns how many were released.
        /// </summary>
        public static int Release(object? root, Func<Type, bool>? walk = null)
        {
            if (root == null) return 0;
            int released = 0;
            Visit(root, walk ?? NAudioWrappers, new HashSet<object>(ReferenceEqualityComparer.Instance), 0, ref released);
            return released;
        }

        private static void Visit(object node, Func<Type, bool> walk, HashSet<object> seen, int depth, ref int released)
        {
            if (depth > 10 || !seen.Add(node)) return;

            if (Marshal.IsComObject(node))
            {
                try
                {
                    Marshal.FinalReleaseComObject(node);
                    released++;
                }
                catch
                {
                    // Already released or not releasable; nothing more to do
                }
                return;
            }

            var type = node.GetType();
            if (!walk(type)) return;

            foreach (var field in GetFields(type))
            {
                object? value;
                try
                {
                    value = field.GetValue(node);
                }
                catch
                {
                    continue;
                }

                if (value == null || value is Delegate || value is string) continue;
                Visit(value, walk, seen, depth + 1, ref released);
            }
        }

        private static FieldInfo[] GetFields(Type type) => FieldCache.GetOrAdd(type, t =>
        {
            var fields = new List<FieldInfo>();
            for (Type? cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                foreach (var f in cur.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!f.FieldType.IsValueType) fields.Add(f);
                }
            }
            return fields.ToArray();
        });
    }

    /// <summary>
    /// Collects NAudio objects created during one audio call and releases their COM objects when the call ends.
    /// Declare it with <c>using var com = new ComScope();</c> before the objects it tracks so it is disposed last.
    /// </summary>
    public sealed class ComScope : IDisposable
    {
        private readonly List<object> _tracked = new();

        public T Track<T>(T item) where T : class
        {
            _tracked.Add(item);
            return item;
        }

        public void Dispose()
        {
            for (int i = _tracked.Count - 1; i >= 0; i--)
                ComRelease.Release(_tracked[i]);
            _tracked.Clear();
        }
    }
}