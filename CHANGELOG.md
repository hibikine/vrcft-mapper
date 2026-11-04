# Changelog

## [Unreleased]
- VRC Avatar Descriptor に Face Tracking 設定がある場合、インスペクターに警告を表示 (ビルド時は上書きされる旨)
- デスクリプタの Face Tracking 設定を VRCFT Mapper に読み込む「設定を追加する」「設定を上書きする」(Ctrl+Z 対応)
- デスクリプタの Face Tracking 設定をすべて削除するボタン (確認あり・Ctrl+Z 対応)
- ビルド時は既存の Face Tracking 設定を既定で上書き。上級者向け設定で「合成」を選択可能

## [0.2.0] - 2026-11-05
- 修正: グループグラフでカーブをドラッグ編集した後、Ctrl+Z で元に戻せず直前の別の操作が戻っていた問題
- 式入力のマッピング、上限 / 下限 (Limit Blendshape)、左右分離シェイプキーの自動生成
- ネイティブ出力 (VRChat Face Tracking): Blendshapes Set / Parameter Behavior への書き出し、範囲外用シェイプキー生成、ランク予測、FT / 表情トグル
- プレイモードのアバター (Av3Emulator / Gesture Manager / Animator) へトラッキング有効フラグと入力を送る機能
- 修正: トラッキング OFF 時に Mapping レイヤーがリップシンク・ジェスチャー表情を上書きしていた問題
- 初期実装: パラメータ自動設定、in/out 範囲とベジエカーブ、顔プレビュー、OSC 受信 / 録画 / 再生、OSCQuery による VRChat なしテスト
- トラッキング有効フラグとメニュー、ジェスチャー表情の停止、標準まばたき / リップシンクの無効化、スムージング
- 目・視線: 視線調整、視線変化カーブ、寄り目制御、まぶた追従、自然な揺らぎ
