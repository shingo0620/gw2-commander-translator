# GW2 Commander Translator

目前的第一個交付是 **Squad Chat capture spike**：驗證 `Blish HUD + ArcDPS + arcdps-bhud + Unofficial Extras` 能否低延遲、完整地把實際 Squad Chat 傳到 Blish HUD module。

這不是完整翻譯器，也不會讀取 Map Chat、辨識 Commander、發送聊天或操作遊戲。

## 此版本會做什麼

- 顯示 ArcDPS bridge 是否已連線，以及 `SquadMessage` 是否可用。
- 收到 Squad-family message 時，顯示計數、最後訊息的經過時間與最新一行訊息。
- 在設定中提供「Record raw Squad Chat to the Blish HUD log」開關，預設關閉；開啟後才會把原文、角色／帳號與 payload metadata 寫到本機 Blish HUD log。
- 保留 subgroup 與 broadcast metadata，協助驗證 Triple Trouble 實戰中的訊息範圍。

## 不會做什麼

- 不翻譯、不做 tactical intent engine、不顯示正式 HUD。
- 不需要 Nexus；不接收 Map Chat。
- 不需要先辨識發話者是否為 Commander。
- 不存檔 raw chat，除非使用者自行啟用 Blish HUD diagnostic log。

## 安裝前置條件

1. Blish HUD 1.3.0 或相容更新版。
2. ArcDPS。
3. `arcdps-bhud.dll`（ArcDPS Blish HUD Integration v2）。
4. `arcdps_unofficial_extras.dll`（Unofficial Extras）。
5. 此 repo release 或 Actions artifact 內的 `CommanderTranslator.bhm`。

請把 `.bhm` 放到 Blish HUD 的 Modules 資料夾後啟用。ArcDPS DLL 與 Blish HUD 不應放在同一個資料夾。

## 實戰驗證流程

1. 在進入 GW2 且開啟 Blish HUD 後，確認面板顯示 `Bridge: connected | SquadMessage available`。
2. 由可協調的 squad member 依序發送 10–20 則有編號 marker 的 squad message，例如 `CT01 HOLD 5%` 至 `CT20 BURN NOW`。
3. 同步錄下 GW2 chat panel 與 capture panel，核對 sequence、重複、遺失與畫面可見到 HUD 收到的時間差。
4. 測 whole-squad、subgroup、broadcast（若有權限）、離開／重新加入 squad 與 map change。
5. 在下一場 Triple Trouble 保留 capture log，觀察真實指揮 shorthand 是否能完整送達。

### 通過標準

- controlled visible messages 100% 收到，沒有非預期重複。
- 順序正確，或至少可由 metadata 穩定診斷。
- `screen-visible → module received`：p95 ≤ 250 ms、max ≤ 500 ms。
- 角色名、帳號、文字與 subgroup/broadcast metadata 都沒有亂碼或截斷。
- bridge 缺失或 `SquadMessage` 不可用時，面板必須清楚顯示而非靜默無輸出。

## 開發與建置

需要 Windows、Visual Studio 2022（.NET Framework 4.8 targeting pack）與 NuGet。Blish HUD 的 package targets 會在 build 後把 module 打包成 `.bhm`。

```powershell
nuget restore CommanderTranslator.csproj
msbuild CommanderTranslator.csproj /p:Configuration=Release
```

輸出位置：`bin\Release\CommanderTranslator.bhm`。

若要以 Blish HUD 偵錯，請使用：

```text
Blish HUD.exe --debug --module "<repo>\bin\Debug\CommanderTranslator.bhm"
```

## 風險說明

本 module 只重現玩家已可見的 Squad Chat，沒有自動化或遊戲輸入控制；但 ArcDPS 與 Unofficial Extras 都是第三方元件，ArenaNet 未正式核准任何特定第三方工具。使用者應自行理解與承擔第三方工具風險。
