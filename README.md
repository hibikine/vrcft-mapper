# VRCFT Mapper

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

> **開発中です。まだリリースしていません。**

VRCFaceTracking (v2 パラメータ) → シェイプキーの対応を Unity 上で設定し、ビルド時に非破壊で FX レイヤーと同期パラメータを生成するエディタ拡張です。

## 導入
リリース後は VCC (VRChat Creator Companion) から追加できる予定です。

1. VCC の Settings → Packages → Add Repository に `https://vpm.hikage.works/index.json` を追加 (公開準備中)
2. アバタープロジェクトの Manage Project から **VRCFT Mapper** を追加
3. メニュー **Tools > VRCFT Mapper** を開き、アバターを指定 → 「VRCFT Mapper を追加」

依存: VRChat SDK Avatars / NDMF / Modular Avatar

### Nightly (開発版)
main の最新状態を毎回ビルドした不安定版です。`https://vpm.hikage.works/nightly.json` (公開準備中) を追加すると、通常版と Nightly の両方が選べます。通常版の `index.json` とは同時に追加しないでください。

## 使い方
| 機能 | 操作 |
|---|---|
| 顔プレビュー | 左上。Humanoid の Head / Eye ボーンからカメラ位置を自動決定。アバターを複製して描画するので本体は無変更 |
| 自動設定 | 「標準セット」= よく使う簡易パラメータ約 20 個 / 「全パラメータ」= Unified Expressions 全体。Unified Expressions・ARKit・SRanipal・MMD (あ/お/う/まばたき/ウィンク等)・VRChat viseme 名から検索 |
| 修正 | 各行のパラメータ▼ (カタログ選択 or 自由入力)、シェイプキー▼ で付け替え。「シェイプキー名から推定」もあり |
| in / out | 青▼=下、赤▼=上。in の下以下→out 下、上以上→out 上に張り付き。out は -1〜2 まで (0〜1 外はシェイプキー限界超え)。out の▼は交差可能 (= 反転、バーがオレンジ表示)。「⇅ out 反転」でも可 |
| 詳細モード | 「詳細(カーブ)」ON でベジエ曲線エディタ。ダブルクリック追加、右クリック削除/プリセット、Alt でハンドル分離、Shift でスナップ |
| ライブプレビュー | 「シーンにライブプレビュー」= AnimationMode で一時反映 (OFF で元に戻る) |
| OSC テスト | 「OSC ライブ」で UDP 9002 受信。VRCFT の出力先 (または OSC ルーター) を 9002 に向けてください |
| 録画/再生 | 「● OSC を録画」→ .asset 保存 → 「録画再生」で再生・シーク・ループ |
| 容量 | 左下バー。Expression Parameters + 他の MA Parameters + FT 分 / 256 bit |

Undo/Redo: マッピング・範囲・カーブ・自動設定・追加/削除・コンポーネント追加はすべて Undo 対応。プレビュー状態・OSC 受信状態・テスト入力値は設定ではないため対象外です。

## ビルド時の生成物
- `FT/v2/<Name>` (Float, 8bit) または `FT/v2/<Name>1,2,4…` (+ `Negative`) の Bool 群を MA Parameters で宣言
- Binary は AAP (Animated Animator Parameter) で 1 レイヤー目にデコード (1 フレーム遅延)
- 2 レイヤー目は Direct BlendTree + 1D BlendTree。カーブは `curveSamples` 点で折れ線近似
- WD ON 前提 (Direct BlendTree のため)

## トラッキング制御 (v0.2)
左パネル「トラッキング制御」で設定します。

| # | 機能 | 生成物 |
|---|---|---|
| ① | `EyeTrackingActive` / `LipTrackingActive` / `ExpressionTrackingActive` を同期 Bool で宣言、メニュー (既定「Face Tracking」) にトグル | MA Parameters / MA Menu Installer + Menu Item。Mapping 層の各子ツリーの重みを有効フラグにし、OFF 時は出力しない |
| ② | トラッキング中にハンドジェスチャー表情を停止 | `FacialExpressionsDisabled` (Bool) を Parameter Driver で設定。最終 FX (MA マージ後, Optimizing フェーズ) で Gesture* を参照し顔シェイプキーを動かすレイヤーを検出し、Layer Control で重み 0 (または遷移に条件追加)。対象は UI で除外/追加可 |
| ③ | トラッキング中は VRChat 標準の自動まばたき・視線 / リップシンクを停止 | VRC Animator Tracking Control (eyes&eyelids / mouth&jaw) |
| ④ | 指数平滑 `S ← S + k(T−S)` | AAP の Smooth 層。k は IsLocal で自分/他人を切替、パラメータ個別指定も可 |
| ⑤ | 視線 | シェイプキー型: EyeX/EyeY を eyeLook 系へ自動割当。ボーン型: Additive 層で Eye マッスル駆動 (可動域はアバターの Humanoid 設定から計算、独自に平滑化) |

注意:
- 有効フラグが OFF のときはシェイプキーを出力しません。テスト時は Av3Emulator 等でフラグを ON にしてください (VRCFT 接続時は自動で ON)。
- ボーン型視線は Additive + 加算基準ポーズを使います。向きが逆なら X/Y 反転で調整し、実機で確認してください。
- AAO など Optimizing フェーズでレイヤーを統合・削除するツールと併用する場合、② の Layer Control のレイヤー番号がずれる可能性があります。その場合は ② を「遷移条件」方式にしてください。

## 目・視線 (v0.3)
右パネル「目・視線」タブで設定します。視線の方式が Bone / BlendShape のときに有効です。

| 機能 | 内容 | 実装 |
|---|---|---|
| 視線調整 | オフセット / 倍率 (変化量スケーリング) / デッドゾーン / 「今の視線を正面にする」 | Eye Shape レイヤー (33 点の折れ線) |
| 視線変化カーブ | |入力| に対するベジエカーブ (符号は保持) | 同上 |
| 寄り目制御 | 左右差 v を反映率で縮め、内寄せ / 外開きを tanh で滑らかに上限処理。デフォルメ向けプリセットあり | Eye Verge + Eye Soft Limit レイヤー |
| まぶた追従 | 下を見ると上まぶたを下げる / 上を見ると見開く。目を閉じているほど弱まる | Mapping の Direct ツリーに 2D ブレンドで加算 |
| 揺らぎ | 固視微動 (正弦の和) + マイクロサッカード (ランダム間隔の小さな跳び)。30 秒ループ | ループクリップで AAP を駆動し出力に加算 |

- テスト: 視線パッド (ドラッグ)・X/Y スライダー・片目の寄せ量・注視距離 (m) から寄り目を再現。顔プレビューで目ボーン / eyeLook シェイプキー / まぶたが動きます。
- 処理は 平滑化 → 整形 → 揺らぎ → 輻輳 → ソフト上限 → 出力 の順で、各段が別レイヤーのため数フレームの遅れがあります。
- 目ボーンは Additive、シェイプキー視線とまぶた追従は FX で同じパイプラインを生成します (AAP はレイヤー間で共有されないため)。

## ライセンス
[MIT License](LICENSE)
