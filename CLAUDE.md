# CLAUDE.md

yt-dlpのGUIラッパー（WinForms、`net8.0-windows`）。URLをキューに積み、動画または音声を順にダウンロードする。オプションで、チャットリプレイの取得と、絵文字・エモートのキャッシュの事前投入も行う。再生や表示の機能は持たない（純粋なダウンローダー）。

- 用語：[CONTEXT.md](CONTEXT.md)（キュー項目・ステータス・動画取得・チャット取得などの区別が厳密）
- 設計判断：[docs/adr/](docs/adr/)。チャット取得の失敗とステータスの関係、Twitchの逐次実行、`--sub-langs live_chat` について書いてある。チャット取得まわりを変える前に読む。
- 絵文字キャッシュの要件：[動画ダウンローダー_絵文字キャッシュ事前投入機能_要件定義書.md](動画ダウンローダー_絵文字キャッシュ事前投入機能_要件定義書.md)

## コマンド

```bash
dotnet build yt.slnx
dotnet run --project YtGui/YtGui.csproj
dotnet publish YtGui/YtGui.csproj -c Release -o publish/<フォルダ名>
```

テストプロジェクトは無い。純粋なロジックは、スクラッチに作るハーネスで検証する（`~/.claude/rules/csharp-general.md` 項目14）。起動中の `YtGui.exe` があると再ビルドに失敗するので、終了してよいか確認してからビルドする。

## 構成

| ファイル | 責務 |
|---|---|
| `YtGui/Program.cs` | `MainForm` と `QueueItem`。キュー処理（`ProcessQueueAsync` → `RunYtDlpAsync` → `ProcessChatReplayIfRequestedAsync` → `ProcessEmojiCacheIfRequestedAsync`）、stdoutからの進捗の解析、`ListView` のオーナードロー |
| `YtGui/YtDlp.cs` | yt-dlpの呼び出し、URLの正規化、サイト種別の判定、出力パスの組み立て |
| `YtGui/EmojiCache.cs` | チャットJSONから絵文字・エモートのURLを抽出してダウンロードし、`emoji_cache.sqlite` に投入する |
| `YtGui/FormatSelectionForm.cs`・`MediaFormat.cs` | フォーマット一覧の解析と選択画面 |
| `YtGui/Settings.cs`・`SettingsForm.cs` | 設定は `%APPDATA%\YtGui\settings.json`。yt-dlp・ffmpeg・Twitchチャットツールのパス、出力先、絵文字キャッシュの出力先など |

## コードから読み取れない不変条件

- **ステータスを決めるのは、動画取得の成否だけ。** チャット取得や絵文字キャッシュの失敗は、ログへの警告にとどめる（ADR 0001）。
- **Twitchは、動画取得（yt-dlp）が終わってからチャット取得（TwitchDownloaderCLI の `chatdownload`）を行う。** キュー処理は「1項目＝1プロセスを待つ」前提で作られている（ADR 0002）。
- **YouTubeのチャットは `--write-subs --sub-langs live_chat` で取る**（`--write-live-chat` はyt-dlpから削除された。ADR 0003）。yt-dlpの仕様は変わるので、この連携を変えたら、実際にフルダウンロードして確かめる（`--skip-download` だけで確かめない）。
- **後の処理が出力ファイル名を書き換えることがある**（ライブの後処理での衝突回避など）。そこから導くパスは、書き換えより前に確定させる。
- **進捗の表示**
  - 1回の実行の中でも、stdoutの行の形式が変わる（`%` 付き ↔ `%` 無し）。表示モードに入る条件と出る条件は、対で実装する。
  - 終端のステータス（完了・キャンセル・失敗）は、文字列で表示する。
- **`await` の後にキュー項目を書き換えるときは、事前に `allItems.Contains(item)` を確かめる。**
- **`emoji_cache.sqlite` はvlc-chatと共有する契約。** 次のものを変えるときは、両方のリポジトリを揃える。
  - テーブル：`emoji_cache(url TEXT PRIMARY KEY, data BLOB NOT NULL)`
  - TwitchのエモートURLの形：`https://static-cdn.jtvnw.net/emoticons/v2/{id}/default/dark/2.0`
  - 読む側：`../vlc-chat/Services/EmojiCacheService.cs`
- **キャッシュへの書き込み**
  - URLは、JSONから取り出した値をそのまま使う（`Trim` や再エンコードはしない）。
  - BLOBには `byte[]` をそのまま入れる。
  - 書き込みは1件ずつ `try/catch` し、ダウンロードが全部終わってから1本の接続で順に行う。
