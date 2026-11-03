using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace VRCFTMapper.Editor
{
    /// <summary>
    /// プレイモード中のアバターへアニメーターパラメータを送る。
    /// Av3Emulator / Gesture Manager はパラメータを自前で管理して毎フレーム Animator に書き戻すので、
    /// それぞれの管理領域に値を入れる (Animator へ直接書いても上書きされるため)。どちらも無ければ Animator に直接書く。
    /// 両ツールへの依存はリフレクションのみ (未導入でもコンパイルできる)。
    /// </summary>
    public static class PlayModeBridge
    {
        public enum Backend { None, Av3Emulator, GestureManager, Animator }

        public class Target
        {
            public VRCAvatarDescriptor avatar;
            public Backend backend;
            public List<Component> av3Runtimes = new List<Component>();
            public object gmModule;
            public string Label => avatar == null ? "-" : $"{avatar.name} ({backend})";
        }

        static Type Find(string fullName) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullName)).FirstOrDefault(t => t != null);

        static Type av3Type, gmType;
        static bool typesResolved;

        static void Resolve()
        {
            if (typesResolved) return;
            av3Type = Find("Lyuma.Av3Emulator.Runtime.LyumaAv3Runtime");
            gmType = Find("BlackStartX.GestureManager.GestureManager");
            typesResolved = true;
        }

        /// <summary>プレイ中のシーンにいるアバターと、そのパラメータの管理方式を列挙。</summary>
        public static List<Target> FindTargets()
        {
            Resolve();
            var list = new List<Target>();
            if (!EditorApplication.isPlaying) return list;

            var gmModules = new List<object>();
            if (gmType != null)
                foreach (var gm in UnityEngine.Object.FindObjectsByType(gmType, FindObjectsSortMode.None))
                {
                    var module = gmType.GetField("Module", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(gm);
                    if (module != null) gmModules.Add(module);
                }

            foreach (var d in UnityEngine.Object.FindObjectsByType<VRCAvatarDescriptor>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                var t = new Target { avatar = d, backend = Backend.Animator };
                if (av3Type != null)
                {
                    // Av3Emulator は本体のほか、ミラー / シャドウ用のクローンにもランタイムを付ける
                    foreach (var rt in UnityEngine.Object.FindObjectsByType(av3Type, FindObjectsSortMode.None).Cast<Component>())
                        if (rt.gameObject == d.gameObject || rt.name.StartsWith(d.name)) t.av3Runtimes.Add(rt);
                    if (t.av3Runtimes.Count > 0) t.backend = Backend.Av3Emulator;
                }
                if (t.backend == Backend.Animator)
                    foreach (var mod in gmModules)
                    {
                        var go = mod.GetType().GetField("Avatar", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(mod) as GameObject;
                        if (go == d.gameObject) { t.gmModule = mod; t.backend = Backend.GestureManager; break; }
                    }
                list.Add(t);
            }
            return list;
        }

        /// <summary>パラメータに値を送る。パラメータ側の型 (Bool / Int / Float) に合わせて変換する。見つからなければ false。</summary>
        public static bool Set(Target t, string name, float value)
        {
            if (t == null || t.avatar == null) return false;
            switch (t.backend)
            {
                case Backend.Av3Emulator:
                    bool any = false;
                    foreach (var rt in t.av3Runtimes) if (rt != null) any |= SetAv3(rt, name, value);
                    return any;
                case Backend.GestureManager:
                    return SetGm(t.gmModule, name, value);
                default:
                    return SetAnimators(t.avatar.gameObject, name, value);
            }
        }

        static bool SetAv3(Component rt, string name, float value)
        {
            var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var listName in new[] { "Floats", "Ints", "Bools" })
            {
                if (!(rt.GetType().GetField(listName, bf)?.GetValue(rt) is System.Collections.IList list)) continue;
                foreach (var p in list)
                {
                    var pt = p.GetType();
                    if ((string)pt.GetField("name", bf)?.GetValue(p) != name) continue;
                    var vf = pt.GetField("value", bf);
                    if (listName == "Floats")
                    {
                        vf.SetValue(p, value);
                        pt.GetField("expressionValue", bf)?.SetValue(p, value); // メニュー由来の値として扱われる場合に備えて両方
                    }
                    else if (listName == "Ints") vf.SetValue(p, Mathf.RoundToInt(value));
                    else vf.SetValue(p, value > 0.5f);
                    return true;
                }
            }
            return false;
        }

        static bool SetGm(object module, string name, float value)
        {
            if (module == null) return false;
            var mt = module.GetType();
            var param = mt.GetMethod("GetParam", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string) }, null)
                ?.Invoke(module, new object[] { name });
            if (param == null) return false;
            var pt = param.GetType();
            var type = (AnimatorControllerParameterType)(pt.GetField("Type", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(param) ?? AnimatorControllerParameterType.Float);
            object arg; Type argType;
            switch (type)
            {
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger: arg = value > 0.5f; argType = typeof(bool); break;
                case AnimatorControllerParameterType.Int: arg = Mathf.RoundToInt(value); argType = typeof(int); break;
                default: arg = value; argType = typeof(float); break;
            }
            var set = pt.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Set" && m.GetParameters().Length == 3 && m.GetParameters()[1].ParameterType == argType);
            if (set == null) return false;
            set.Invoke(param, new[] { module, arg, null });
            return true;
        }

        static bool SetAnimators(GameObject root, string name, float value)
        {
            bool any = false;
            foreach (var a in root.GetComponentsInChildren<Animator>(true))
            {
                if (a.runtimeAnimatorController == null) continue;
                foreach (var p in a.parameters)
                {
                    if (p.name != name) continue;
                    switch (p.type)
                    {
                        case AnimatorControllerParameterType.Bool: a.SetBool(name, value > 0.5f); break;
                        case AnimatorControllerParameterType.Int: a.SetInteger(name, Mathf.RoundToInt(value)); break;
                        case AnimatorControllerParameterType.Float: a.SetFloat(name, value); break;
                    }
                    any = true;
                }
            }
            return any;
        }

        /// <summary>現在の値を読む (確認表示用)。読めなければ null。</summary>
        public static float? Get(Target t, string name)
        {
            if (t == null || t.avatar == null) return null;
            foreach (var a in t.avatar.GetComponentsInChildren<Animator>(true))
            {
                if (a.runtimeAnimatorController == null || !a.isInitialized) continue;
                foreach (var p in a.parameters)
                {
                    if (p.name != name) continue;
                    switch (p.type)
                    {
                        case AnimatorControllerParameterType.Bool: return a.GetBool(name) ? 1 : 0;
                        case AnimatorControllerParameterType.Int: return a.GetInteger(name);
                        case AnimatorControllerParameterType.Float: return a.GetFloat(name);
                    }
                }
            }
            return null;
        }
    }
}
