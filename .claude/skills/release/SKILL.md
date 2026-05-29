---
name: release
description: MediaPreviewSan のリリース（バージョニング → 2版発行 → コミット/タグ push → GitHub Release）を実施する。ユーザーが「リリースして」「vX.Y.Z でリリース」「バージョン上げて push/リリース」等と言ったときに使う。直近 v1.1.0（.NET 10 移行 + FD 版追加）リリースで得た手順・ハマりどころを反映した実行ガイド。
---

# MediaPreviewSan リリース手順スキル

CLAUDE.md の「リリース手順（バージョン管理）」を、実作業で踏んだ落とし穴込みで手順化したもの。
**真実の源は CLAUDE.md**。齟齬があれば CLAUDE.md を優先し、本スキルを直す。

## 大前提（毎回守る）

- ビルド／発行は必ず公式 SDK を明示： `DOTNET_ROOT=/opt/dotnet-ms PATH=/opt/dotnet-ms:$PATH` を前置きする。システム既定 dotnet（apk 版）は WindowsDesktop SDK を含まないので使わない。
- ターゲットは **.NET 10**（`net10.0-windows`）。SDK は `/opt/dotnet-ms` に 10.0.x が導入済み。
- 発行物は **2 種類の単一 EXE**：`MediaPreviewSan.exe`（自己完結・約110MB）と `MediaPreviewSan-fd.exe`（ランタイム非同梱・約95MB）。
  - **FD 版でもサイズ差は約15MB だけ**。本アプリは OpenCV のネイティブライブラリ（約95MB）を自己完結/FD どちらでも同梱必須で、削れるのは .NET ランタイム分のみ。「FD 版なら数 MB」にはならない点をユーザーに正しく伝える。
- **exe の実動作確認は Windows 側でのみ可能**。Linux（Alpine/WSL2）上ではビルド・発行・公開までしかできない旨を必ずユーザーに明示する（特に FD 版は .NET 10 デスクトップランタイム必須）。
- **タグ push と GitHub Release 公開は不可逆な外部公開**。実行直前に必ずユーザーの最終確認を取る（CLAUDE.md の指示）。

## バージョン番号の決め方（SemVer）

CHANGELOG の `[Unreleased]` 内容から判断する：
- 後方互換の **機能追加**（例: FD 版同梱）があれば **MINOR**（例 1.0.0 → 1.1.0）。
- **修正のみ**なら **PATCH**。
- settings.json 形式の非互換化など **互換を壊す変更**は **MAJOR**。
- .NET ランタイムのメジャー更新だけでは MAJOR にしない（自己完結版は単体で動くため後方互換は壊れない。実際 v1.1.0 の .NET 10 移行は MINOR 扱いとした）。
- 迷ったら採用案を提示しつつユーザーに確認。

## 手順

### 0. 事前確認
```sh
git status                  # 作業ツリーの差分を把握
git tag | tail -n 5         # 直近タグ
grep -n "<Version>" MediaPreviewSan.csproj
which gh && gh auth status   # gh 未導入/未認証なら Release 自動作成は不可（後述）
```

### 1. CHANGELOG.md 更新
- `[Unreleased]` の見出し直後に `## [X.Y.Z] - YYYY-MM-DD`（実日付）を挿入し、内容をそこへ移す。`[Unreleased]` は空のまま残す。
- 末尾の compare リンクを更新：
  - `[Unreleased]: .../compare/vX.Y.Z...HEAD`
  - `[X.Y.Z]: .../compare/v(前版)...vX.Y.Z` を追加。

### 2. csproj のバージョンを 3 つ揃える
本プロジェクトの csproj は `<Version>`/`<FileVersion>`/`<InformationalVersion>` を **直書きで 3 つ**持つ（`$(Version)` 派生ではない）。**3 つとも同じ番号へ**書き換える。

