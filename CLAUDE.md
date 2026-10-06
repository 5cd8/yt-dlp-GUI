# CLAUDE.md

yt-dlpのGUIラッパー（WinForms、`net8.0-windows`）。URLをキューに積み、動画または音声を順にダウンロードする。オプションで、チャットリプレイの取得と、絵文字・エモートのキャッシュの事前投入も行う。再生や表示の機能は持たない（純粋なダウンローダー）。

- 用語：[CONTEXT.md](CONTEXT.md)（キュー項目・ステータス・動画取得・チャット取得などの区別が厳密）
- 設計判断：[docs/adr/](docs/adr/)。チャット取得の失敗とステータスの関係、Twitchの逐次実行、`--sub-langs live_chat` について書いてある。ライブ録画のチャットを録画の後にアーカイブから取る理由（0005）も。チャット取得まわりを変える前に読む。一時フォルダを使うときに出力先を `-P` で渡す理由（0006）も。
- 絵文字キャッシュの要件：[動画ダウンローダー_絵文字キャッシュ事前投入機能_要件定義書.md](動画ダウンローダー_絵文字キャッシュ事前投入機能_要件定義書.md)
- 絵文字キャッシュの検証用ビューア：https://github.com/5cd8/emoji-cache-viewer（別リポジトリ。ローカルは `../emoji-cache-viewer`）。`emoji_cache.sqlite` を読み取り専用で開き、絵文字を画像の一覧で表示する。絵文字キャッシュ投入（キュー項目・フォルダ一括投入・チャンネル事前投入）で絵文字が入ったかを確かめるときに使う。このリポジトリの一部ではない。

## コマンド

```bash
dotnet build yt.slnx
dotnet run --project YtGui/YtGui.csproj
dotnet publish YtGui/YtGui.csproj -c Release -o publish/<フォルダ名>
```

