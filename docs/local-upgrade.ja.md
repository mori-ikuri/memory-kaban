# ローカルの bag を v1.0 から v1.1 に差し替える

この手順はぷゆんが実行するためのものです。スクリプトを用意しただけでは、
インストール済みの bag は変わりません。作業はリポジトリのルートから進めます。

- 差し替え対象: `%LOCALAPPDATA%\Programs\bag` のアプリ一式。
- 読み取り確認だけに使う既存DB: `C:\Pocket\bag\bag-v1.db`。
- `bag-v1.db`、元の書き出しZIP、`seals.json`、`bag.config.json` は移動・上書きしません。
- `update-local.ps1` にDBを指定する引数はありません。取り込みやDB移行も実行しません。
- 既存の Claude Desktop のMCP設定は、インストール先のパスが同じなら変更不要です。

## 1. v1.1のアプリ一式を一時フォルダに準備する

.NET 10 SDKを使います。PowerShellをリポジトリのルートで開き、以下を順に実行します。
ビルド出力は固有の一時フォルダへ出します。失敗したら、その先へ進めません。

```powershell
$ErrorActionPreference = 'Stop'
$work = Join-Path $env:TEMP ('memory-kaban-v11-' + [guid]::NewGuid().ToString('N'))
$artifacts = Join-Path $work 'artifacts'
$publish = Join-Path $work 'publish'

dotnet restore bag.slnx --locked-mode -p:UseArtifactsOutput=true -p:ArtifactsPath="$artifacts"
if ($LASTEXITCODE -ne 0) { throw 'restore failed' }

dotnet build bag.slnx -c Release --no-restore -p:UseArtifactsOutput=true -p:ArtifactsPath="$artifacts"
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

$env:BAG_TEST_ROOT = Join-Path $work 'tests'
dotnet test bag.slnx -c Release --no-build --no-restore -p:UseArtifactsOutput=true -p:ArtifactsPath="$artifacts"
if ($LASTEXITCODE -ne 0) { throw 'tests failed' }

dotnet publish src/Bag/Bag.csproj -c Release --no-restore --self-contained false -p:UseArtifactsOutput=true -p:ArtifactsPath="$artifacts" -o "$publish"
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

dotnet (Join-Path $publish 'bag.dll') --version
if ($LASTEXITCODE -ne 0) { throw 'version check failed' }
# 1.1.0 と表示されることを確認する。
```

ここでの `dotnet publish` はローカルにアプリ一式を出力する.NETのコマンドです。
GitHubや外部サービスへの公開は行いません。`bag.dll` だけを抜き出さず、
依存DLL・`runtimes`・言語別フォルダを含めた `publish` 全体を使います。

## 2. Claude Desktopを完全に終了し、予行確認する

ウィンドウを閉じるだけでなく、通知領域のClaudeアイコンからも終了します。
同じ bag を使うほかのMCPクライアントや、手動で起動した bag も終了します。
スクリプトはプロセスを強制終了しません。ファイル使用中なら中止して、
クライアントが終了していることを確認してからやり直します。

このPowerShellプロセス内だけでスクリプト実行を許可し、`-WhatIf` で予定を確認します。

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
& .\scripts\update-local.ps1 -PublishPath $publish -WhatIf
```

`-WhatIf` はインストール先・退避先・ステージング先を作成・変更しません。
通常の差し替えは、既存が1.0.0、準備したファイルが1.1.0であることを確認してから進みます。
別の版や不完全なアプリ一式なら中止します。

## 3. 退避して差し替える

```powershell
$upgrade = & .\scripts\update-local.ps1 -PublishPath $publish
if (-not $upgrade.Applied) { throw 'Upgrade was not applied' }
$upgrade | Format-List
```

表示された `BackupPath` を控えます。v1.0のフォルダを、インストール先と同じ親にある
固有名のフォルダへ退避してから、準備したv1.1を同じインストールパスに配置します。
旧版を上書きせず、退避先は自動削除しません。

```powershell
$backup = $upgrade.BackupPath
$install = Join-Path $env:LOCALAPPDATA 'Programs\bag'
dotnet (Join-Path $install 'bag.dll') --version
if ($LASTEXITCODE -ne 0) { throw 'installed version check failed' }
# 1.1.0 と表示されることを確認する。
```

処理が中断された場合は、表示された退避先と残ったフォルダを確認します。
上書きや手動削除で片付けず、下の復元手順を使います。

## 4. 再起動と、既存の鞄をそのまま読む最終確認

**Claude Desktopを再起動しないと、新しいMCPツールは見えません。**
完全に終了したあと起動し直し、`list_rooms`、`find`、`find_heads`、`get_messages`
の4つが見えることを確認します。設定は同じインストールパスと既存DBを指したままにします。
現れなければMCP設定のパスと、完全に終了できていたかを確認します。

最後に、差し替えたアプリで本物の鞄を**読み取り専用で**確認します。
これは実行者が行う確認であり、準備作業中には実行していません。

```powershell
$install = Join-Path $env:LOCALAPPDATA 'Programs\bag'
$db = 'C:\Pocket\bag\bag-v1.db'
if (-not (Test-Path -LiteralPath $db -PathType Leaf)) { throw 'Existing database not found' }
$before = (Get-FileHash -LiteralPath $db -Algorithm SHA256).Hash

