using System;
using System.Collections.Generic;
using System.Linq;
using VRC.SDK3.Avatars.Components;

namespace VRCFTMapper.Editor
{
    /// <summary>
    /// ネイティブ Face Tracking の Tracking Key (Unified Expressions 102 個) と、
    /// VRCFT v2 の簡易パラメータを Tracking Key の式で表した対応表。
    /// 簡易パラメータの式は VRCFT の定義に沿った近似 (一部は平均で代用)。
    /// </summary>
    public static class NativeKeys
    {
        public static readonly string[] All = Enum.GetNames(typeof(VRCAvatarDescriptor.FaceTrackingKey))
            .Where(n => n != "Max").ToArray();
        public static int Count => All.Length;
        static readonly Dictionary<string, int> index = All.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);

        public static bool IsKey(string name) => name != null && index.ContainsKey(name);
        public static int IndexOf(string name) => name != null && index.TryGetValue(name, out var i) ? i : -1;

        static string Avg(params string[] k) => "(" + string.Join(" + ", k) + ") * " + Fmt(1f / k.Length);
        static string LR(string b) => Avg(b + "Left", b + "Right");
        public static string Fmt(float v) => Math.Round(v, 5).ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>簡易パラメータ名 → Tracking Key の式。Key 名そのものも受け付ける。</summary>
        static readonly Dictionary<string, string> v2 = new Dictionary<string, string>();

        static void D(string name, string expr) => v2[name] = expr;

        static void Side(string name, Func<string, string> f)
        {
            D(name + "Left", f("Left"));
            D(name + "Right", f("Right"));
        }