`publish` のときだけ、`YtGui.csproj` の設定でランタイム同梱の単一ファイル（`YtGui.exe` 1つ）になる。`build`・`run` の出力先は変わらない。設定・ログは実行時に、exeの隣の `data\` に作られる。

テストプロジェクトは無い。純粋なロジックは、スクラッチに作るハーネスで検証する（`~/.claude/rules/csharp-general.md` 項目14）。起動中の `YtGui.exe` があると再ビルドに失敗するので、終了してよいか確認してからビルドする。

## 構成

| ファイル | 責務 |
|---|---|
| `YtGui/Program.cs` | `MainForm` と `QueueItem`。キュー処理（`ProcessQueueAsync` → `RunYtDlpAsync` → `FinalizeLiveOutputFileAsync` → `ProcessChatReplayIfRequestedAsync`（ライブは `FetchYouTubeLiveChatReplayAsync`）→ `ProcessEmojiCacheIfRequestedAsync`）、キュー処理の開始・停止・終わりの受け取り（`StartProcessing`・`StopProcessing`・`OnQueueProcessingFinished`）、stdoutからの進捗の解析、`ListView` のオーナードロー。フォルダ一括投入（`StartFolderBulkEmojiCacheAsync` → `RunFolderBulkEmojiCacheAsync`）、チャンネル事前投入（`StartChannelPrefetchAsync` → `RunChannelPrefetchAsync`）。両者が共有する実行の枠は `RunEmojiCacheJobAsync` |
| `YtGui/YtDlp.cs` | yt-dlpの呼び出し、URLの正規化、サイト種別の判定、出力パスの組み立て、出力先・並列数の引数の組み立て |
| `YtGui/EmojiCache.cs` | チャットJSONから絵文字・エモートのURLを抽出してダウンロードし、`emoji_cache.sqlite` に投入する。フォルダ一括投入のための、チャットJSONの形式判定（`DetectChatSiteKind`）・列挙（`FindChatReplayFiles`）と、複数ファイルへの投入（`PopulateFilesAsync`） |
| `YtGui/ChannelPrefetch.cs` | チャンネル事前投入のUIに依存しない部分。チャンネルURLの解釈、アーカイブ一覧の行の解釈とN件での打ち切り（`ArchiveListingCollector`）、処理済みアーカイブの記録（`ProcessedArchiveStore`）、全体の流れ（`RunAsync`） |
| `YtGui/ChannelPrefetchTools.cs` | チャンネル事前投入で外部プロセス（yt-dlp・TwitchDownloaderCLI）を呼ぶ。アーカイブ一覧の取得と、チャットリプレイの取得。キュー項目の `ActiveCts` には触らない |
| `YtGui/ThumbnailEmbedder.cs` | ライブ録画の仕上げで、ffmpeg を使って動画にサムネイルを埋め込む。トークンで止められる |
| `YtGui/LivePartMerger.cs` | ライブ録画の仕上げで、映像と音声に分かれた途中ファイル（`<名前>.f<ID>.<拡張子>` か `<名前>.f<ID>.<拡張子>.part`）を探し、ffmpeg で再エンコードせずに結合し、結合できたら途中ファイルを消す。トークンで止められる |
| `YtGui/ExecutionLog.cs` | 実行ログ（画面下部のログ欄）への追記と、古い行の切り詰め。yt-dlp の出力のうち、ffmpeg の雑音の行の判定。ログ欄のテキストは `ExecutionLogWriter` だけが書き換える |
| `YtGui/LiveChatReplay.cs` | ライブ録画のチャットリプレイを録画の後に取るときの、UIに依存しない判定と組み立て（対象かどうか、「チャット未取得」の表示、右クリックでキューに入れてよいか、yt-dlp の引数） |
| `YtGui/QueueProcessingLifecycle.cs` | キュー処理の状態（動いていない・動いている・停止中）と、自動開始を予約するか、キュー処理の終わりに次を始めるかの、UIに依存しない判定 |
| `YtGui/FormatSelectionForm.cs`・`MediaFormat.cs` | フォーマット一覧の解析と選択画面 |
| `YtGui/DataDirectory.cs` | データフォルダ（設定・エラーログ・処理済みアーカイブの記録を置く）の決定と、`%APPDATA%\YtGui\` からの移行 |
| `YtGui/Settings.cs`・`SettingsForm.cs` | 設定は `<実行ファイルのフォルダ>\data\settings.json`（書き込めないときだけ `%APPDATA%\YtGui\`）。yt-dlp・ffmpeg・Twitchチャットツールのパス、出力先、絵文字キャッシュの出力先、チャンネルURL・件数N、一時フォルダ・並列数など |

## コードから読み取れない不変条件

- **ステータスを決めるのは、動画取得の成否だけ。** チャット取得や絵文字キャッシュの失敗は、ログへの警告にとどめる（ADR 0001）。
- **Twitchは、動画取得（yt-dlp）が終わってからチャット取得（TwitchDownloaderCLI の `chatdownload`）を行う。** キュー処理は「1項目＝1プロセスを待つ」前提で作られている（ADR 0002）。
- **キュー処理（`ProcessQueueAsync`）は同時に1つだけ動かす。** 始めるのは `StartQueueProcessingLoop` だけで、停止中（`QueueProcessingState.Stopping`）の間は始めない（停止中に予約された開始は、停止が完了した時点で `OnQueueProcessingFinished` が始める）。キュー処理の終わりは `OnQueueProcessingFinished` が UIスレッドで受け取り、次を始めるか（停止中に予約された開始、終わる直前に入った項目）を決める。キュー処理を自動で始める操作は、`StartProcessing` ではなく `StartOrReserveProcessing` を呼ぶ。
- **YouTubeのチャットは `--write-subs --sub-langs live_chat` で取る**（`--write-live-chat` はyt-dlpから削除された。ADR 0003）。yt-dlpの仕様は変わるので、この連携を変えたら、実際にフルダウンロードして確かめる（`--skip-download` だけで確かめない）。
- **YouTubeのライブ録画では、録画と同じyt-dlpでチャットを取らない**（ADR 0005）。録画の後に、別のyt-dlp（`--skip-download`）でアーカイブのチャットリプレイを取る。取れたかどうかは、終了コードではなくファイルの有無で決める（チャットリプレイがまだ無いと、終了コード0でファイルを作らないため）。上の「`--skip-download` だけで確かめない」は、動画とチャットを1つの yt-dlp で取る経路の話。ライブ録画のチャットは `--skip-download` が本番の経路なので、`--skip-download` の実行で確かめてよい。
- **フォルダ一括投入とチャンネル事前投入は、キューの「停止」・「選択を中止」では止まらない。** 専用のトークン（`emojiCacheCts`）を持ち、止めるのは「絵文字キャッシュ ▼」ボタンの「中止」とアプリの終了だけ。このトークンは、フォルダ一括投入とチャンネル事前投入が同時に1つだけ動くことの印も兼ねる（キュー項目の絵文字キャッシュ投入とは無関係に並行して動く）。
- **チャンネル事前投入の「N件」は、YouTubeでは `was_live` の枠だけを新しい順に数え、処理済みで飛ばした枠も数に入れる。** 一覧は `--playlist-end (N + ListingMargin)` で絞り、yt-dlp を自然終了させる（`Kill` で止めると `%TEMP%\_MEI*` が毎回残る）。`--playlist-end N` だけにしない（先頭の配信中・配信予定の枠が数に入るため）。
- **チャンネル事前投入は、アーカイブごとに取得と投入が両方終わってから処理済みに記録する**（データフォルダの `prefetched_archives.txt`）。取得できたかはファイルの有無で決める（ADR 0005）。絵文字の失敗件数に関わらず記録する（`PopulateAsync` は新規分だけを取るので、失敗で未処理に戻すと毎回チャット全体を取り直すため）。例外が出たときだけ記録しない。
- **アプリが書き出す3ファイル（`settings.json`・`last_error.log`・`prefetched_archives.txt`）の置き場は、`Settings.GetDataDirectory()` だけが決める。** 通常は `<実行ファイルのフォルダ>\data`。そこに書き込めないとき（実際に一時ファイルを作って確かめる）だけ `%APPDATA%\YtGui\`。判定は実行中に1回。移行は、起動のたびに旧フォルダから `data` に無いファイルだけをコピーする（元は消さない。`data` 側を消すと旧フォルダの内容が戻るので、リセットするには両方を消す）。新しいファイルを書き出す機能を足すときは、パスを自前で組み立てず、このフォルダを使う。
- **動画取得の出力先は、一時フォルダを設定したときだけ `-P home:`・`-P temp:` ＋ファイル名だけの `-o` で渡す**（絶対パスの `-o` だと `-P` が無視される。ADR 0006）。ライブ録画には一時フォルダを渡さない（仕上げが出力先の途中ファイルを探すため）。`-N`（並列数）も渡さない（Issue #34の確定事項）。失敗・中止で一時フォルダに残った途中のファイル（`.part` 等）は消さない（再実行・リトライの再開に使うため）。
- **後の処理が出力ファイル名を書き換えることがある**（ライブの後処理での衝突回避など）。そこから導くパスは、書き換えより前に確定させる。
- **ライブ録画の仕上げは、yt-dlp が残した途中ファイルの形で分岐する。** `<名前>.mp4.part` が1つなら改名、`<名前>.mp4` があれば何もしない（yt-dlp が完成させた）、`<名前>.f<ID>.<拡張子>`（設定で `.part` を使うときは末尾に `.part`）に分かれていれば結合（「配信開始から録画」ONで止めた場合。yt-dlp が映像と音声を並行して取るので、長さは揃わず、短いほうに合わせる）。「配信開始から録画」の設定値では分岐しない（分かれ方は yt-dlp が決めるため）。
- **進捗の表示**
  - 1回の実行の中でも、stdoutの行の形式が変わる（`%` 付き ↔ `%` 無し）。表示モードに入る条件と出る条件は、対で実装する。
  - 終端のステータス（完了・キャンセル・失敗）は、文字列で表示する。
- **`await` の後にキュー項目を書き換えるときは、事前に `allItems.Contains(item)` を確かめる。**
- **項目の中止（「選択を中止」「削除」）が止めるのは、`item.ActiveCts` のトークンと `item.ActiveProcPid` のプロセスだけ。** キュー全体の `token` を渡しているだけの処理は止まらない。動画取得の後に止められる処理を足すときは、`CreateLinkedTokenSource` で作ったトークンを `item.ActiveCts` に入れ、そのトークンを処理に渡す。
- **`emoji_cache.sqlite` はvlc-chatと共有する契約。** 次のものを変えるときは、両方のリポジトリを揃える。
  - テーブル：`emoji_cache(url TEXT PRIMARY KEY, data BLOB NOT NULL)`
  - TwitchのエモートURLの形：`https://static-cdn.jtvnw.net/emoticons/v2/{id}/default/dark/2.0`
  - 読む側：`../vlc-chat/Services/EmojiCacheService.cs`
- **キャッシュへの書き込み**
  - URLは、JSONから取り出した値をそのまま使う（`Trim` や再エンコードはしない）。
  - BLOBには `byte[]` をそのまま入れる。
  - 書き込みは1件ずつ `try/catch` し、ダウンロードが全部終わってから1本の接続で順に行う。