$text = & dotnet (Join-Path $install 'bag.dll') find --db $db --limit 1 --json
if ($LASTEXITCODE -ne 0) { throw 'Existing database read failed' }
$found = ($text -join "`n") | ConvertFrom-Json
if ($found.schema -ne 'bag-find/1') { throw 'Unexpected response' }

$after = (Get-FileHash -LiteralPath $db -Algorithm SHA256).Hash
if ($before -ne $after) { throw 'Database changed during verification' }
[pscustomobject]@{ schema = $found.schema; message_count = $found.message_count; database_unchanged = $true }
```

成功終了・`bag-find/1`・`database_unchanged = True` が確認できれば、v1.0のDBを
作り直さずに読めています。0件でもエラーではありません。封印は通常どおり適用されます。
この確認中は別のプロセスでDBへ取り込まないでください。
v1.0で取り込んだ同一ZIPの再取り込みは、importer versionの保護により拒否されます。
読み取り確認に `import` を使う必要はありません。

## v1.0へ戻す

1. Claude Desktopと、同じbagを使うクライアントを完全に終了します。
2. 控えていたv1.0の退避先を指定して予行確認し、復元します。

```powershell
# 同じPowerShellセッションなら、上で取得した $backup をそのまま使える。
# 別のセッションでは、実際に表示されたBackupPathを設定する。
# $backup = 'C:\Users\YOUR_NAME\AppData\Local\Programs\実際の退避フォルダ名'

$ErrorActionPreference = 'Stop'
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
& .\scripts\update-local.ps1 -Rollback -BackupPath $backup -WhatIf
$restored = & .\scripts\update-local.ps1 -Rollback -BackupPath $backup
if (-not $restored.Applied) { throw 'Rollback was not applied' }
$restored | Format-List

$install = Join-Path $env:LOCALAPPDATA 'Programs\bag'
dotnet (Join-Path $install 'bag.dll') --version
if ($LASTEXITCODE -ne 0) { throw 'restored version check failed' }
# 1.0.0 と表示されることを確認する。
```

復元でも退避済みv1.0は残します。外したv1.1も別名で保存します。
切り替え中断でインストール先が存在しない場合にも、退避先から復元できます。
最後にClaude Desktopを再起動します。v1.0ではツールが `list_rooms` と `find` に戻ります。
DB・封印・話者設定はこの作業の対象外です。

## ChatGPTの実物が届いたら、別DBで試す

原本ZIPをそのまま使い、手でJSONを結合しません。対応する形は、単一の
`conversations.json`、または同じZIP内フォルダの `conversations-000.json`、
`conversations-001.json` …です。各ファイルは会話オブジェクトの配列で、番号は0から連続している必要があります。
同一会話をファイル間で分割した形は自動で結合しません。

ZIP内のすべての対象ファイルを1トランザクションで取り込みます。
途中のJSONが壊れていたり、会話IDや番号が重複していたりすると、全体を取り消します。
単一形式との混在、途中の欠番、異なるZIP内フォルダの混在も拒否します。
`conversations` で始まるファイル名は予約し、対応する名前でない場合も拒否します。
元ZIP全体のSHA256を1件の原本として記録します。
末尾の分割ファイルが欠けていることは、連番だけでは検出できません。
ダウンロードしたZIPを加工せず使ってください。入れ子ZIPや複数ZIPの結合は対象外です。

次の例は `C:\Pocket\bag` の外に固有の試験フォルダを作り、**本物のDBとは別の新規DB**を使います。
`$zip` だけ実際に届いたZIPのパスへ置き換えます。試験用DBは元ZIPから作り直せます。

```powershell
$ErrorActionPreference = 'Stop'
$zip = 'D:\exports\chatgpt-export.zip' # 実際の原本ZIPへ変更する
$install = Join-Path $env:LOCALAPPDATA 'Programs\bag'
$trial = Join-Path $env:LOCALAPPDATA ('bag-chatgpt-trials\' + [guid]::NewGuid().ToString('N'))
$trialDb = Join-Path $trial 'chatgpt-test.db'
if (-not (Test-Path -LiteralPath $zip -PathType Leaf)) { throw 'Export ZIP not found' }
if (Test-Path -LiteralPath $trialDb) { throw 'Use a new database path' }
New-Item -ItemType Directory -Path $trial -ErrorAction Stop | Out-Null

dotnet (Join-Path $install 'bag.dll') import $zip --format chatgpt --db $trialDb
if ($LASTEXITCODE -ne 0) { throw 'ChatGPT import failed; do not switch the real database' }

dotnet (Join-Path $install 'bag.dll') find --db $trialDb --speaker chatgpt-assistant --limit 3 --json
if ($LASTEXITCODE -ne 0) { throw 'ChatGPT read failed' }
Write-Output "Trial database: $trialDb"
```

`unresolved_rooms` が0でない場合は、本筋を決められなかった会話があります。
原本は残し、必要なら試験用DBで `find --all-branches` を使って確認します。
試験フォルダは既存DBと別なので、既存の封印・話者設定を共有しません。
実物での取り込み結果を確認するまでは、Claude Desktopの接続先を試験DBへ変えません。