        static NativeKeys()
        {
            // ---- 目 ----  EyeLid: 0 閉 / 0.75 通常 / 1 見開き
            Side("EyeLid", s => $"(1 - EyeClosed{s}) * 0.75 + EyeWide{s} * 0.25");
            D("EyeLid", "(2 - EyeClosedLeft - EyeClosedRight) * 0.375 + (EyeWideLeft + EyeWideRight) * 0.125");
            D("EyeSquint", LR("EyeSquint"));
            Side("EyeX", s => s == "Left" ? "EyeLookInLeft - EyeLookOutLeft" : "EyeLookOutRight - EyeLookInRight");
            v2["EyeLeftX"] = v2["EyeXLeft"]; v2["EyeRightX"] = v2["EyeXRight"];
            v2.Remove("EyeXLeft"); v2.Remove("EyeXRight");
            D("EyeLeftY", "EyeLookUpLeft - EyeLookDownLeft");
            D("EyeRightY", "EyeLookUpRight - EyeLookDownRight");
            D("EyeX", "(EyeLookInLeft - EyeLookOutLeft + EyeLookOutRight - EyeLookInRight) * 0.5");
            D("EyeY", "(EyeLookUpLeft - EyeLookDownLeft + EyeLookUpRight - EyeLookDownRight) * 0.5");
            D("PupilDilation", "(EyeDilationLeft + EyeDilationRight - EyeConstrictLeft - EyeConstrictRight) * 0.25 + 0.5");

            // ---- 眉 ----
            Side("BrowDown", s => $"(BrowLowerer{s} + BrowPinch{s}) * 0.5");
            Side("BrowExpression", s => $"(BrowInnerUp{s} + BrowOuterUp{s}) * 0.5 - (BrowLowerer{s} + BrowPinch{s}) * 0.5");
            D("BrowExpression", "(BrowInnerUpLeft + BrowOuterUpLeft + BrowInnerUpRight + BrowOuterUpRight - BrowLowererLeft - BrowPinchLeft - BrowLowererRight - BrowPinchRight) * 0.25");
            D("BrowInnerUp", LR("BrowInnerUp"));
            D("BrowOuterUp", LR("BrowOuterUp"));
            D("BrowUp", Avg("BrowInnerUpLeft", "BrowInnerUpRight", "BrowOuterUpLeft", "BrowOuterUpRight"));

            // ---- 鼻・頬 ----
            D("NoseSneer", LR("NoseSneer"));
            D("CheekSquint", LR("CheekSquint"));
            Side("CheekPuffSuck", s => $"CheekPuff{s} - CheekSuck{s}");
            D("CheekPuffSuck", "(CheekPuffLeft + CheekPuffRight - CheekSuckLeft - CheekSuckRight) * 0.5");

            // ---- 顎 ----
            D("JawX", "JawRight - JawLeft");
            D("JawZ", "JawForward - JawBackward");
            D("JawClench1", "JawClench");
            D("JawMandibleRaise1", "JawMandibleRaise");

            // ---- 唇 ----
            foreach (var p in new[] { "LipSuckUpper", "LipSuckLower", "LipFunnelUpper", "LipFunnelLower", "LipPuckerUpper", "LipPuckerLower" })
                D(p, LR(p));
            D("LipSuck", Avg("LipSuckUpperLeft", "LipSuckUpperRight", "LipSuckLowerLeft", "LipSuckLowerRight"));
            D("LipFunnel", Avg("LipFunnelUpperLeft", "LipFunnelUpperRight", "LipFunnelLowerLeft", "LipFunnelLowerRight"));
            D("LipPucker", Avg("LipPuckerUpperLeft", "LipPuckerUpperRight", "LipPuckerLowerLeft", "LipPuckerLowerRight"));

            // ---- 口 ----
            D("MouthUpperUp", LR("MouthUpperUp"));
            D("MouthLowerDown", LR("MouthLowerDown"));
            D("MouthOpen", Avg("MouthUpperUpLeft", "MouthUpperUpRight", "MouthLowerDownLeft", "MouthLowerDownRight"));
            D("MouthX", "(MouthUpperRight + MouthLowerRight - MouthUpperLeft - MouthLowerLeft) * 0.5");
            D("MouthUpperX", "MouthUpperRight - MouthUpperLeft");
            D("MouthLowerX", "MouthLowerRight - MouthLowerLeft");
            Side("MouthSmile", s => $"(MouthCornerPull{s} + MouthCornerSlant{s}) * 0.5");
            Side("MouthSad", s => $"(MouthFrown{s} + MouthStretch{s}) * 0.5");
            Side("SmileFrown", s => $"(MouthCornerPull{s} + MouthCornerSlant{s}) * 0.5 - MouthFrown{s}");
            D("SmileFrown", "(MouthCornerPullLeft + MouthCornerSlantLeft + MouthCornerPullRight + MouthCornerSlantRight) * 0.25 - (MouthFrownLeft + MouthFrownRight) * 0.5");
            Side("SmileSad", s => $"(MouthCornerPull{s} + MouthCornerSlant{s}) * 0.5 - (MouthFrown{s} + MouthStretch{s}) * 0.5");
            D("SmileSad", "(MouthCornerPullLeft + MouthCornerSlantLeft + MouthCornerPullRight + MouthCornerSlantRight - MouthFrownLeft - MouthStretchLeft - MouthFrownRight - MouthStretchRight) * 0.25");

            // ---- 舌 ----
            D("TongueX", "TongueRight - TongueLeft");
            D("TongueY", "TongueUp - TongueDown");
            D("TongueArchY", "TongueBendDown - TongueCurlUp");
            D("TongueShape", "TongueSquish - TongueFlat");
        }

        /// <summary>パラメータ名を Tracking Key の式にする。対応がなければ null。</summary>
        public static string ExprFor(string param)
        {
            if (string.IsNullOrEmpty(param)) return null;
            if (IsKey(param)) return param;
            return v2.TryGetValue(param, out var e) ? e : null;
        }

        static readonly HashSet<string> functions = new HashSet<string> { "abs", "min", "max", "clamp" };

        public static IEnumerable<string> Identifiers(string expr) =>
            System.Text.RegularExpressions.Regex.Matches(expr ?? "", "[A-Za-z_][A-Za-z0-9_]*").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value);

        /// <summary>式の中の、Tracking Key でも関数でもない名前。</summary>
        public static List<string> UnknownNames(string expr) =>
            Identifiers(expr).Where(n => !functions.Contains(n) && !IsKey(n)).Distinct().ToList();

        /// <summary>式が目 / 眉のキーを含むか (Eye 側の有効判定にするかの自動判定用)。</summary>
        public static bool UsesEyeKeys(string expr) =>
            Identifiers(expr).Any(n => IsKey(n) && (n.StartsWith("Eye") || n.StartsWith("Brow")));

        /// <summary>式が単一の Tracking Key か。</summary>
        public static bool IsSingleKey(string expr) => IsKey(expr);
    }
}
