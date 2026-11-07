using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TpmVr;

// Our own shaders (tools/ShaderBundle, built into lib/tpmvr-shaders with the
// game's Unity version and copied beside the plugin), loaded the first time
// one is asked for. Get returns null when the bundle is missing, won't load
// (built with another Unity version?) or the shader can't run here: callers
// fall back to the game's shaders.
static class ModShaders
{
    const string BundleFile = "tpmvr-shaders";
    static bool tried;
    static AssetBundle bundle;
    static readonly Dictionary<string, Shader> shaders = new();

    public static Shader Get(string name)
    {
        if (shaders.TryGetValue(name, out var cached)) return cached;
        Shader shader = null;
        if (Open())
        {
            shader = LoadAsset(bundle, name, Il2CppInterop.Runtime.Il2CppType.Of<Shader>())?.TryCast<Shader>();
            if (shader == null || !shader.isSupported)
            {
                Plugin.Logger.LogWarning($"[shaders] {name} {(shader == null ? "missing from the bundle" : "not supported here")}");
                shader = null;
            }
            else Plugin.Logger.LogInfo($"[shaders] loaded {shader.name}");
        }
        shaders[name] = shader;
        return shader;
    }

    static bool Open()
    {
        if (tried) return bundle != null;
        tried = true;
        var path = Path.Combine(Plugin.Instance.PluginDir, BundleFile);
        if (!File.Exists(path))
        {
            Plugin.Logger.LogWarning($"[shaders] {path} not found; using the game's shaders (rough diorama edge)");
            return false;
        }
        bundle = LoadBundle(path);
        if (bundle == null) Plugin.Logger.LogWarning($"[shaders] couldn't load {path} (built with a different Unity version?)");
        return bundle != null;
    }

    // Unity 6 passes strings to native code as a span over their characters;
    // the interop wrappers that build that span fail here (missing
    // ReadOnlySpan members / collected arrays), so build it ourselves and call
    // the native ("_Injected") functions directly. They return GC handles.
    static unsafe AssetBundle LoadBundle(string path)
    {
        IntPtr handle;
        fixed (char* chars = path)
        {
            var span = new UnityEngine.Bindings.ManagedSpanWrapper(chars, path.Length);
            handle = AssetBundle.LoadFromFile_Internal_Injected(ref span, 0, 0);
        }
        return handle == IntPtr.Zero ? null : UnityEngine.Bindings.Unmarshal.UnmarshalUnityObject<AssetBundle>(handle);
    }

    static unsafe Object LoadAsset(AssetBundle bundle, string name, Il2CppSystem.Type type)
    {
        IntPtr handle;
        fixed (char* chars = name)
        {
            var span = new UnityEngine.Bindings.ManagedSpanWrapper(chars, name.Length);
            handle = AssetBundle.LoadAsset_Internal_Injected(bundle.m_CachedPtr, ref span, type);
        }
        return handle == IntPtr.Zero ? null : UnityEngine.Bindings.Unmarshal.UnmarshalUnityObject<Object>(handle);
    }
}
