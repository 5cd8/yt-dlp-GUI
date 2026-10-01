# YouTubeのライブチャット取得は--write-live-chatではなく--sub-langs live_chatを使う

yt-dlp 2026.08.19では`--write-live-chat`オプションが削除されており、これを引数に含めて動画本編をダウンロードすると`yt-dlp.exe: error: no such option: --write-live-chat`（exit 2）で本編ダウンロードごと失敗する状態になっていた。yt-dlpは現在、ライブチャットを疑似言語コード`live_chat`の字幕として扱う方式に統一しており、`--write-subs --sub-langs live_chat`で同等の結果（`<basename>.live_chat.json`への1行1チャットイベントのJSON Lines出力）が得られることを実機（配信中のライブ・終了済みアーカイブ双方）で確認した。そのため`--write-live-chat`の呼び出しをこの新方式に置き換えた。

## Considered Options

- 古いyt-dlp（`--write-live-chat`がまだ使えるバージョン）との両対応（バージョン判定やエラー時フォールバックの追加）: 実機で確認できたのは現行バージョンのみで、過去バージョンへの互換性維持が実際に必要かどうかの根拠がないため却下。なお、チャットが存在しない動画に対して`--sub-langs live_chat`を指定した場合、yt-dlpは`There are no subtitles for the requested languages`という警告のみを出しexit 0で正常終了することを確認しており、未知の言語コードが要求された場合も同様に警告のみで動画ダウンロードは継続される可能性が高いと考えられる（この点は古いバージョンの実機では未検証の推測）。そのため、仮に古いyt-dlpを使っているユーザーがいても、本対応により新たにクラッシュが発生する可能性は低いと判断した。
