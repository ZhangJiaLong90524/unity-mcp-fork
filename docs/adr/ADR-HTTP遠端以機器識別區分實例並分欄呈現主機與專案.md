---
status: accepted
uid: 3fc04572
date: 2026-10-22
decision-makers: ["fork 維護者"]
---

## HTTP 遠端模式以機器識別區分實例，並把主機標籤、專案名稱與專案識別分欄呈現

### Context and Problem Statement／脈絡與問題陳述

本 fork 的部署是一台共用的 HTTP MCP server，多台 Windows 機器上的 Unity Editor 以 HTTP Remote 範圍、不開認證連回這台 server，由同一個 AI 代理操作所有 Editor。可能同時出現：不同主機連線、不同專案連線、同一專案在多台主機以相同或不同路徑連線，以及同一主機上同一專案的多份 checkout。

上游（到 v10.3.0 與 beta 10.3.1-beta.11 為止）的實例身分只有一個值：`Application.dataPath` 的 SHA1 前 16 碼（專案雜湊）。server 的 `PluginRegistry` 不開認證時只以專案雜湊為鍵，同雜湊的新連線會移除舊 session 並關掉舊 WebSocket；這是為了處理同一台 Editor 在 domain reload 後重連的競態。路由（`unity_instance_middleware`、`custom_tool_service`）只取 `Name@hash` 中 `@` 後的雜湊找連線，名稱只供顯示。因此多台機器把同一專案放在相同路徑時，雜湊相同，後連上者驅逐先連上者，被驅逐的 Editor 自動重連後再驅逐對方；代理鎖定的實例 ID 會隨之改指另一台機器，指令在錯的機器上執行而代理無從察覺。

另一方面，代理看到的資訊不足以判斷「哪台主機、哪個專案、哪一份 checkout」：HTTP 模式的實例清單只有專案名稱與雜湊，不給路徑；fork 先前加入的「每專案自訂顯示名稱」直接取代專案名稱，設了主機名後就看不出專案。

### Considered Options／考量的選項

* 身分分欄模型，HTTP Remote 範圍的路由鍵混入機器識別
* 外層資料夾改成主機專屬名稱（部署端避開碰撞）
* 每專案自訂顯示名稱（取代專案名稱）
* 自訂顯示名稱參與雜湊
* server 端以（機器識別, 專案雜湊）複合鍵允許同雜湊多連線
* 每台機器各用一把 API key（上游既有的 user_id 分範圍）
* server 依連線來源 IP 區分
* 所有 HTTP 範圍的路由鍵都混入機器識別

### Decision Outcome／決策結果

Chosen option: "身分分欄模型，HTTP Remote 範圍的路由鍵混入機器識別"，因為它同時解決碰撞與辨識兩件事，只在會跨機器的範圍改變 ID，`Name@hash` 的格式與「`@` 後即路由鍵」的上游語意都不變，server 不需理解機器識別。

身分模型的各欄位：

| 欄位 | 用途 | 來源 |
|---|---|---|
| 路由鍵（`@` 後段） | server 找連線、判斷同一實例重連時取代舊連線 | HTTP Remote 範圍：機器識別加 Assets 路徑的 SHA1 前 16 碼；其他範圍與 stdio 維持專案雜湊 |
| 專案雜湊 | 本機儲存鍵（EditorPrefs、stdio 狀態檔） | 維持 Assets 路徑的 SHA1 前 16 碼，不混入機器識別 |
| 機器識別 | 只參與路由鍵，不顯示 | `SystemInfo.deviceUniqueIdentifier` 與 `Environment.MachineName` 一起參與，任一相異即可區分 |
| 主機標籤 | 代理辨識主機 | 機器層設定，存本機 EditorPrefs、所有專案共用，預設 `Environment.MachineName`；改標籤不改路由鍵 |
| 專案名稱（`@` 前段） | 代理辨識專案 | 資料夾推得的專案名稱，不再被任何自訂名稱取代 |
| 專案識別 | 判斷不同主機或 checkout 是否為同一專案 | `PlayerSettings.productGUID`（存於進版控的 ProjectSettings，各 clone 相同） |
| 專案路徑 | 分辨同一專案的不同 checkout | 註冊訊息既有的 `project_path`，改為也回給代理 |

連帶決定：

* fork 的「每專案自訂顯示名稱」刪除，設定介面改為主機標籤。
* 註冊訊息新增的主機標籤與專案識別在 server 端為選填，舊版 Editor 仍可連線，只是看不到新欄位。
* 實例清單回傳主機標籤、專案識別與路徑；同一專案識別出現在多個實例時附分組說明，逐筆列出專案名稱、主機與路徑。重名警告只留給分組說明不涵蓋的同名實例（不同專案識別，或舊版 Editor 沒送專案識別）。
* custom tool 不另改：Unity 端經 WebSocket 註冊工具，server 以連線對應 session，自然跟著路由鍵。
* CLI 的命令與 custom tool 路由以雜湊比對完整 `Name@hash`，不再退回比對專案名稱；只給專案名稱且有多個同名實例時回報歧義。
* server 在路由鍵被另一個連線取代時記錄新舊主機標籤：同一主機重連（含兩端都是不回報主機的舊版 Editor）記為 INFO，主機不同記為 WARNING。

#### Consequences／影響後果

