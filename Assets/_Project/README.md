# Morld 目前使用的內容

- 主場景：`Assets/Scenes/SampleScene.unity`（目前唯一啟用建置的場景）。
- 玩家出生物件：SampleScene 內的 `PlayerSpawnPoint`。
- 主玩家預製物件：`Assets/_Project/Prefabs/Player/MorldPlayer.prefab`。
- 執行時生成的玩家：`MorldPlayer(Clone)`。
- 玩家移動、跳躍、落地、繞行與下落：`Scripts/Gameplay/PlayerController.cs`。
- 按鍵顯示、Bug 標記與返回平台：`Scripts/Debug/PlayerDebugTools.cs`。
- 主關卡預製物件：`Prefabs/levels/Test Level/Test Level 1.prefab`。

`MorldPlayer` 是目前主場景使用的玩家。
舊場景與其他玩家方案集中在 `Legacy`，停用與恢復方式請看 `Legacy/README.md`。