### 3. クリーン発行（2版）— ★順序とフラグが肝
```sh
rm -rf bin obj Release /tmp/pub-fd
DOTNET_ROOT=/opt/dotnet-ms PATH=/opt/dotnet-ms:$PATH dotnet build -c Release   # 警告0/エラー0 を確認
# ① 自己完結版を Release へ（付随4ファイルは CopyDistFiles ターゲットが配置）
DOTNET_ROOT=/opt/dotnet-ms PATH=/opt/dotnet-ms:$PATH dotnet publish -c Release -o Release
# ② FD 版を一時ディレクトリへ
DOTNET_ROOT=/opt/dotnet-ms PATH=/opt/dotnet-ms:$PATH dotnet publish -c Release -o /tmp/pub-fd \
  --self-contained false -p:SelfContained=false \
  -p:EnableCompressionInSingleFile=false -p:AssemblyName=MediaPreviewSan-fd
# ③ FD exe だけ Release へ追加
cp /tmp/pub-fd/MediaPreviewSan-fd.exe Release/ && rm -rf /tmp/pub-fd
ls -la Release   # exe 2 つ + LICENSE/THIRD-PARTY-NOTICES.txt/README/CHANGELOG が揃うこと
```
- **`dotnet publish -o Release` は出力先を毎回クリーンする＝後勝ち**。同一フォルダへ 2 回発行すると一方が消える。だから FD は一時発行→コピー。FD は付随ファイルをコピーしない（自己完結版発行時のものをそのまま使う）。
- FD で `EnableCompressionInSingleFile=false` を外すと `NETSDK1176`（圧縮は自己完結時のみ可）で失敗する。
- `AssemblyName=MediaPreviewSan-fd` で `-fd.exe` が直接生成される（ログファイルも `MediaPreviewSan-fd.log` になる点に留意）。
- README/CHANGELOG を publish 後に編集したら、`Release/` 内の同名ファイルを `cp README.md CHANGELOG.md Release/` で最新へ差し替える（publish 時コピーは編集前の内容）。

### 4. コミット & push（master 直で運用）
本プロジェクトのリリースは履歴上 master 直コミット。
```sh
# ★ git add -A は使わない：未追跡の net10AndFd.md（指示書）やローカル zip 等を巻き込むため、
#    リリースに関係する変更ファイルだけを明示的に add する。
git add CHANGELOG.md MediaPreviewSan.csproj README.md CLAUDE.md   # 実際に変更したものに合わせる（Src/*.cs 等があれば追加）
git commit -m "Release vX.Y.Z" ...   # 末尾に Co-Authored-By: Claude を付与
git push origin master
```

### 5. タグ & GitHub Release —（不可逆：ここで最終確認を取る）
```sh
git tag -a vX.Y.Z -m "vX.Y.Z"
git push origin vX.Y.Z
# Release/ の発行物すべてを zip 化して添付（残骸を残さないよう /tmp に作る）
cd Release && zip -j /tmp/MediaPreviewSan-vX.Y.Z.zip \
  MediaPreviewSan.exe MediaPreviewSan-fd.exe LICENSE THIRD-PARTY-NOTICES.txt README.md CHANGELOG.md && cd ..
# CHANGELOG 該当版をノートに転記（2 種類の EXE 早見表 / Windows 限定 / FD はランタイム必須の注記も入れる）
gh release create vX.Y.Z /tmp/MediaPreviewSan-vX.Y.Z.zip --title "vX.Y.Z" --notes-file /tmp/relnotes.md
gh release view vX.Y.Z --json tagName,assets,url   # 確認
```

## gh が無い/未認証のとき
- `git push`・タグ push は SSH 鍵があれば可能だが、**GitHub Release 作成は gh か GITHUB_TOKEN が必須**。
- 無ければユーザーに選ばせる：(a) push+タグだけ実行し Release は手動（zip とノートは用意して渡す）／(b) `gh auth login` か `GH_TOKEN` 用意後に全自動／(c) ローカルまで。
- 必要 token scope は `repo`。

## 完了報告に必ず含める
- リリース URL、添付 zip 名と中身。
- **Windows 側でのみ動作確認可**（自己完結版の単体起動／FD 版は .NET 10 デスクトップランタイム導入機での起動）。
- 問題が出たら次は PATCH 版（vX.Y.Z+1）で再リリースになる旨。

## CLAUDELOG への追記を忘れない
CLAUDE.md の指示どおり、やりとり完了ごとに `CLAUDELOG.local.log` へ規定フォーマットで追記する。
