# 褲鍊解壓 ZipDown

批次解壓從網盤下載、被各種方式偽裝的壓縮檔。Windows 桌面程式（.NET 8 + Avalonia），架構保留日後支援 macOS / Linux。

*Batch-extracts disguised archives (fake MP4s, fake extensions, junk characters in names, split volumes, nested archives). Windows desktop app built with .NET 8 + Avalonia. UI in Traditional Chinese, Simplified Chinese and English.*

## 功能

- **偽裝 MP4**：依 MP4 結構找出影片結束位置，看後面接的是 ZIP / RAR / 7z；檔尾的假標頭不會誤判。真影片不動。
- **偽裝副檔名**：`xxx.7z.jpg`、`xxx.7z.001.TXT` 會先用內容確認是壓縮檔再改名。
- **垃圾字**：檔名裡的「删」等字（出現在任何位置都會去掉），清單可自訂。
- **分卷**：`.part1.rar`、`.7z.001`、`.zip.001`、`.z01`，只從第 1 卷解，其他卷跟著改名。
- **多層解壓**（預設 3 層）：內層的圖片後接 ZIP、沒副檔名的壓縮檔都會處理；內層內容搬到第一層資料夾，撞名加 (2)、(3)…，中間層壓縮檔保留。
- **密碼**
  - 檔名裡的密碼優先試：`xxx.zip密码123`、`xxx.zippw123`、`xxx.7z.jpgpw:123`
  - 資料夾名稱有「密碼／解壓碼／pw」也會抓（繁簡都可以）
  - 密碼本一行一個；成功次數多的排前面，跨次執行有效
  - 先用假密碼測試：能過代表沒加密
- **完成清單**：解過的壓縮檔用「大小＋檔名」記下，重跑時跳過。
- **復原改名**：所有改名都有紀錄，可以一鍵改回去。
- **兩段式停止**：按一次＝目前這個檔做完就停；再按一次＝強制停止，並清掉解一半的資料夾。

## 使用

1. 安裝 [Bandizip](https://www.bandisoft.com/bandizip/) 或 WinRAR（有 Bandizip 優先用；也可以在設定裡指定路徑）。
2. 執行 `ZipDown.exe`，把檔案或資料夾拖進視窗（或按「加入檔案／加入資料夾」）。
3. 按「設定」把密碼一行一個貼進密碼本。
4. 按「開始」。

設定與紀錄都放在程式旁邊（免安裝、可攜）：

| 檔案 | 內容 |
|---|---|
| `settings.ini` | 設定 |
| `passwords.txt` | 密碼本（一行一個） |
| `password_stats.txt` | 每個密碼的成功次數 |
| `done_list.txt` | 已解過的壓縮檔 |
| `rename_history.txt` | 改名紀錄（復原用） |
| `logs/` | 每次執行的詳細紀錄（含每次密碼嘗試與結束代碼） |

公開版不附任何密碼。

## 下載

到 [Releases](https://github.com/pierre654852/ZipDown/releases) 下載 `ZipDown-vX.Y.Z-win-x64.zip`，解開直接執行。
程式沒有數位簽章，第一次執行 Windows 可能跳出 SmartScreen 警告，按「其他資訊 → 仍要執行」。

## 自己編譯

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```
dotnet build
dotnet test
dotnet run --project src/ZipDown.App
```

發佈單一 exe：

```
dotnet publish src/ZipDown.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## 專案結構

```
src/ZipDown.Core      偽裝判斷、檔名規則、密碼本、解壓流程（不碰 UI）
src/ZipDown.Engines   Bandizip、WinRAR（之後加 7-Zip）
src/ZipDown.App       Avalonia 介面
tests/ZipDown.Core.Tests
```

## 已知限制

- 目前只支援 Windows 的 Bandizip / WinRAR。
- 7-Zip 打不開「前面接 MP4」的 zip；之後 macOS / Linux 版會先把壓縮檔那段另存成獨立檔再交給 7-Zip。
- WinRAR 的結束代碼（密碼錯 11）依官方文件，尚未實測。

## 致謝

功能構想參考 smart-unzip-workbench（只參考功能，未使用其程式碼）。

## 授權

[MIT](LICENSE)
