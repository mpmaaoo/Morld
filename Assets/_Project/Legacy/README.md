# 舊版與試作（預設停用）

目前主場景是 `Assets/Scenes/SampleScene.unity`，主玩家是
`Assets/_Project/Prefabs/Player/MorldPlayer.prefab`，移動由 `PlayerController` 負責。

本資料夾保留過去的場景與玩家預製物件，未加入遊戲建置：

- `Scenes/SampleScene 1.unity`：舊場景副本。
- `Scenes/GridProjectionTest.unity`：格子投影替代方案測試場景。
- `Prefabs/Player.prefab`：未被目前主場景引用的玩家版本。
- `Prefabs/GridTestPlayer.prefab`：格子投影方案的測試角色。

移動時保留了原有 `.meta` 與 GUID，舊場景內的物件引用仍可解析。

## 停用方式與恢復

`FezGridTestRig.Start` 的舊試作啟動內容以 `MORLD_LEGACY_TESTS` 條件編譯停用。
`FezGridTestPlayer` 預設停用自身，不讀取移動或旋轉輸入。
若要重新使用這套方案，請在 Unity 的 Scripting Define Symbols 加入
`MORLD_LEGACY_TESTS`，再開啟本資料夾中的 `GridProjectionTest` 場景。
測試結束後移除此符號。

## 仍共用的內容

`Scripts/Gameplay/Fez/Grid` 內的方塊標記與資料類別仍保留。
主場景的方塊與 Block Map 編輯工具仍會使用部分 Grid 類別。
`FezGridMap`、`FezGridProjection`、`FezGridShell` 是舊方案的運算程式，
目前沒有由 SampleScene 啟動。

`FezPlayerDepthSnapper` 與 `PlayerDepthProxySideProbe` 保留作舊資源相容用途；
目前玩家由 `PlayerController` 停用這兩個舊控制元件。
主場景使用的 `FezDepthProxyFollower`、平台可見性、四向視角及關卡編輯工具仍保留。

尚未完成的幻影、任務、對話等功能不在本次舊移動方案整理範圍內。
