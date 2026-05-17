## Claude Code 追加指示
- 日本語応対を行うこと。
- CLAUDELOG.local.logに追記する形で、やりとりが一度完了する度に指示そのままの文言と実際の作業内容を200文字以内程度にまとめること。
  - 次のフォーマットを厳守すること。
    `<改行>---<改行><改行>**指示**: <指示文言><改行><改行>**作業**: <作業まとめ><改行>`
  - ファイルの中身を確認しないこと。
  - ファイルが無い場合は作成すること。

---

# MediaPreviewSan

キャプチャーボード・Web カメラ・仮想カメラ（OBS Virtual Camera 等）の出力を確認できる軽量プレビューツール。
WinForms / .NET 8 / Windows 10-11 x64。Alpine Linux 上でクロスコンパイルし単一 exe で配布。

---

## 1. 要件（実現したかったこと）と細部仕様

機能の「正しい挙動」は試行錯誤で細かく確定している。次回これを仕様として尊重すること。

- 入力デバイス映像をメインウィンドウでリアルタイム表示。ウィンドウ拡縮可。
- 右クリックメニュー: **設定 / 再接続 / 接続解除 / 終了**。
  - **再接続**: 現在の設定デバイスへ再接続（排他していた他アプリを閉じた後に使う）。`StartCapture` を呼ぶだけ。
  - **接続解除**: `_capture.Dispose()` でデバイス排他を解放し、他アプリ（OBS/Teams 等）に制御を渡す。空インスタンスへ差替え、案内オーバーレイ表示。
- 設定ダイアログ: デバイス / 解像度・FPS / 補間 / アス比維持。`settings.json` に保存。初回起動時は自動表示。
  - 補間コンボのラベルは「カタカナ：和訳：補足」形式（例: `バイリニア：双線形：既定・滑らか`）。値は `Bilinear` / `Point` / `Anisotropic`。
  - 「自動 - 最高画質: WxH @ Nfps [SubType]」を先頭に置く。自動選択時は列挙の最高画質を採用。
  - デバイス一覧の直下に「N 件のデバイスを検出しました」。その下に取得/描画方式の注記:
    - MF: `取得: Media Foundation ／ 描画: Direct3D11 (GPU)`
    - DS: `取得: DirectShow（レガシー）+ OpenCV ／ 描画: Direct3D11 (GPU)`
  - ClientSize は 540x320（幅は長いデバイス名・解像度名のため広めが必須）。
  - 設定画面を開くだけでは再検出しない（キャッシュ表示）。**再検出ボタン押下時のみ**実列挙。
  - 再検出後は「再検出前の UI 選択」を完全復元する（保存設定に巻き戻さない）。デバイス・解像度・FPS・**カラーフォーマット**・補間・アス比すべて。FPS は小数があるので `CaptureFormat.Equals`（W/H 一致 + `Abs(Fps差)<0.01` + SubType 完全一致）で完全一致 → 解像度+FPS(±0.05) → 解像度のみ、の3段フォールバック。
  - Windows のタスク終了要求（TaskManagerClosing / WindowsShutDown / ApplicationExitCall）時、設定に変更が無ければキャンセル扱いで設定画面とメインウィンドウを閉じて終了。変更ありなら終了をブロック。
- タイトルバー: `MediaPreviewSan - <デバイス名> [WxH @ Nfps] N.Nfps`
  - 括弧内 `@ Nfps`: 設定 FPS。**自動指定時は実際に適用された公称 FPS（`NominalFps`）**。実測ではない。
  - 括弧の後 ` N.Nfps`: 実測 FPS（数字のみ・空白1つ）。1 秒周期タイマーで更新。
- 起動中ダイアログ: 検出中ダイアログ（`BusyDialog`）を流用。「MediaPreviewSan起動中…」→「デバイスを検出中…」と同一ダイアログで推移。**処理が 0.5s 以内に終わるなら出さない**（0.5s 遅延表示）。位置は Show 前に確定（左上チラ見防止）。設定なし=実行モニタ中央、設定あり=メインウィンドウ中心。
- 初回起動時のメインウィンドウは「実行したモニタ（カーソルのある画面）」の中央。保存位置が画面外でも同様。
- デバイスが他アプリに排他使用されている時は `ShowExclusiveError` で案内し、右クリック「再接続」で復帰。
- ウィンドウ位置/サイズ保存は **500ms デバウンス**（初期化・移動中の連続発火で 1 秒 30 回保存されたバグの対策）。