* Good, because 同一專案放在多台機器的相同路徑時各有不同路由鍵，不再互相驅逐，也不再把指令送到錯的機器
* Good, because 代理一次看到主機、專案、專案識別與路徑，能分辨四種多實例情境
* Good, because 同一台 Editor 重連時路由鍵不變，上游的重連取代行為完整保留
* Good, because 單機使用者（HTTP Local 與 stdio）的 ID 與行為不變
* Bad, because 既有 HTTP Remote 使用者升級後路由鍵會改變一次，先前記下的 `Name@hash` 失效
* Bad, because 程式碼裡要區分「專案雜湊」與「路由鍵」兩個概念，Unity 與 server 兩端都要改
* Bad, because server 與所有 Editor 都更新後才看得到新欄位；仍跑舊版的 Editor 之間照樣碰撞
* Neutral, because 更換主機板等硬體使 `deviceUniqueIdentifier` 改變時，該機路由鍵跟著改變

#### Confirmation／確認方式

1. Unity EditMode 測試：HTTP Remote 範圍的路由鍵含機器識別、其他範圍等於專案雜湊；改主機標籤不改路由鍵；專案識別取自 `productGUID`。
2. Python 測試：實例清單回傳新欄位；同一專案識別的多個實例有分組說明；缺新欄位的註冊仍被接受。
3. 實機驗收：三台機器以相同路徑開同一專案、同時連同一 server，實例清單列出三筆；對每筆送出回讀主機標籤的指令，結果各自落在對應主機；連續 10 分鐘 server 日誌沒有跨主機驅逐的 WARNING（同一主機重連的 INFO 不算失敗）。

### Pros and Cons of the Options／各選項的優缺點

#### 身分分欄模型，HTTP Remote 範圍的路由鍵混入機器識別

* Good, because 不需要部署端設定就能區分同路徑的機器
* Good, because 主機、專案、路徑各有欄位，彼此不互相覆蓋
* Good, because 範圍限在會跨機器的 HTTP Remote，適合整理成上游 PR
* Bad, because 既有遠端使用者的 ID 變一次

#### 外層資料夾改成主機專屬名稱（部署端避開碰撞）

曾為選中方案，因只避開碰撞而沒有修正身分判定而推翻：每台新機器都要記得改資料夾名，漏改時會靜默打錯機器；代理仍只能從資料夾名猜主機。

* Good, because 不用改程式碼
* Bad, because 碰撞是否發生取決於部署紀律，程式不會警告

#### 每專案自訂顯示名稱（取代專案名稱）

曾為選中方案，因名稱只供顯示、路由仍只看雜湊而推翻：它不解決碰撞，而且設了主機名後代理看不到專案名稱。

* Good, because 代理看得到人取的名稱
* Bad, because 主機與專案擠在同一欄，只能擇一呈現

#### 自訂顯示名稱參與雜湊

* Good, because 預設行為完全不變
* Bad, because 每台仍要各設不同名稱，等於把部署步驟從改資料夾名移到 EditorPrefs
* Bad, because 改名就改身分，與「名稱只供顯示」的上游語意衝突

#### server 端以（機器識別, 專案雜湊）複合鍵允許同雜湊多連線

* Good, because 仿照上游既有的 user_id 複合鍵
* Bad, because 路由只認 `@` 後的雜湊，同雜湊多條連線時 `Name@hash` 指不到單一實例，要另立 ID 格式
* Bad, because custom tool 註冊帶的是 Unity 自算的雜湊，若改由 server 另組 ID 會對不上；補齊後等於本選中方案再加 server 改動

#### 每台機器各用一把 API key（上游既有的 user_id 分範圍）

* Good, because 上游已實作
* Bad, because 開認證後實例清單依呼叫者的 user_id 過濾，代理用一把 key 只看得到一台，與一個代理操作多台的需求相反

#### server 依連線來源 IP 區分

* Good, because 只改 server
* Bad, because server 在容器內時，埠映射可能把來源位址換成閘道位址，多台機器看起來同一個 IP
* Bad, because 路由問題與複合鍵選項相同

#### 所有 HTTP 範圍的路由鍵都混入機器識別

* Good, because 概念一致：實例就是機器加路徑
* Bad, because HTTP Local 只在單機上運作，不會跨機器碰撞，卻讓所有 HTTP 使用者的 ID 都改變

#### 專案識別的其他候選

* git remote 的 repo 名稱：人看得懂，但專案沒有 git 或 remote 名稱不同時失效，因此不選。
* 資料夾名稱：主機專屬資料夾名會讓同一專案被判成不同專案，因此不選。
* `productGUID` 的已知限制：複製資料夾另起的新專案會沿用同一個 GUID，所以分組說明同時列出專案名稱與路徑，不單憑 GUID 下結論。

### More Information／補充資訊

* `PlayerSettings.productGUID` 的 getter 已確認存在於 Unity 2020.3、2021.3、2022.3、6000.3、6000.6 的 `UnityEditor.CoreModule.dll`。
* 上游相關：issue #1023（維護者表示多實例會在後續大版本處理）；PR #981（明確路由與 `/mcp/instance/{Name@hash}` 端點）與 PR #1285（EditorPrefs 依專案雜湊分範圍）都改到本決策會動的程式碼，要等本決策實作後再評估是否整合。
* 送上游 PR 時從上游 `beta` 分出，只帶程式碼提交，不帶本 ADR。
