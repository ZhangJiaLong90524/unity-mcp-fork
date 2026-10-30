---
status: accepted
uid: 33f81c71
date: 2026-10-22
decision-makers: ["fork 維護者"]
---

## fork 以 develop 整合上游 beta、他人 PR 與自有功能，上游 PR 分支在送出時才從 beta 建立

### Context and Problem Statement／脈絡與問題陳述

本 fork 同時有兩個用途：自己日常使用（含上游尚未合併的他人 PR 與 fork 自有功能），以及把自有功能整理成 PR 送回上游。上游的開發主線是 `beta`，PR 一律以 `beta` 為目標；`main` 是發版線，上游大約三週就累積上百筆提交。

先前 fork 的整合線沿用第一個功能的分支名，基底是上游發版點而非 `beta`，後續功能與 fork 專屬文件都直接疊在上面。結果整合線名稱不再反映內容，基底落後 `beta`，也沒有一條分支能直接用來送 PR。

### Considered Options／考量的選項

* `develop` 整合線以 `beta` 為基底，功能分支從 `develop` 分出，PR 分支在送出時才從 `beta` 建立
* fork 自有提交直接放在 `beta`
* 整合線跟隨上游發版線 `main`
* 功能分支一律從 `beta` 分出並持續 rebase，隨時可直接送 PR
* 整合線沿用第一個功能的分支名

### Decision Outcome／決策結果

Chosen option: "`develop` 整合線以 `beta` 為基底，功能分支從 `develop` 分出，PR 分支在送出時才從 `beta` 建立"，因為它讓自用的整合與送上游的整理各走各的分支：自用分支照一般的主樹、任務樹流程運作，送 PR 時才付整理成本，而且付的是當下最新 `beta` 的成本。

各分支的角色：

| 分支 | 角色 | 規則 |
|---|---|---|
| `beta` | 上游 `beta` 的鏡像 | 只快轉，不放任何 fork 提交 |
| `develop` | 整合線，主工作樹檢出這條 | 以合併提交收入上游 `beta`、他人 PR 與 fork 功能分支；不壓縮、不 rebase；永遠不作為 PR 來源 |
| `feat/<描述>`（或 `fix/<描述>`） | fork 功能開發 | 在各自的任務樹中從 `develop` 分出；合進 `develop` 後凍結，不再 rebase |
| PR 分支 | 送上游 | 送出時從最新的 `beta` 建立，cherry-pick 或 rebase 功能分支的提交後整理；不帶 `docs/adr/` 等 fork 專屬文件；PR 結束後刪除 |

他人的 PR 以合併原分支的方式收入 `develop`，不 cherry-pick：保留原作者的提交雜湊，上游日後以合併提交收下該 PR 時，git 能認出是同一批提交。

`develop` 另有一份鏡像放在工作用的私有 GitLab，供不能依賴個人 GitHub 的專案取用 Unity 套件與 server。鏡像只收 `develop` 與 tag，不在鏡像上提交。GitHub fork 是來源：`develop` 的新提交先推到 GitHub fork，再由能同時連到兩個遠端的工作機從 GitHub fork 拉下，以同一個提交只快轉推到鏡像，不強推。只能連 GitHub 的家用機只推 GitHub fork，推完經跨主機通訊通知完整雜湊，由工作機對齊鏡像。

#### Consequences／影響後果

* Good, because `beta` 保持可快轉，同步上游只需快轉 `beta` 再把它合進 `develop`
* Good, because 功能分支照一般的任務樹流程開發與收合，不必為了送 PR 反覆 rebase
* Good, because PR 分支建立在送出當下的 `beta` 上，不會因上游前進而提早過時
* Bad, because 送 PR 時要另外整理一次提交；fork 版本與上游版本內容不同時（例如 fork 刪除上游沒有的功能），PR 版本要改寫
* Bad, because `develop` 跟隨預發布的 `beta`，會一併承接上游 `beta` 的回歸問題；合併 `beta` 後要先跑測試再讓下游使用

#### Confirmation／確認方式

1. `git log --no-merges origin/beta..beta` 沒有輸出（`beta` 沒有 fork 提交）。
2. `develop` 上合併 `origin/beta` 與功能分支的提交都是雙親合併提交。
3. 送出的 PR 分支以 `origin/beta` 為基底，`git diff origin/beta...<PR 分支> --name-only` 不含 `docs/adr/`。
4. 工作機對齊鏡像後，GitHub fork 與 GitLab 鏡像的 `develop` 頂端是同一個提交。

### Pros and Cons of the Options／各選項的優缺點

#### `develop` 整合線以 `beta` 為基底，PR 分支送出時才建立

* Good, because 自用與送上游分開，各自的規則簡單
* Good, because 整合線與上游 PR 的目標是同一個基底，PR 分支多半只要一次乾淨的 rebase
* Bad, because 每個要送的功能都多一次整理

#### fork 自有提交直接放在 `beta`

* Good, because 少一條分支
* Bad, because `beta` 一旦有 fork 提交就無法快轉同步上游
* Bad, because 從 `beta` 分出的 PR 分支會夾帶 fork 專屬提交

#### 整合線跟隨上游發版線 `main`

* Good, because 下游拿到的是上游已發版的程式碼
* Bad, because 功能分支與 PR 的基底是 `beta`，合進以 `main` 為基底的整合線時會一併拖進尚未發版的 `beta` 提交；要避免就得同一功能各做一份

#### 功能分支一律從 `beta` 分出並持續 rebase

* Good, because 任何時候都能直接送 PR
* Bad, because 已合進 `develop` 的分支再 rebase 會產生雜湊不同的重複提交，再合一次會在 `develop` 留下兩份並可能產生假衝突
* Bad, because 上游前進快，提早整理的 PR 分支到送出時多半要再 rebase 一次
* Bad, because PR 版本常與自用版本不同，維持同一條分支兩種用途會互相牽制

#### 整合線沿用第一個功能的分支名

曾為實際做法，因分支名不再反映內容、基底落後 `beta` 而推翻：讀者看名稱會以為它只有一個功能，而它其實收了多個功能與 fork 專屬文件。

* Good, because 不用改名
* Bad, because 名稱誤導，且沒有一條分支能直接作為 PR 基底
