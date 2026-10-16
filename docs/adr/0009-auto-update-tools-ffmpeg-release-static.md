# 外部ツールは起動時に自動で更新し、ffmpeg は release ブランチ追従の static ビルドを使う

起動時に、yt-dlp（`yt-dlp -U`、今まで通り）→ ffmpeg → deno → TwitchDownloaderCLI の順に、1つずつ確認して、新しい版があれば更新まで行う。設定画面にオン・オフは足さない。更新の間は `QueueProcessingState.Updating` で、「開始」とライブの自動開始は予約になり、更新が終わった時点で始まる。

- **ffmpeg（ffmpeg.exe・ffprobe.exe。ffplay.exe は同じフォルダにあるときだけ）：** `BtbN/FFmpeg-Builds` の release ブランチ追従の static ビルド（gpl）。最近の `autobuild-*` リリース（最大10件）のうち、ブランチ `X.Y` が最大のものを選ぶ。版番号（`X.Y.Z`）が上がったときだけ更新する。`ffmpeg -version` の `n<X>.<Y>[.<Z>]` が読めない版（master の `N-126313-g…` など）は「更新が必要」として、release の static ビルドに置き換える。ハッシュは GitHub API のアセットの `digest`（SHA-256）で検証する。
- **deno：** PATH の先頭にある `deno.exe` で `deno upgrade` を実行する（yt-dlp が使うもの）。WinGet 側など、後ろのものは対象外。
- **TwitchDownloaderCLI：** GitHub の最新リリースの `TwitchDownloaderCLI-<タグ>-Windows-x64.zip` から `TwitchDownloaderCLI.exe` を入れ替える。ハッシュは `digest` で検証する。
- **入れ替え：** 入れ替え先と同じフォルダの `.ytgui-update\<ツール名>`（更新用フォルダ）にダウンロードして展開し、新しい exe を `--version` で実行して確かめてから、名前の変更で入れ替える。旧版は `<名前>.old` として1世代だけ残す。ffmpeg の3ファイルは1組として扱い、途中で失敗したら全部を元に戻す。
- **失敗：** ネットワーク・ハッシュ不一致・書き込み不可・exe が使用中などは、実行ログへの警告にとどめ、その項目だけ飛ばす（ステータスには触れない。ADR 0001 と同じ考え方）。再試行は次回の起動時。
- **AtomicParsley は対象外：** 実行ログに `[EmbedThumbnail] mutagen` と出ていて、yt-dlp 内蔵の mutagen が使われており、使われていないようなので。

理由は、アプリが更新まで行うなら、shared ビルドは exe と DLL を1組で入れ替える必要があり、DLL 名にメジャーバージョンが入る（`avcodec-63` → `-64`）ため古い DLL が残るから。static は exe の入れ替えだけで済む。release を選んだのは、master を追うと毎日出る新しいビルド（約194MB）を取り直すことになるため。

## Considered Options

- **yt-dlp/FFmpeg-Builds：** README に「Currently, no patches are being applied to the builds.」とあり、配布しているのは master（`N-…`）だけ（2026-10-16 に全リリースを確認）。yt-dlp 専用の調整が今は無いので、release を使いたいという条件に合わない。
- **Gyan の release-essentials：** 正式リリースだが、BtbN と同じ GitHub の `digest` で検証できる BtbN のほうが、仕組みが1つで済む。
- **毎回、新しいビルドへ更新する：** 毎日 194MB のため却下。
- **更新中は「開始」を無効にする：** 更新が長引くと操作が止まって見えるため、予約にした。
- **deno は WinGet 側も更新する：** winget の管理に任せる。
- **shared ビルドのまま、DLL も含めて更新する：** 上記の理由で却下。

## Consequences

- 初回の起動で、master・shared の ffmpeg が release の static ビルドに置き換わる。`.old`（旧 exe）は1世代だけ残り、旧 DLL は残る（案内のログだけ）。`n` の無い形（Gyan のビルドなど）の ffmpeg も「読めない版」なので、置き換わる。
- ffmpeg の3ファイルが約500MBで、`.old` を含めると更に増える。
- release ブランチが新しくなる（n9.0 → n10.0）と自動で切り替わる。互換性は yt-dlp 側の追従に頼る。
- `TwitchDownloaderCLI --version` は、バージョンを標準エラーにだけ出し、終了コード1を返す（2026-10-16、1.56.5）。判定は出力で行い、終了コードを見ない。
- 起動時の更新が長引くと、その間はキュー処理の開始（ライブの自動開始を含む）が予約のまま待たされる。ダウンロードの打ち切りは60分、deno は10分。回線が遅い（約55KB/s 未満）と、起動のたびに待ったうえで失敗する。ライブを `--no-live-from-start` で録る場合は、その間の配信を取りこぼす。更新を飛ばす手段は無い。
- `yt-dlp -U` の呼び出し（`YtDlp.RunAsync`）には時間切れが無い。`-U` が止まったままだと、キュー処理の開始は予約のまま始まらず、アプリを再起動しても同じになる。今までは `-U` と並行して「開始」が使えた。yt-dlp 側にソケットの時間切れがあるので、実際に止まるのはまれと見ている（推測）。上限は設けず、今のまま受け入れる（2026-10-17 にユーザーが決定）。
- アプリを閉じるときは、ffmpeg・deno・TwitchDownloaderCLI の更新のダウンロード・展開・外部プロセスを止め、更新用フォルダの片付けを最大3秒待つ。間に合わなかった分は、次回の起動時の更新の前に消す。`yt-dlp -U` は、yt-dlp 自身の更新を途中で殺すと壊れるおそれがあるので、止めない。`deno upgrade` は強制終了する。deno 自身が入れ替えの途中だと壊れるおそれがある（まれ）。壊れた場合は `deno upgrade` を実行できないので、手で入れ直す。
- 更新の間も、チャンネル事前投入（`ChannelPrefetchTools`）や URL 追加（`GetVideoInfoAsync`）は止まらず、入れ替えの一瞬に exe が見つからず、その1件が失敗しうる。影響が小さいので受け入れる。
- GitHub API は認証なしで1時間60回まで。1回の起動で、このアプリが2回、`yt-dlp -U` を含めて3回前後使う。
- 更新用フォルダ（`.ytgui-update`）は、アプリのデータフォルダではなく、入れ替え先のツールのフォルダに作る（名前の変更で入れ替えるため、同じドライブに置く必要がある）。
- 巻き戻し（名前の変更）自体が途中で失敗すると、ffmpeg が見つからない、または3ファイルの版が混ざった状態が残りうる。次回の起動でも直らず、手で `.old` から戻す。名前の変更の失敗はまれなので、受け入れる。
