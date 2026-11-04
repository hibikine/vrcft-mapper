using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace VRCFTMapper
{
    public enum FTParamType
    {
        Float = 0,   // 8bit 同期 float
        Binary = 1,  // n bit + (Negative)
    }

    public enum FTGate
    {
        Auto = 0,    // カタログのグループから自動 (Eye/Brow/Gaze → Eye, それ以外 → Lip)
        Eye = 1,     // EyeTrackingActive
        Lip = 2,     // LipTrackingActive || ExpressionTrackingActive
        Always = 3,  // 常に有効
    }

    public enum GestureDisableMode
    {
        None = 0,            // 何もしない
        LayerWeight = 1,     // VRC Animator Layer Control でレイヤー重みを 0 に
        TransitionCondition = 2, // 遷移に FacialExpressionsDisabled == false を追加 (テンプレート互換)
    }

    public enum GazeMode
    {
        None = 0,
        BlendShape = 1,  // eyeLookUp/Down/In/Out 系シェイプキー (通常のマッピングで設定)
        Bone = 2,        // Humanoid の目ボーン (Additive レイヤーでマッスル駆動)
    }

    public enum EyeSide { Auto = 0, Left = 1, Right = 2 }

    public enum FTOutputMode
    {
        Native = 0, // VRChat ネイティブ Face Tracking (アバターデスクリプタに書き出し。同期パラメータ不要)
        Legacy = 1, // 従来: VRCFT の FT/v2 パラメータを同期し、アニメーターで変形
    }

    /// <summary>ビルド時、アバターデスクリプタに既にある Face Tracking 設定の扱い。</summary>
    public enum FTExistingMode
    {
        Overwrite = 0, // 既存の設定をすべて消してから書き込む (既定)
        Merge = 1,     // 既存の設定を残して追加する (予期しない動作の可能性あり)
    }

    /// <summary>目周りの調整 (視線の加工・寄り目防止・まぶた追従・揺らぎ)。</summary>
    [Serializable]
    public class EyeSettings
    {
        // ---- 視線調整 (キャリブレーション) ----
        [Tooltip("入力に加えるオフセット (正面のずれ補正)")]
        public float offsetX = 0f, offsetY = 0f;
        [Tooltip("入力の倍率 (変化量スケーリング)")]
        public float gainX = 1f, gainY = 1f;
        [Tooltip("この値以下の小さな動きを無視")]
        [Range(0f, 0.5f)] public float deadzone = 0f;

        // ---- 視線変化カーブ (|入力| 0..1 → 0..1) ----
        public bool useCurve = false;
        public AnimationCurve curve = AnimationCurve.Linear(0, 0, 1, 1);

        // ---- 寄り目 (輻輳) 制御 ----
        [Tooltip("左右の目の向きの差 (輻輳) をどれだけ反映するか。0 = 常に平行")]
        [Range(0f, 1f)] public float vergenceScale = 1f;
        [Tooltip("片目あたりの内寄せの上限 (入力単位)。デフォルメ顔は小さめに")]
        [Range(0f, 1f)] public float maxConverge = 0.35f;
        [Tooltip("片目あたりの外開きの上限 (入力単位)")]
        [Range(0f, 1f)] public float maxDiverge = 0.05f;

        // ---- まぶた追従 ----
        [Tooltip("下を見たとき上まぶたを下げる量 (0..1 = シェイプキー 0..100)")]
        [Range(0f, 1f)] public float lidFollowDown = 0.3f;
        [Tooltip("上を見たとき目を見開く量")]
        [Range(0f, 1f)] public float lidFollowUp = 0f;
        public SkinnedMeshRenderer lidRenderer;
        public string lidDownLeft = "", lidDownRight = "", lidUpLeft = "", lidUpRight = "";

        // ---- 揺らぎ (トラッキング中のみ加算) ----
        [Tooltip("微細な震え (固視微動) の振幅 (入力単位)")]
        [Range(0f, 0.1f)] public float tremor = 0.008f;
        [Tooltip("小さな視線の跳び (マイクロサッカード) の振幅")]
        [Range(0f, 0.2f)] public float saccade = 0.025f;
        [Tooltip("跳びの平均間隔 (秒)")]
        [Range(0.3f, 5f)] public float saccadeInterval = 1.6f;
        public int seed = 1;
    }

    /// <summary>1 つの OSC パラメータ → 1 つのシェイプキーの対応。</summary>
    [Serializable]
    public class FTMapping
    {
        public bool enabled = true;

        [Tooltip("パラメータ名 (prefix を除く) 例: JawOpen, SmileFrownLeft")]
        public string parameter = "JawOpen";

        public FTParamType type = FTParamType.Float;
        [Range(1, 8)] public int binaryBits = 4;
        [Tooltip("負の値 (-1..0) を送るパラメータか (Binary 時に Negative ビットを追加)")]
        public bool signed = false;

        public SkinnedMeshRenderer renderer;
        public string blendShape = "";

        // in : OSC 値の閾値 / out : シェイプキー変形量 (1 = 100)
        public float inMin = 0f;
        public float inMax = 1f;
        public float outMin = 0f;
        public float outMax = 1f;

        [Tooltip("詳細モード: ベジエカーブで in→out を補間")]
        public bool useCurve = false;
        public AnimationCurve curve = AnimationCurve.Linear(0, 0, 1, 1);

        [Tooltip("どのトラッキング有効フラグで出力を ON/OFF するか")]
        public FTGate gate = FTGate.Auto;

        [Tooltip("平滑化係数 (毎フレーム目標値へ近づく割合)。負値 = 全体設定を使用")]
        public float smoothing = -1f;

        [Tooltip("ON: パラメータの代わりに Tracking Key の式を入力にする (ネイティブ出力のみ)。例: TongueOut * (1 - JawOpen)")]
        public bool useExpression = false;
        public string expression = "";

        [Tooltip("ネイティブ出力で out が 0..1 を超えるとき、変形量を拡大したシェイプキーをビルド時に生成する")]
        public bool overshootShape = true;

        [Tooltip("スタイル切替: このマッピングを有効にする選択肢 (0 = 常に有効, k = k 番目の選択肢)。指定するとアニメーター経由で動かす")]
        public int style = 0;

        [Tooltip("視線パラメータのとき、どちらの目の値を使うか (自動 = シェイプキー名の Left/Right)")]
        public EyeSide eyeSide = EyeSide.Auto;

        public FTMapping Clone()
        {
            var m = (FTMapping)MemberwiseClone();
            m.curve = new AnimationCurve(curve != null ? curve.keys : AnimationCurve.Linear(0, 0, 1, 1).keys);
            return m;
        }
    }

    /// <summary>シェイプキーの最終値の上限 / 下限を式で決める (ネイティブ: Limit Blendshape)。</summary>
    [Serializable]
    public class FTLimit
    {
        public bool enabled = true;
        public string blendShape = "";
        [Tooltip("OFF: 上限 (Max) / ON: 下限 (Min)")]
        public bool isMin = false;
        [Tooltip("Tracking Key の式 (0..1)。例: 1 - TongueOut")]
        public string expression = "";
    }

    /// <summary>
    /// スタイル切替。マッピングを選択肢ごとにまとめ、メニューで切り替える (例: 半目の種類)。
    /// 「自動」では autoExpression の値に最も近い autoValue の選択肢へなめらかに切り替わる。
    /// </summary>
    [Serializable]
    public class FTStyleGroup
    {
        public bool enabled = false;
        public string name = "目の表情";
        public List<string> options = new List<string>();
        [Tooltip("メニューに「自動」を付ける")]
        public bool auto = true;
        [Tooltip("自動のときに使う Tracking Key の式 (-1..1)")]
        public string autoExpression = "";
        [Tooltip("各選択肢が選ばれる autoExpression の値 (選択肢と同じ順)")]
        public List<float> autoValues = new List<float>();
        [Tooltip("切替にかける時間 (秒)")]
        public float transition = 0.2f;
    }

    /// <summary>左右共通のシェイプキーを、ビルド時に左右別のシェイプキーへ分ける設定。</summary>
    [Serializable]
    public class FTSplit
    {
        public bool enabled = true;
        public SkinnedMeshRenderer renderer;
        public string source = "";
        [Tooltip("中央で左右を混ぜる幅 (左右の目の間隔に対する割合)")]
        [Range(0.01f, 1f)] public float band = 0.25f;
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("VRCFT Mapper/VRCFT Mapper")]
    public class VRCFTMapper : MonoBehaviour, IEditorOnly
    {
        [Tooltip("OSC アドレス接頭辞 (/avatar/parameters/ の後ろ)。VRCFT は .../v2/XXX を検出します")]
        public string prefix = "FT/v2/";

        [Tooltip("ブレンドツリーでカーブを近似するサンプル数 (詳細モード時)")]
        [Range(3, 33)] public int curveSamples = 17;

        public List<FTMapping> mappings = new List<FTMapping>();

        // ---- 出力方式 ----
        public FTOutputMode outputMode = FTOutputMode.Native;
        [Tooltip("ネイティブ: FT / 表情モードを切り替えるメニューを追加 (同期 1bit)")]
        public bool nativeToggle = true;
        [Tooltip("ネイティブ: FT / 表情モード切替時に Blendshapes Set の重みが変わる速さ (大きいほど速い)")]
        public float nativeLerpSpeed = 7f;
        [Tooltip("ネイティブ: ベジエカーブを折れ線の式にするときの区間数")]
        [Range(2, 16)] public int nativeCurveSegments = 8;
        [Tooltip("ビルド時、アバターデスクリプタに既にある Face Tracking 設定を上書きするか合成するか")]
        public FTExistingMode existingFaceTracking = FTExistingMode.Overwrite;

        // ---- ① トラッキング有効フラグ / メニュー ----
        [Tooltip("EyeTrackingActive / LipTrackingActive / ExpressionTrackingActive を宣言し、メニューにトグルを追加")]
        public bool generateTrackingMenu = true;
        public string menuName = "Face Tracking";

        // ---- ② ジェスチャー表情の停止 ----
        public GestureDisableMode gestureMode = GestureDisableMode.LayerWeight;
        [Tooltip("自動検出から除外するレイヤー名")]
        public List<string> gestureExcluded = new List<string>();
        [Tooltip("自動検出されなくても対象にするレイヤー名")]
        public List<string> gestureForced = new List<string>();
        public float gestureBlendDuration = 0.2f;

        // ---- ③ VRChat 標準まばたき / リップシンクの無効化 ----
        public bool trackingControl = true;

        // ---- ④ スムージング ----
        public bool enableSmoothing = true;
        [Range(0.01f, 1f)] public float smoothLocal = 0.5f;
        [Range(0.01f, 1f)] public float smoothRemote = 0.15f;

        // ---- ⑤ 視線 ----
        public GazeMode gazeMode = GazeMode.None;
        [Tooltip("ON: EyeX/EyeY (両目共通, 16bit) / OFF: EyeLeftX, EyeRightX, EyeY (24bit)")]
        public bool gazeCombined = true;
        public float gazeYawIn = 20f, gazeYawOut = 20f, gazeUp = 15f, gazeDown = 12f;
        public bool gazeInvertX = false, gazeInvertY = false;
        [Range(0.01f, 1f)] public float gazeSmoothing = 0.4f;

        public EyeSettings eye = new EyeSettings();

        public List<FTLimit> limits = new List<FTLimit>();
        public FTStyleGroup styleGroup = new FTStyleGroup();
        public List<FTSplit> splits = new List<FTSplit>();
        [Tooltip("左右対称: ここにある Left 側パラメータの設定を Right 側へ自動でコピーする")]
        public List<string> mirrored = new List<string>();

        /// <summary>視線として扱うパラメータ名 (マッピングの通常処理から除外し、目の加工後の値を使う)。</summary>
        public static readonly HashSet<string> GazeNames = new HashSet<string>
        { "EyeX", "EyeY", "EyeLeftX", "EyeRightX", "EyeLeftY", "EyeRightY" };

        public static bool IsGaze(string param) => param != null && GazeNames.Contains(param);

        /// <summary>同期する視線パラメータ。</summary>
        public IEnumerable<string> GazeParams()
        {
            if (gazeMode == GazeMode.None || outputMode == FTOutputMode.Native) yield break;
            if (gazeCombined) { yield return "EyeX"; yield return "EyeY"; }
            else { yield return "EyeLeftX"; yield return "EyeRightX"; yield return "EyeY"; }
        }

        public string FullName(string param) => prefix + param;
    }

    public static class FTMath
    {
        /// <summary>
        /// in → out。in 範囲外は out の端に張り付く。out は 0..1 外もそのまま返す (シェイプキー限界超え)。
        /// outMin > outMax で反転。
        /// </summary>
        public static float Evaluate(FTMapping m, float x)
        {
            float t;
            if (Mathf.Approximately(m.inMin, m.inMax)) t = x >= m.inMax ? 1f : 0f;
            else t = Mathf.Clamp01((x - m.inMin) / (m.inMax - m.inMin));
            if (m.useCurve && m.curve != null && m.curve.length >= 2) t = m.curve.Evaluate(t);
            return Mathf.LerpUnclamped(m.outMin, m.outMax, t);
        }

        public static int SyncBits(FTMapping m)
            => m.type == FTParamType.Float ? 8 : m.binaryBits + (m.signed ? 1 : 0);

        /// <summary>Binary 量子化後の値 (プレビューで実機と同じ段差を再現)。</summary>
        public static float Quantize(FTMapping m, float x)
        {
            if (m.type == FTParamType.Float)
            {
                // VRChat の 8bit float 同期: -1..1 を 255 段階
                return Mathf.Round(Mathf.Clamp(x, -1, 1) * 127f) / 127f;
            }
            int steps = (1 << m.binaryBits) - 1;
            float a = m.signed ? Mathf.Clamp(x, -1, 1) : Mathf.Clamp01(x);
            float q = Mathf.Round(Mathf.Abs(a) * steps) / steps;
            return a < 0 ? -q : q;
        }
    }
}