---

## 2. アーキテクチャ

### 2 系統キャプチャ（`ICaptureService` で抽象化）

| デバイス種別 | サービス | 取得 |
|---|---|---|
| MF 対応（物理デバイス。MF 列挙に出る） | `MediaFoundationCaptureService` | MF SourceReader で NV12 |
| DS-only（OBS 等仮想カメラ。MF 列挙に出ない） | `DirectShowCaptureService` | OpenCvSharp `VideoCapture(CAP_DSHOW)` で BGR |

- `DeviceInfo.IsDirectShowOnly`（SymbolicLink 空 & DirectShowDevicePath あり）で判定。`MainForm.StartCapture` が適切な service を生成。`PersistentId`（MF=SymbolicLink / DS=DevicePath）で設定保存・復元。
- デバイス列挙 `MediaFoundationCaptureService.EnumerateDevices`: MF を `default` + `KSCATEGORY_VIDEO_CAMERA` の2カテゴリで列挙 → `DsDevice` でも列挙して補完。`\\?\` で始まる物理は MF 兼用、`@device:sw:` 系は DS-only。SymbolicLink/Name 両軸で重複排除し index 付き詳細ログ。
- 解像度列挙: MF=SourceReader `GetNativeMediaType`、DS=DirectShowLib `IAMStreamConfig`/`GetStreamCaps`。`DeviceCache` がデバイス/解像度をプロセス内キャッシュ（起動時と再検出のみ実列挙、`DS|`/`MF|` でキー分離）。
  - **OBS 等は IAMStreamConfig で 1 解像度しか公開しない** → 標準解像度プリセット（3840x2160〜424x240）を **ネイティブ最大解像度以下のみ**、ネイティブ FPS 付きでマージ（仮想カメラは OpenCV.Set でネイティブ以下へソフトリサイズ可）。

### 描画 `Nv12Renderer`（D3D11 GPU・専用描画スレッド。MF/DS 共通）

- D3D11 Device / DXGI SwapChain / Context を**専用描画スレッドが完全専有**。UI・取得スレッドからは `ConcurrentQueue` でコマンド受け渡し。これが「カメラ FPS にデスクトップが引きずられる」問題の根治策。
- `IDXGISwapChain2.FrameLatencyWaitableObject` でディスプレイ V-sync に同期（カメラ FPS とディスプレイ FPS を分離）。Waitable 不可なら AllowTearing にフォールバック。
- **NV12 パス**（MF 用）: HLSL で BT.709 limited→full レンジ変換、YUV→RGB。
- **BGRA パス**（DS/OpenCV 用）: `UpdateBgra` で B8G8R8A8 テクスチャ転送、`ps_bgra` でそのまま出力。`_pendingMode`/`_renderMode` で NV12/BGRA 切替。
- 補間は D3D11 サンプラ（Point/Linear/Anisotropic）、アス比は描画先矩形 `SetDrawRect`。MF/DS 共通。
- フルスクリーン三角形は **CullMode=None 必須**（CCW なので既定のバックフェイスカリングで黒画面になる）。

### 主要ファイル

- `Program.cs` — エントリ。`MediaFoundationCaptureService.GlobalStartup/Shutdown`（MFStartup/MFShutdown を try/finally で確実に）。
- `MainForm.cs` — メインウィンドウ。右クリックメニュー、信号待ち（`_signalTimer`）、タイトル更新（`_renderTimer` 1秒周期で実測 FPS 反映）、起動ダイアログ、`ShowExclusiveError`/`Reconnect`/`Disconnect`、ウィンドウ位置（実行モニタ中央／保存復元）、Save 500ms デバウンス。
- `SettingsForm.cs` — 設定。`DeviceCache` 利用。`BusyDialog` 0.5s 遅延表示。再検出は `_restore*`（`_restoreFormat` は `CaptureFormat`）に退避→完全復元。タスク終了処理。`SuspendRedraw`/`ResumeRedraw` でちらつき抑制。
- `MediaFoundationCaptureService.cs` — MF。**同期 ReadSample + `_generation` 世代管理**。Stop は renderer 同期 Dispose + Source/Reader を別スレッド後片付け。`ActualFps`（実測）/`NominalFps`（公称）。`FatalError` イベント。
- `DirectShowCaptureService.cs` — OpenCV。`VideoCapture(index, DSHOW)`、`Cv2.CvtColor(BGR→BGRA)` → `Nv12Renderer.UpdateBgra`。Stop は **完全同期**（ReadLoop Join → renderer Dispose → cap Release/Dispose。OpenCV は別スレッド遅延 Release だと二重 Open でデッドロック）。`EnumerateFormats` で DS 解像度列挙＋プリセット補完。`ActualFps`/`NominalFps`。
- `Nv12Renderer.cs` — D3D11 描画（NV12/BGRA 両対応）。
- `DeviceCache.cs` — デバイス/解像度キャッシュ。
- `CaptureTypes.cs` — `ICaptureService` / `DeviceInfo` / `CaptureFormat`(IEquatable)。
- `AppSettings.cs`（canonical JSON 差分セーブ）/ `Logger.cs` / `IconLoader.cs` / `StatusOverlay.cs` / `BusyDialog.cs`。

---

## 3. ビルド・発行（毎回これに従う）

- **実装環境**: Alpine Linux（WSL2 可）。**ターゲット**: Windows 10/11 x64。
- Alpine の `dotnet8-sdk` には WindowsDesktop SDK が無い。**Microsoft 公式 SDK が `/opt/dotnet-ms` に配置済み**。これを使う。csproj に `<EnableWindowsTargeting>true</EnableWindowsTargeting>` 必須。`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`（D3D11 テクスチャの unsafe コピー用）。
- ビルド／発行は必ず DOTNET_ROOT を通す:
  ```sh
  DOTNET_ROOT=/opt/dotnet-ms PATH=/opt/dotnet-ms:$PATH dotnet build -c Release
  rm -rf bin obj Release
  DOTNET_ROOT=/opt/dotnet-ms PATH=/opt/dotnet-ms:$PATH dotnet publish -c Release -o Release
  ```
- 主要 NuGet: `Vortice.MediaFoundation`/`Vortice.Direct3D11`/`Vortice.DXGI`/`Vortice.D3DCompiler`、`DirectShowLib.Standard`（**デバイス/解像度列挙のみ**）、`OpenCvSharp4.Windows`（DS-only 取得。ネイティブ同梱）。
- 発行物は `Release/` に **`MediaPreviewSan.exe`（単一ファイル・約 110MB）+ `LICENSE` + `THIRD-PARTY-NOTICES.txt`**。csproj の `CopyLicenseFiles` ターゲット（AfterTargets=Publish）が後者2つを自動コピーする。
- アイコン `app.ico`（マルチ解像度）は csproj の `<ApplicationIcon>` + `<EmbeddedResource>`。生成は ImageMagick の **`magick`**（`convert` は IM7 で非推奨）。**oklch は ImageMagick 非対応**なので sRGB 値を自前計算して渡す。
- 実行・動作確認は Windows 側のみ。Windows 固有 API は Linux ビルドではエラーにならないが実行不可。ログは exe と同階層の `MediaPreviewSan.log`。
- Vortice の正確な API シグネチャは `~/.nuget/packages/.../*.xml` か、別プロジェクトでリフレクション（`net8.0` 非 Windows でメタデータ読み）して確認すると速い。

---

## 4. ハマりポイント（最重要・次回これを先に読む）

過去ここで数十回の手戻りが発生した。原因と確定した対処を記す。

- **Vortice の非同期 SourceReader は使えない**: `ReadSample` が out 引数必須で、非同期モード（out 引数 NULL 必須）と矛盾し `0x80070057 E_INVALIDARG`。→ 同期 ReadSample + `_generation` 世代管理で実装する。
- **解像度変更時の `0x80070005 E_ACCESSDENIED` の真因は MF Source ではなく SwapChain**: 旧 `Nv12Renderer` の SwapChain が panel HWND を握ったまま新 `CreateSwapChainForHwnd` を呼ぶと E_ACCESSDENIED。→ Stop で **renderer のみ同期 Dispose**（SwapChain/HWND 即解放）。Source/Reader は別スレッド後片付けで可。「2 秒待つ」等の遅延回避策は不要かつ誤り。
- **ハングの真因は Join/Shutdown のブロッキング**: 同期 ReadSample を UI スレッドで Join するとデバイス次第でデッドロック。Stop は UI を一切ブロックしない（世代++のみ即リターン、後片付けは Task）。`Nv12Renderer.Dispose` は `WaitHandle.WaitAny` で描画スレッドが即抜けるので同期で安全。
- **OpenCV(DS) の Stop は逆に完全同期が必要**: `cap.Release` を別スレッド遅延にすると、次の Start が同一デバイスを二重 Open してデッドロック。ReadLoop Join → renderer Dispose → cap Release/Dispose の順で同期。
- **OBS 等仮想カメラは MF からは見えない**: `MFEnumDeviceSources` に出ない（DShow フィルタのみ）。SampleGrabber 経由も frames=0（NullRenderer は pull しない、VMR9/IC/IVideoWindow も不可）。**OpenCvSharp の `VideoCapture(CAP_DSHOW)` が唯一実用解**（OpenCV が内部で堅牢な DShow グラフを組む。Chrome 等と同じ理屈）。
- **Web カメラがデバイス一覧に出ない**: `MFEnumVideoDeviceSources()` 引数なしだと出ないことがある。`MFEnumDeviceSources(attr)` で `SourceType=VidCap` を明示。`KSCATEGORY_CAPTURE` は **オーディオデバイスまで含む**ので列挙に使わない（17 件のマイクが混入したバグ）。`default` + `KSCATEGORY_VIDEO_CAMERA` を使う。MF で出ない物理デバイスは DsDevice 列挙で補完。プライバシー設定 OFF だと MF が列挙しないので、0 件時は「設定→プライバシー→カメラ→デスクトップアプリ許可」を案内表示する。
- **カメラ FPS にディスプレイ全体が引きずられる**: ReadSample スレッドから直接 Present するのが原因。描画専用スレッドに分離 + Waitable SwapChain + `SetMaximumFrameLatency(1)` で根治。
- **黒画面**: フルスクリーン三角形が CCW で既定バックフェイスカリングに当たる。RasterizerState `CullMode=None`。
- **NV12 が崩れる**: D3D11 NV12 SRV は Format で plane 自動選択（`R8_UNorm`=Y / `R8G8_UNorm`=UV）。`PlaneSlice` フィールドは Vortice の `Texture2DShaderResourceView` に無い。
- **再検出で選択がリセットされる**: ①`_suppressDeviceChange=false` を LoadFormats の前に戻すと、await 中の遅延 `SelectedIndexChanged` が並行 LoadFormats を起動し復元を上書き → suppress を LoadFormats 完了まで維持。②復元先を `_settings` 直参照にすると保存値へ巻き戻る → 再検出時は再検出前 UI 選択を `_restore*` に退避し復元。③FPS は小数（29.97 等）があり緩い比較だと別エントリへズレる → `CaptureFormat.Equals`（Abs<0.01）で完全一致。
- **設定が 1 秒 30 回保存される**: SizeChanged/LocationChanged の連発。500ms デバウンス + FormClosing で強制保存。`AppSettings` は Load 時に canonical JSON を保持し差分時のみ書く。
- **ビジーダイアログが左上にチラつく**: Show 後に位置設定していたため。**Show 前に Location 確定**。
- DS-only デバイスでも解像度/補間/アス比は有効（OpenCV.Set + Nv12Renderer BGRA で適用可能）。「DS だから設定不可」にしない。

---

## 5. ライセンス（配布時）

- 本体: `LICENSE`（MIT, `Copyright (c) 2026 senamih`）。
- 依存: `THIRD-PARTY-NOTICES.txt`。MIT（.NET / Vortice / SharpGen）、Apache-2.0（OpenCvSharp / OpenCV）、**LGPL-2.1（DirectShowLib.Standard）**。
- LGPL-2.1 は NuGet 動的参照＋ソース入手先明示で条件充足。配布時は exe と同フォルダに `LICENSE` と `THIRD-PARTY-NOTICES.txt` を必ず同梱（csproj が自動コピー）。これによりアプリ本体は MIT 等任意ライセンスで配布可能。
