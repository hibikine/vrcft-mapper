using System.Collections.Generic;

namespace VRCFTMapper.Editor
{
    /// <summary>VRCFT v2 パラメータと、対応しうるシェイプキー名の候補。</summary>
    public class FTTargetSpec
    {
        public string[] candidates;      // 優先順 (Unified Expressions → ARKit → SRanipal → MMD/viseme)
        public float inMin, inMax, outMin, outMax;
        public FTTargetSpec(float inMin, float inMax, float outMin, float outMax, params string[] c)
        { this.inMin = inMin; this.inMax = inMax; this.outMin = outMin; this.outMax = outMax; candidates = c; }
    }

    public class FTParamSpec
    {
        public string name;
        public string label;
        public bool signed;
        public string group;
        public float rest;   // 無表情時の値 (EyeLid は 0.75)
        public List<FTTargetSpec> targets = new List<FTTargetSpec>();
    }

    public static class FTCatalog
    {
        public static readonly List<FTParamSpec> All = new List<FTParamSpec>();
        static readonly Dictionary<string, FTParamSpec> byName = new Dictionary<string, FTParamSpec>();

        public static FTParamSpec Find(string name) => byName.TryGetValue(name ?? "", out var s) ? s : null;

        static FTParamSpec P(string group, string name, string label, bool signed = false)
        {
            var p = new FTParamSpec { group = group, name = name, label = label, signed = signed };
            All.Add(p); byName[name] = p; return p;
        }
        static FTParamSpec T(this FTParamSpec p, params string[] c) { p.targets.Add(new FTTargetSpec(0, 1, 0, 1, c)); return p; }
        static FTParamSpec Tr(this FTParamSpec p, float i0, float i1, float o0, float o1, params string[] c)
        { p.targets.Add(new FTTargetSpec(i0, i1, o0, o1, c)); return p; }

        // 負側: in -1..0 を out 1..0 に (反転)
        static FTParamSpec Rest(this FTParamSpec p, float v) { p.rest = v; return p; }

        /// <summary>トラッキング有効フラグの自動判定。</summary>
        public static FTGate AutoGate(string param)
        {
            var g = Find(param)?.group;
            return g == "Eye" || g == "Brow" || g == "Gaze" ? FTGate.Eye : FTGate.Lip;
        }

        static FTParamSpec Neg(this FTParamSpec p, params string[] c) => p.Tr(-1, 0, 1, 0, c);

        static void LR(string group, string baseName, string label, string[] l, string[] r)
        {
            P(group, baseName + "Left", label + " 左").T(l);
            P(group, baseName + "Right", label + " 右").T(r);
        }

