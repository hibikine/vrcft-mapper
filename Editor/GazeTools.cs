using System;
using UnityEngine;

namespace VRCFTMapper.Editor
{
    /// <summary>目ボーン (Humanoid マッスル) 関連。</summary>
    public static class GazeTools
    {
        public const string LeftDownUp = "Left Eye Down-Up";
        public const string LeftInOut = "Left Eye In-Out";
        public const string RightDownUp = "Right Eye Down-Up";
        public const string RightInOut = "Right Eye In-Out";

        /// <summary>マッスルの可動域 (度)。アバター側でカスタム設定されていればそれを使用。</summary>
        public static (float min, float max) MuscleRange(Animator anim, string muscle)
        {
            int mi = Array.IndexOf(HumanTrait.MuscleName, muscle);
            if (mi < 0) return (-20, 20);
            float min = HumanTrait.GetMuscleDefaultMin(mi), max = HumanTrait.GetMuscleDefaultMax(mi);
            if (anim == null || anim.avatar == null || !anim.avatar.isHuman) return (min, max);

            int bone = HumanTrait.BoneFromMuscle(mi);
            int dof = -1;
            for (int d = 0; d < 3; d++) if (HumanTrait.MuscleFromBone(bone, d) == mi) dof = d;
            var boneName = HumanTrait.BoneName[bone];
            foreach (var hb in anim.avatar.humanDescription.human)
            {
                if (hb.humanName != boneName || hb.limit.useDefaultValues || dof < 0) continue;
                min = hb.limit.min[dof];
                max = hb.limit.max[dof];
            }
            return (min, max);
        }

        /// <summary>角度 (度) → マッスル値 (-1..1)。</summary>
        public static float AngleToMuscle(float angle, (float min, float max) range)
        {
            if (angle >= 0) return Mathf.Abs(range.max) < 1e-3f ? 0 : angle / range.max;
            return Mathf.Abs(range.min) < 1e-3f ? 0 : angle / -range.min;
        }

        /// <summary>
        /// 入力 x (-1..1, +右/+上) → 目の角度。in/out は目ごとに向きが違う。
        /// 左目: 右を見る = 内側 (In)、右目: 右を見る = 外側 (Out)。
        /// 戻り値: (yaw: +外側, pitch: +上)
        /// </summary>
        public static float YawAngle(VRCFTMapper m, bool leftEye, float x)
        {
            if (m.gazeInvertX) x = -x;
            bool towardOut = leftEye ? x < 0 : x > 0;
            float mag = Mathf.Abs(x) * (towardOut ? m.gazeYawOut : m.gazeYawIn);
            return towardOut ? mag : -mag;
        }

        public static float PitchAngle(VRCFTMapper m, float y)
        {
            if (m.gazeInvertY) y = -y;
            return y >= 0 ? y * m.gazeUp : y * m.gazeDown;
        }
    }
}