        static FTCatalog()
        {
            // ---- Gaze (シェイプキー型視線。+X = 右を見る) ----
            P("Gaze", "EyeX", "視線 左右 両目", true)
                .T("EyeLookInLeft", "eyeLookInLeft", "Eye_Left_Right").T("EyeLookOutRight", "eyeLookOutRight", "Eye_Right_Right")
                .Neg("EyeLookOutLeft", "eyeLookOutLeft", "Eye_Left_Left").Neg("EyeLookInRight", "eyeLookInRight", "Eye_Right_Left");
            P("Gaze", "EyeY", "視線 上下 両目", true)
                .T("EyeLookUpLeft", "eyeLookUpLeft", "Eye_Left_Up").T("EyeLookUpRight", "eyeLookUpRight", "Eye_Right_Up")
                .Neg("EyeLookDownLeft", "eyeLookDownLeft", "Eye_Left_Down").Neg("EyeLookDownRight", "eyeLookDownRight", "Eye_Right_Down");
            P("Gaze", "EyeLeftX", "視線 左右 左目", true).T("EyeLookInLeft", "eyeLookInLeft", "Eye_Left_Right").Neg("EyeLookOutLeft", "eyeLookOutLeft", "Eye_Left_Left");
            P("Gaze", "EyeRightX", "視線 左右 右目", true).T("EyeLookOutRight", "eyeLookOutRight", "Eye_Right_Right").Neg("EyeLookInRight", "eyeLookInRight", "Eye_Right_Left");
            P("Gaze", "EyeLeftY", "視線 上下 左目", true).T("EyeLookUpLeft", "eyeLookUpLeft", "Eye_Left_Up").Neg("EyeLookDownLeft", "eyeLookDownLeft", "Eye_Left_Down");
            P("Gaze", "EyeRightY", "視線 上下 右目", true).T("EyeLookUpRight", "eyeLookUpRight", "Eye_Right_Up").Neg("EyeLookDownRight", "eyeLookDownRight", "Eye_Right_Down");

            // ---- Eye ----
            // EyeLid: 0 閉じ / 0.75 通常 / 1 見開き
            P("Eye", "EyeLidLeft", "まぶた 左 (0閉-0.75通常-1見開)").Rest(0.75f)
                .Tr(0, 0.75f, 1, 0, "EyeClosedLeft", "eyeBlinkLeft", "Eye_Left_Blink", "Blink_L", "blink_l", "EyeBlink_L", "ウィンク２", "ウィンク")
                .Tr(0.75f, 1, 0, 1, "EyeWideLeft", "eyeWideLeft", "Eye_Left_Wide", "EyeWide_L");
            P("Eye", "EyeLidRight", "まぶた 右 (0閉-0.75通常-1見開)").Rest(0.75f)
                .Tr(0, 0.75f, 1, 0, "EyeClosedRight", "eyeBlinkRight", "Eye_Right_Blink", "Blink_R", "blink_r", "EyeBlink_R", "ウィンク２右", "ウィンク右")
                .Tr(0.75f, 1, 0, 1, "EyeWideRight", "eyeWideRight", "Eye_Right_Wide", "EyeWide_R");
            P("Eye", "EyeLid", "まぶた 両目").Rest(0.75f)
                .Tr(0, 0.75f, 1, 0, "EyeClosed", "eyeBlink", "Blink", "まばたき", "vrc.blink")
                .Tr(0.75f, 1, 0, 1, "EyeWide", "びっくり");
            LR("Eye", "EyeSquint", "目を細める",
                new[] { "EyeSquintLeft", "eyeSquintLeft", "Eye_Left_Squeeze", "EyeSquint_L" },
                new[] { "EyeSquintRight", "eyeSquintRight", "Eye_Right_Squeeze", "EyeSquint_R" });
            P("Eye", "EyeSquint", "目を細める 両目").T("EyeSquint", "笑い", "なごみ");

            // ---- Brow ----
            LR("Brow", "BrowInnerUp", "眉 内側上げ", new[] { "BrowInnerUpLeft" }, new[] { "BrowInnerUpRight" });
            LR("Brow", "BrowOuterUp", "眉 外側上げ", new[] { "BrowOuterUpLeft", "browOuterUpLeft" }, new[] { "BrowOuterUpRight", "browOuterUpRight" });
            LR("Brow", "BrowLowerer", "眉 下げ", new[] { "BrowLowererLeft" }, new[] { "BrowLowererRight" });
            LR("Brow", "BrowPinch", "眉 寄せ", new[] { "BrowPinchLeft" }, new[] { "BrowPinchRight" });
            LR("Brow", "BrowDown", "眉 下げ(簡易)", new[] { "BrowDownLeft", "browDownLeft", "BrowDown_L" }, new[] { "BrowDownRight", "browDownRight", "BrowDown_R" });
            P("Brow", "BrowInnerUp", "眉 内側上げ 両").T("BrowInnerUp", "browInnerUp", "困る");
            P("Brow", "BrowUp", "眉 上げ 両").T("BrowUp", "上");
            P("Brow", "BrowExpressionLeft", "眉 上下 左", true)
                .T("BrowOuterUpLeft", "browOuterUpLeft", "BrowUp_L").Neg("BrowDownLeft", "browDownLeft", "BrowLowererLeft", "BrowDown_L");
            P("Brow", "BrowExpressionRight", "眉 上下 右", true)
                .T("BrowOuterUpRight", "browOuterUpRight", "BrowUp_R").Neg("BrowDownRight", "browDownRight", "BrowLowererRight", "BrowDown_R");
            P("Brow", "BrowExpression", "眉 上下 両", true).T("BrowUp", "上").Neg("BrowDown", "下", "怒り");

            // ---- Nose / Cheek ----
            LR("Nose", "NoseSneer", "鼻にしわ", new[] { "NoseSneerLeft", "noseSneerLeft", "Nose_Sneer_L" }, new[] { "NoseSneerRight", "noseSneerRight", "Nose_Sneer_R" });
            P("Nose", "NoseSneer", "鼻にしわ 両").T("NoseSneer");
            LR("Cheek", "CheekSquint", "頬上げ", new[] { "CheekSquintLeft", "cheekSquintLeft" }, new[] { "CheekSquintRight", "cheekSquintRight" });
            P("Cheek", "CheekPuffSuckLeft", "頬 膨らみ/吸い 左", true).T("CheekPuffLeft", "Cheek_Puff_Left").Neg("CheekSuckLeft", "Cheek_Suck_Left");
            P("Cheek", "CheekPuffSuckRight", "頬 膨らみ/吸い 右", true).T("CheekPuffRight", "Cheek_Puff_Right").Neg("CheekSuckRight", "Cheek_Suck_Right");
            P("Cheek", "CheekPuffSuck", "頬 膨らみ/吸い 両", true).T("CheekPuff", "cheekPuff", "Cheek_Puff", "ぷくー").Neg("CheekSuck", "Cheek_Suck");

            // ---- Jaw ----
            P("Jaw", "JawOpen", "口を開く(顎)").T("JawOpen", "jawOpen", "Jaw_Open", "MouthOpen", "あ", "vrc.v_aa", "mouth_a");
            P("Jaw", "MouthClosed", "口を閉じる").T("MouthClosed", "mouthClose", "Mouth_Close");
            P("Jaw", "JawX", "顎 左右", true).T("JawRight", "jawRight", "Jaw_Right").Neg("JawLeft", "jawLeft", "Jaw_Left");
            P("Jaw", "JawZ", "顎 前後", true).T("JawForward", "jawForward", "Jaw_Forward").Neg("JawBackward", "Jaw_Backward");
            P("Jaw", "JawClench", "顎 食いしばり").T("JawClench");

            // ---- Lip ----
            P("Lip", "LipFunnel", "口をすぼめる(漏斗)").T("LipFunnel", "mouthFunnel", "Mouth_O_Shape", "お", "vrc.v_oh");
            P("Lip", "LipPucker", "口をとがらす").T("LipPucker", "mouthPucker", "Mouth_Pout", "う", "vrc.v_ou");
            P("Lip", "LipSuckUpper", "上唇を巻き込む").T("LipSuckUpper", "mouthRollUpper", "Mouth_Upper_Inside");
            P("Lip", "LipSuckLower", "下唇を巻き込む").T("LipSuckLower", "mouthRollLower", "Mouth_Lower_Inside");
            P("Lip", "LipFunnelUpper", "上唇 漏斗").T("LipFunnelUpper");
            P("Lip", "LipFunnelLower", "下唇 漏斗").T("LipFunnelLower");
            P("Lip", "LipPuckerUpper", "上唇 とがらす").T("LipPuckerUpper");
            P("Lip", "LipPuckerLower", "下唇 とがらす").T("LipPuckerLower");

            // ---- Mouth ----
            LR("Mouth", "MouthUpperUp", "上唇上げ", new[] { "MouthUpperUpLeft", "mouthUpperUpLeft", "Mouth_Upper_UpLeft" }, new[] { "MouthUpperUpRight", "mouthUpperUpRight", "Mouth_Upper_UpRight" });
            LR("Mouth", "MouthLowerDown", "下唇下げ", new[] { "MouthLowerDownLeft", "mouthLowerDownLeft", "Mouth_Lower_DownLeft" }, new[] { "MouthLowerDownRight", "mouthLowerDownRight", "Mouth_Lower_DownRight" });
            P("Mouth", "MouthUpperUp", "上唇上げ 両").T("MouthUpperUp", "mouthUpperUp");
            P("Mouth", "MouthLowerDown", "下唇下げ 両").T("MouthLowerDown", "mouthLowerDown");
            P("Mouth", "MouthX", "口 左右", true).T("MouthRight", "mouthRight", "Mouth_Right").Neg("MouthLeft", "mouthLeft", "Mouth_Left");
            P("Mouth", "MouthUpperX", "上唇 左右", true).T("MouthUpperRight", "Mouth_Upper_Right").Neg("MouthUpperLeft", "Mouth_Upper_Left");
            P("Mouth", "MouthLowerX", "下唇 左右", true).T("MouthLowerRight", "Mouth_Lower_Right").Neg("MouthLowerLeft", "Mouth_Lower_Left");
            P("Mouth", "SmileFrownLeft", "笑み/への字 左", true)
                .T("MouthSmileLeft", "mouthSmileLeft", "Mouth_Smile_Left", "MouthCornerPullLeft").Neg("MouthFrownLeft", "mouthFrownLeft", "Mouth_Sad_Left");
            P("Mouth", "SmileFrownRight", "笑み/への字 右", true)
                .T("MouthSmileRight", "mouthSmileRight", "Mouth_Smile_Right", "MouthCornerPullRight").Neg("MouthFrownRight", "mouthFrownRight", "Mouth_Sad_Right");
            P("Mouth", "SmileFrown", "笑み/への字 両", true).T("MouthSmile", "にっこり", "口角上げ", "∧").Neg("MouthFrown", "口角下げ", "∨");
            P("Mouth", "SmileSad", "笑み/悲しみ 両", true).T("MouthSmile", "にっこり").Neg("MouthSad", "口角下げ");
            LR("Mouth", "MouthStretch", "口を横に引く", new[] { "MouthStretchLeft", "mouthStretchLeft" }, new[] { "MouthStretchRight", "mouthStretchRight" });
            LR("Mouth", "MouthPress", "唇を押し付け", new[] { "MouthPressLeft", "mouthPressLeft" }, new[] { "MouthPressRight", "mouthPressRight" });
            LR("Mouth", "MouthDimple", "えくぼ", new[] { "MouthDimpleLeft", "mouthDimpleLeft" }, new[] { "MouthDimpleRight", "mouthDimpleRight" });
            LR("Mouth", "MouthTightener", "唇を締める", new[] { "MouthTightenerLeft" }, new[] { "MouthTightenerRight" });
            P("Mouth", "MouthRaiserUpper", "上口上げ").T("MouthRaiserUpper", "mouthShrugUpper");
            P("Mouth", "MouthRaiserLower", "下口上げ").T("MouthRaiserLower", "mouthShrugLower");
            P("Mouth", "MouthOpen", "口を開く(唇)").T("MouthOpen", "い", "vrc.v_ih");

            // ---- Tongue ----
            P("Tongue", "TongueOut", "舌を出す").T("TongueOut", "tongueOut", "Tongue_LongStep1", "ぺろっ", "べー");
            P("Tongue", "TongueX", "舌 左右", true).T("TongueRight", "Tongue_Right").Neg("TongueLeft", "Tongue_Left");
            P("Tongue", "TongueY", "舌 上下", true).T("TongueUp", "Tongue_Up").Neg("TongueDown", "Tongue_Down");
            P("Tongue", "TongueRoll", "舌を丸める").T("TongueRoll", "Tongue_Roll");
        }
    }
}
